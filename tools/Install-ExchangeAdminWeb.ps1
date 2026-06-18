<#
.SYNOPSIS
    Installs or updates ExchangeAdminWeb in a new IIS environment.

.DESCRIPTION
    Product installer for ExchangeAdminWeb. This script is intentionally separate
    from ADI deployment helpers. It uses generic Windows/IIS defaults, prompts for
    environment-specific values, and preserves existing appsettings.json and config
    fragments unless explicitly told to regenerate appsettings.json.

    Fresh install:
      - Builds the app from the current source tree.
      - Creates/configures an IIS app pool and web application.
      - Generates appsettings.json.
      - Seeds required config fragments if missing.
      - Sets ACLs for publish, config, and log folders.

    Update:
      - Builds the app from the current source tree.
      - Copies binaries/static files while preserving appsettings.json, config, and logs.
      - Seeds only newly missing config fragments.

.EXAMPLE
    .\tools\Install-ExchangeAdminWeb.ps1

.EXAMPLE
    .\tools\Install-ExchangeAdminWeb.ps1 -NonInteractive `
        -AdminGroups "CONTOSO\Exchange Admins" `
        -ContactEmail "it-admins@contoso.com" `
        -AdminNotificationEmail "it-admins@contoso.com"

.EXAMPLE
    .\tools\Install-ExchangeAdminWeb.ps1 -PlanOnly
#>

[CmdletBinding()]
param(
    [string]$ParentSite = "Default Web Site",
    [string]$AppAlias = "ExchangeAdminWeb",
    [string]$AppPoolName = "ExchangeAdminWeb",
    [string]$PublishPath,
    [string]$LogRoot,
    [string]$PathBase,

    [string]$ServiceAccount,
    [securestring]$ServiceAccountPassword,

    [string[]]$AdminGroups,
    [string[]]$AllowedGroups,
    [string]$DomainPrefix,

    [string]$ApplicationName = "IT Admin Portal",
    [string]$ContactEmail,

    [string]$SmtpHost = "localhost",
    [int]$SmtpPort = 25,
    [switch]$SmtpUseSsl,
    [string]$FromAddress,
    [string]$FromName = "IT Admin Portal",
    [string]$AdminNotificationEmail,

    [string]$DelineaUrl,
    [string]$DelineaCredentialTarget = "Delinea_Client",
    [string]$OnPremExchangeServerUri,
    [string]$CertSubject,

    [switch]$RegenerateAppSettings,
    [switch]$PlanOnly,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "PLAN $Message" -ForegroundColor DarkGray }
function Write-Fail { param([string]$Message) throw $Message }

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Read-Value {
    param(
        [string]$Name,
        [string]$Prompt,
        [string]$CurrentValue,
        [string]$DefaultValue
    )

    if ($CurrentValue) { return $CurrentValue }
    if ($NonInteractive) { return $DefaultValue }

    $effectiveDefault = $DefaultValue
    $display = if ($effectiveDefault) { "$Prompt [$effectiveDefault]" } else { $Prompt }
    $value = Read-Host -Prompt $display
    if ([string]::IsNullOrWhiteSpace($value)) { return $effectiveDefault }
    return $value.Trim()
}

function Read-StringArray {
    param(
        [string]$Prompt,
        [string[]]$CurrentValue,
        [string[]]$DefaultValue
    )

    if ($CurrentValue -and $CurrentValue.Count -gt 0) { return $CurrentValue }
    if ($NonInteractive) { return $DefaultValue }

    $defaultText = if ($DefaultValue -and $DefaultValue.Count -gt 0) { $DefaultValue -join ", " } else { "" }
    $display = if ($defaultText) { "$Prompt [$defaultText]" } else { $Prompt }
    $value = Read-Host -Prompt $display
    if ([string]::IsNullOrWhiteSpace($value)) { return $DefaultValue }

    return @($value -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Read-PasswordIfNeeded {
    param([string]$Prompt)
    if ($ServiceAccountPassword) { return $ServiceAccountPassword }
    if ($NonInteractive) { return $null }
    return Read-Host -Prompt $Prompt -AsSecureString
}

function ConvertTo-PlainText {
    param([securestring]$Secure)
    if (-not $Secure) { return $null }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Normalize-Groups {
    param([string[]]$Groups, [string]$Prefix)

    $normalized = @()
    foreach ($group in ($Groups | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $trimmed = $group.Trim()
        if ($trimmed -like "*\*") {
            $normalized += $trimmed
        } elseif ($Prefix) {
            $normalized += "$Prefix\$trimmed"
        } else {
            Write-Fail "Group '$trimmed' is not domain-qualified. Use DOMAIN\GroupName or provide -DomainPrefix."
        }
    }
    return @($normalized)
}

function Assert-SafeDirectoryPath {
    param([string]$Path, [string]$Name)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        Write-Fail "$Name is required."
    }

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\', '/')
    if ($full.TrimEnd('\', '/') -eq $root) {
        Write-Fail "$Name cannot be a drive root: $full"
    }
    return $full.TrimEnd('\', '/')
}

function Invoke-PlanOrAction {
    param([string]$Description, [scriptblock]$Action)

    if ($PlanOnly) {
        Write-Plan $Description
        return
    }

    & $Action
}

function Set-DirectoryAcl {
    param([string]$Path, [string]$Identity, [string]$Rights)

    Invoke-PlanOrAction "Grant $Identity $Rights on $Path" {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        }
        # icacls is native: $ErrorActionPreference="Stop" won't catch its failure and
        # "| Out-Null" hides the error, so an unchecked grant fails silently. Convert a
        # nonzero exit to a throw (icacls returns 0 on success).
        & icacls $Path /grant "${Identity}:$Rights" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "icacls failed (exit $LASTEXITCODE) granting $Identity ${Rights} on $Path"
        }
    }
}

function Write-JsonFileIfMissing {
    param([string]$Path, $Value, [string]$Description)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Ok "$Description exists - preserving"
        return
    }

    Invoke-PlanOrAction "Create $Description at $Path" {
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json | Out-Null
    }
}

function Write-AppSettings {
    param([string]$Path, [hashtable]$Settings)

    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and -not $RegenerateAppSettings) {
        Write-Ok "appsettings.json exists - preserving"
        return
    }

    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and $RegenerateAppSettings) {
        $backup = "$Path.bak.$(Get-Date -Format 'yyyyMMddHHmmss')"
        Invoke-PlanOrAction "Back up existing appsettings.json to $backup" {
            Copy-Item -LiteralPath $Path -Destination $backup -Force
        }
    }

    Invoke-PlanOrAction "Write appsettings.json at $Path" {
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $Settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json | Out-Null
    }
}

function Invoke-RobocopyChecked {
    param([string[]]$Arguments, [string]$Description)

    if ($PlanOnly) {
        Write-Plan "robocopy $($Arguments -join ' ')"
        return
    }

    Write-Step $Description
    & robocopy @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ge 8) {
        Write-Fail "robocopy failed with exit code $exitCode during: $Description"
    }
    Write-Ok "$Description completed (robocopy exit $exitCode)"
}

function Write-DeploymentManifest {
    param([string]$PublishFolder, [string]$RepoRoot)

    $dllPath = Join-Path $PublishFolder "ExchangeAdminWeb.dll"
    $version = if (Test-Path -LiteralPath $dllPath -PathType Leaf) {
        (Get-Item $dllPath | Select-Object -ExpandProperty VersionInfo).FileVersion
    } else { "unknown" }

    $gitCommit = try { (git -C $RepoRoot rev-parse HEAD 2>$null) } catch { "unknown" }
    $gitBranch = try { (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null) } catch { "unknown" }
    $gitDirty = try { [bool](git -C $RepoRoot status --porcelain 2>$null) } catch { $false }

    $manifest = [ordered]@{
        version = ($version -split '\.')[0..2] -join '.'
        gitCommit = $gitCommit
        gitBranch = $gitBranch
        gitDirty = $gitDirty
        buildTime = (Get-Date).ToUniversalTime().ToString("o")
        repoPath = $RepoRoot
        installer = "Install-ExchangeAdminWeb.ps1"
    }

    $path = Join-Path $PublishFolder "deployment-manifest.json"
    Invoke-PlanOrAction "Write deployment manifest at $path" {
        $manifest | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    }
}

function Get-ConfigurablePolicyAliases {
    return @(
        "MailboxPermissions",
        "MailboxPermissionsOnPrem",
        "CalendarPermissions",
        "CalendarPermissionsOnPrem",
        "MigrationCheck",
        "MigrationCreate",
        "MigrationManage",
        "ConferenceRooms",
        "DelegationReport",
        "LicensingUpdates",
        "MessageTrace",
        "RecipientLookup",
        "OutOfOffice",
        "GroupManagement",
        "GroupManagementOnPrem",
        "M365GroupManagement",
        "Comms10k",
        "ADAttributeEditor",
        "ADAttributeEditorLevel1",
        "ADAttributeEditorLevel2",
        "ADAttributeEditorLevel3",
        "MfaReset",
        "NamedLocations",
        "EmergencyDisable",
        "DhcpAuthorization",
        "EventLog",
        "UndoAuditedActions"
    )
}

function New-SectionAccessSeed {
    param([string[]]$Groups)

    $sectionAccess = [ordered]@{}
    foreach ($alias in Get-ConfigurablePolicyAliases) {
        $sectionAccess[$alias] = @($Groups)
    }

    return [ordered]@{
        Security = [ordered]@{
            SectionAccess = $sectionAccess
        }
    }
}

function New-ModuleEnablementSeed {
    return [ordered]@{
        ExchangeOnline = $false
        MailboxPermissions = $true
        CalendarPermissions = $true
        Migration = $true
        ConferenceRooms = $false
        DelegationReport = $true
        LicensingUpdates = $false
        MessageTrace = $true
        RecipientLookup = $true
        OutOfOffice = $true
        GroupManagement = $false
        M365GroupManagement = $false
        Comms10k = $false
        ADAttributeEditor = $false
        MfaReset = $false
        NamedLocations = $false
        EmergencyDisable = $false
        DhcpAuthorization = $false
        AdminEventLog = $true
    }
}

function New-ProtectedPrincipalsSeed {
    return [ordered]@{
        ProtectedPrincipals = [ordered]@{
            Users = @()
            Groups = @()
            OrganizationalUnits = @()
            SamAccountNamePatterns = @()
        }
    }
}

function New-AdEditableAttributesSeed {
    return [ordered]@{
        Attributes = @()
    }
}

function New-AdEditableAttributesLegendSeed {
    return [ordered]@{}
}

function New-AppSettingsObject {
    param(
        [string]$Name,
        [string]$BasePath,
        [string]$LogPath,
        [string[]]$SecurityAllowedGroups,
        [string[]]$SecurityAdminGroups
    )

    return [ordered]@{
        Serilog = [ordered]@{
            MinimumLevel = [ordered]@{
                Default = "Information"
                Override = [ordered]@{
                    "Microsoft.AspNetCore" = "Warning"
                    "Microsoft.Hosting.Lifetime" = "Information"
                }
            }
        }
        ExchangeOnline = [ordered]@{
            AppId = ""
            Organization = ""
            CertificateSubject = $CertSubject
        }
        Audit = [ordered]@{
            LogRoot = $LogPath
            RotationPeriod = "daily"
        }
        OperationTrace = [ordered]@{
            Enabled = $true
        }
        Email = [ordered]@{
            SmtpHost = $SmtpHost
            SmtpPort = $SmtpPort
            SmtpUsername = ""
            SmtpPassword = ""
            SmtpUseSsl = [bool]$SmtpUseSsl
            FromAddress = $FromAddress
            FromName = $FromName
            AdminNotificationEmail = $AdminNotificationEmail
            NotifyUsersOnPermissionGrant = $false
        }
        Security = [ordered]@{
            AllowedGroups = @($SecurityAllowedGroups)
            AdminGroups = @($SecurityAdminGroups)
        }
        ServiceNow = [ordered]@{
            Enabled = $false
            InstanceUrl = ""
            Username = ""
            Password = ""
        }
        Delinea = [ordered]@{
            SecretServerUrl = $DelineaUrl
            CredentialTarget = $DelineaCredentialTarget
        }
        OnPremExchange = [ordered]@{
            ServerUri = $OnPremExchangeServerUri
        }
        Application = [ordered]@{
            Name = $Name
            PathBase = $BasePath
            ContactEmail = $ContactEmail
        }
        AllowedHosts = "*"
    }
}

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$defaultPublishPath = Join-Path $env:SystemDrive "inetpub\$AppAlias"
$defaultLogRoot = Join-Path $env:ProgramData "ExchangeAdminWeb\Logs"

$PublishPath = Read-Value "PublishPath" "Publish path" $PublishPath $defaultPublishPath
$LogRoot = Read-Value "LogRoot" "Audit/log root" $LogRoot $defaultLogRoot
$PathBase = Read-Value "PathBase" "Application path base" $PathBase "/$AppAlias"
$ContactEmail = Read-Value "ContactEmail" "Support/contact email shown in the app" $ContactEmail ""
$AdminNotificationEmail = Read-Value "AdminNotificationEmail" "Admin notification email" $AdminNotificationEmail $ContactEmail
$FromAddress = Read-Value "FromAddress" "Notification from address" $FromAddress $AdminNotificationEmail
$DelineaUrl = Read-Value "DelineaUrl" "Delinea Secret Server URL (blank to configure later)" $DelineaUrl ""
$OnPremExchangeServerUri = Read-Value "OnPremExchangeServerUri" "On-prem Exchange PowerShell URI (blank to configure later)" $OnPremExchangeServerUri ""
$CertSubject = Read-Value "CertSubject" "EXO certificate subject (blank to configure later in Exchange Online module)" $CertSubject ""

$AdminGroups = Read-StringArray "Admin AD groups (comma-separated, DOMAIN\GroupName)" $AdminGroups @()
if (-not $AdminGroups -or $AdminGroups.Count -eq 0) {
    Write-Fail "At least one admin group is required."
}

$AllowedGroups = Read-StringArray "Default app access AD groups (comma-separated, blank = admin groups)" $AllowedGroups $AdminGroups
$adminGroupsQualified = Normalize-Groups -Groups $AdminGroups -Prefix $DomainPrefix
$allowedGroupsQualified = Normalize-Groups -Groups $AllowedGroups -Prefix $DomainPrefix

if (-not $AdminNotificationEmail) { $AdminNotificationEmail = $ContactEmail }
if (-not $FromAddress) { $FromAddress = $AdminNotificationEmail }

$PublishPath = Assert-SafeDirectoryPath $PublishPath "PublishPath"
$LogRoot = Assert-SafeDirectoryPath $LogRoot "LogRoot"
$configPath = Join-Path $PublishPath "appsettings.json"
$configDir = Join-Path $PublishPath "config"
$appLogDir = Join-Path $PublishPath "logs"
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$stagingPath = "$PublishPath.staging.$timestamp"

Write-Host ""
Write-Host "ExchangeAdminWeb installer" -ForegroundColor Magenta
Write-Host "  Source      : $repoRoot" -ForegroundColor DarkGray
Write-Host "  Site        : $ParentSite/$AppAlias" -ForegroundColor DarkGray
Write-Host "  App pool    : $AppPoolName" -ForegroundColor DarkGray
Write-Host "  Publish     : $PublishPath" -ForegroundColor DarkGray
Write-Host "  PathBase    : $PathBase" -ForegroundColor DarkGray
Write-Host "  Log root    : $LogRoot" -ForegroundColor DarkGray
Write-Host "  Mode        : $(if ($PlanOnly) { 'PLAN ONLY' } else { 'APPLY' })" -ForegroundColor DarkGray
Write-Host ""

if (-not $PlanOnly -and -not (Test-IsAdministrator)) {
    Write-Fail "Run this installer from an elevated PowerShell session, or use -PlanOnly for a non-elevated preview."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail "dotnet CLI was not found. Install the .NET SDK before running the installer."
}

if ($PlanOnly) {
    Write-Plan "Import WebAdministration and verify IIS site '$ParentSite'"
} else {
    Import-Module WebAdministration -ErrorAction Stop
    if (-not (Get-Website -Name $ParentSite -ErrorAction SilentlyContinue)) {
        Write-Fail "IIS site '$ParentSite' was not found."
    }
}

$appPoolIdentity = if ($ServiceAccount) { $ServiceAccount } else { "IIS AppPool\$AppPoolName" }
$poolExists = if ($PlanOnly) { $false } else { Test-Path "IIS:\AppPools\$AppPoolName" }
$needsPassword = $false
if ($ServiceAccount -and -not $PlanOnly) {
    if (-not $poolExists) {
        $needsPassword = $true
    } else {
        $existingUser = (Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel).userName
        if ($existingUser -ne $ServiceAccount) {
            $needsPassword = $true
        }
    }
}

if ($needsPassword) {
    $ServiceAccountPassword = Read-PasswordIfNeeded "Enter password for $ServiceAccount"
    if (-not $ServiceAccountPassword) {
        Write-Fail "ServiceAccountPassword is required for a new or changed custom app pool identity."
    }
}

Invoke-PlanOrAction "Create or update IIS app pool $AppPoolName" {
    if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
        New-WebAppPool -Name $AppPoolName | Out-Null
    }

    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value "AlwaysRunning"

    if ($ServiceAccount) {
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 3
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName -Value $ServiceAccount
        if ($ServiceAccountPassword) {
            Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value (ConvertTo-PlainText $ServiceAccountPassword)
        }
    } else {
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 4
    }
}

Set-DirectoryAcl -Path $PublishPath -Identity $appPoolIdentity -Rights "(OI)(CI)M"
Set-DirectoryAcl -Path $LogRoot -Identity $appPoolIdentity -Rights "(OI)(CI)M"
Set-DirectoryAcl -Path $configDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"
Set-DirectoryAcl -Path $appLogDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"

if ($CertSubject) {
    Invoke-PlanOrAction "Grant $appPoolIdentity read access to certificate private key matching $CertSubject if present" {
        $cert = Get-ChildItem Cert:\LocalMachine\My |
            Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
            Sort-Object NotBefore -Descending |
            Select-Object -First 1

        if (-not $cert) {
            Write-Warn "Certificate '$CertSubject' not found in LocalMachine\My - configure certificate ACL later if Exchange Online is used."
        } else {
            $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
            $keyName = $rsa.Key.UniqueName
            $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyName
            if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
                $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyName
            }
            if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
                & icacls $keyPath /grant "${appPoolIdentity}:(R)" | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Fail "icacls failed (exit $LASTEXITCODE) granting $appPoolIdentity (R) on $keyPath"
                }
                Write-Ok "Certificate private key ACL set"
            } else {
                Write-Warn "Certificate private key file could not be located."
            }
        }
    }
}

Write-AppSettings -Path $configPath -Settings (New-AppSettingsObject `
    -Name $ApplicationName `
    -BasePath $PathBase `
    -LogPath $LogRoot `
    -SecurityAllowedGroups $allowedGroupsQualified `
    -SecurityAdminGroups $adminGroupsQualified)

Write-JsonFileIfMissing -Path (Join-Path $configDir "sectionaccess.json") -Value (New-SectionAccessSeed -Groups $adminGroupsQualified) -Description "section access config"
Write-JsonFileIfMissing -Path (Join-Path $configDir "modules-enabled.json") -Value (New-ModuleEnablementSeed) -Description "module enablement config"
Write-JsonFileIfMissing -Path (Join-Path $configDir "protected-principals.json") -Value (New-ProtectedPrincipalsSeed) -Description "protected principals config"
Write-JsonFileIfMissing -Path (Join-Path $configDir "ad-editable-attributes.json") -Value (New-AdEditableAttributesSeed) -Description "AD editable attributes allowlist"
Write-JsonFileIfMissing -Path (Join-Path $configDir "ad-editable-attributes-legend.json") -Value (New-AdEditableAttributesLegendSeed) -Description "AD editable attributes legend"

$protectedPrincipalsModuleConfig = [ordered]@{
    DirectoryReadSecretId = ""
}
Write-JsonFileIfMissing -Path (Join-Path $configDir "module-config-ProtectedPrincipals.json") -Value $protectedPrincipalsModuleConfig -Description "protected principals module config"

Invoke-PlanOrAction "Create or update IIS web application $ParentSite/$AppAlias" {
    if (-not (Test-Path -LiteralPath $PublishPath -PathType Container)) {
        New-Item -ItemType Directory -Path $PublishPath -Force | Out-Null
    }

    $existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue
    if ($existingApp) {
        Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath -Value $PublishPath
        Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
    } else {
        New-WebApplication -Name $AppAlias -Site $ParentSite -PhysicalPath $PublishPath -ApplicationPool $AppPoolName | Out-Null
    }

    $location = "$ParentSite/$AppAlias"
    Set-WebConfigurationProperty -Filter "system.webServer/security/authentication/windowsAuthentication" -Name enabled -Value $true -PSPath "IIS:\" -Location $location
    Set-WebConfigurationProperty -Filter "system.webServer/security/authentication/windowsAuthentication" -Name useKernelMode -Value $false -PSPath "IIS:\" -Location $location
    Set-WebConfigurationProperty -Filter "system.webServer/security/authentication/windowsAuthentication" -Name useAppPoolCredentials -Value $true -PSPath "IIS:\" -Location $location
    Remove-WebConfigurationProperty -Filter "system.webServer/security/authentication/windowsAuthentication/providers" -Name "." -PSPath "IIS:\" -Location $location -AtElement @{ value = "Negotiate" } -ErrorAction SilentlyContinue
    Set-WebConfigurationProperty -Filter "system.webServer/security/authentication/anonymousAuthentication" -Name enabled -Value $false -PSPath "IIS:\" -Location $location
}

Invoke-PlanOrAction "dotnet publish to staging: $stagingPath" {
    Push-Location $repoRoot
    try {
        dotnet publish -c Release -o $stagingPath --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "dotnet publish failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }
}

Invoke-PlanOrAction "Stop app pool $AppPoolName" {
    try { Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop } catch {}
    Start-Sleep -Seconds 2
}

try {
    Invoke-RobocopyChecked -Description "Deploy application files" -Arguments @(
        $stagingPath, $PublishPath,
        "/MIR",
        "/XF", "appsettings*.json",
        "/XD", "logs", "config",
        "/NFL", "/NDL", "/NJH", "/NJS", "/R:2", "/W:1"
    )
} finally {
    Invoke-PlanOrAction "Start app pool $AppPoolName" {
        Start-WebAppPool -Name $AppPoolName
    }
}

if (-not $PlanOnly) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $repoRoot

if ($PlanOnly) {
    Write-Plan "Check HTTPS binding on '$ParentSite'"
} else {
    $httpsBinding = Get-WebBinding -Name $ParentSite -Protocol https -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $httpsBinding) {
        Write-Warn "No HTTPS binding found on '$ParentSite'. Configure HTTPS before production use."
    }
}

Write-Host ""
if ($PlanOnly) {
    Write-Warn "Plan only. No changes were made."
} else {
    Write-Ok "Install/update complete."
}
Write-Host "Open: https://<server>$PathBase" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Sign in as a member of: $($adminGroupsQualified -join ', ')" -ForegroundColor Yellow
Write-Host "  2. Open Admin Settings to enable only the modules you intend to use." -ForegroundColor Yellow
Write-Host "  3. Open Module Config for each enabled module and enter its Delinea, Graph, Exchange, or AD settings." -ForegroundColor Yellow
Write-Host "  4. Configure protected principals before enabling modules that mutate user accounts or permissions." -ForegroundColor Yellow

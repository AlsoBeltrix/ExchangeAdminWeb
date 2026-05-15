#Requires -RunAsAdministrator

param(
    [string]$ParentSite          = "Default Web Site",
    [string]$AppAlias            = "ExchangeAdminWeb",
    [string]$AppPoolName         = "ExchangeAdminWeb",
    [string]$ServiceAccount,
    [securestring]$ServiceAccountPassword,
    [string]$PublishPath         = "D:\inetpub\ExchangeAdminWeb",
    [string]$LogRoot             = "E:\WWWOutput",
    [string]$CertSubject         = "CN=EXO-Automation",

    # Config values (used on fresh install; ignored on upgrade unless -Force)
    [string]$EXOAppId,
    [string]$EXOOrganization,
    [string]$OnPremServerUri,
    [string]$OnPremTargetDomain,
    [string]$DelineaUrl,
    [int]$DelineaSecretId,
    [string]$SmtpHost,
    [string]$AdminEmail,
    [string[]]$AllowedGroups,
    [string[]]$AdminGroups,
    [string]$DomainPrefix        = "DOMAIN",
    [string]$PathBase,
    [string]$OnPremTargetDAG     = "DAG2019",
    [string]$CloudTargetDomain,
    [string]$HybridEndpoint      = "hybrid1",

    [switch]$Force,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

# --- Helpers ---

function Write-Step    { param($m) Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Success { param($m) Write-Host " OK  $m" -ForegroundColor Green }
function Write-Warn    { param($m) Write-Host "  !  $m" -ForegroundColor Yellow }
function Write-Fail    { param($m) Write-Host "  X  $m" -ForegroundColor Red; exit 1 }

function Prompt-Value {
    param([string]$Name, [string]$Prompt, [string]$Default)
    if ($NonInteractive) { return $Default }
    $display = if ($Default) { "$Prompt [$Default]" } else { $Prompt }
    $value = Read-Host -Prompt $display
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value
}

function Prompt-SecureValue {
    param([string]$Prompt)
    if ($NonInteractive) { return $null }
    return Read-Host -Prompt $Prompt -AsSecureString
}

function SecureString-ToPlain {
    param([securestring]$Secure)
    [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure))
}

$ProjectRoot = $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMddHHmmss'

if (-not $PathBase) { $PathBase = "/$AppAlias" }
$PublishPath = $PublishPath.TrimEnd('\', '/')

# --- Detect mode ---

$configPath = Join-Path $PublishPath "appsettings.json"
$isUpgrade = (Test-Path $configPath) -and (-not $Force)

$BackupDir = Join-Path $LogRoot "ExchangeAdminWeb\ConfigBackups"
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }

if ($Force -and (Test-Path $configPath)) {
    $backupName = "appsettings.${timestamp}.pre-force.bak"
    Copy-Item $configPath (Join-Path $BackupDir $backupName)
    Write-Warn "Existing config backed up to $BackupDir\$backupName"
}

$mode = if ($isUpgrade) { "UPGRADE" } else { "INSTALL" }

Write-Host ""
Write-Host "ExchangeAdminWeb - Deploy ($mode)" -ForegroundColor Magenta
Write-Host "  Source  : $ProjectRoot" -ForegroundColor DarkGray
Write-Host "  Publish : $PublishPath" -ForegroundColor DarkGray
Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray
Write-Host ""

Import-Module WebAdministration -ErrorAction Stop

# --- Collect & validate config (fresh install only) ---

if (-not $isUpgrade) {
    Write-Step "Collecting configuration values"

    $requiredParams = @{}

    if (-not $EXOAppId)          { $EXOAppId          = Prompt-Value "EXOAppId"          "Azure App Registration ID (GUID)" }
    if (-not $EXOOrganization)   { $EXOOrganization   = Prompt-Value "EXOOrganization"   "Exchange Online org (e.g. contoso.onmicrosoft.com)" }
    if (-not $OnPremServerUri)   { $OnPremServerUri   = Prompt-Value "OnPremServerUri"   "On-prem Exchange PowerShell URI (e.g. http://exch01.domain.com/PowerShell/)" }
    if (-not $OnPremTargetDomain){ $OnPremTargetDomain = Prompt-Value "OnPremTargetDomain" "On-prem email domain (e.g. yourcompany.com)" }
    if (-not $DelineaUrl)        { $DelineaUrl        = Prompt-Value "DelineaUrl"        "Delinea Secret Server URL" }
    if ($DelineaSecretId -eq 0) {
        $secretIdInput = Prompt-Value "DelineaSecretId" "Delinea Exchange secret ID" "0"
        $parsed = 0
        if ([int]::TryParse($secretIdInput, [ref]$parsed)) { $DelineaSecretId = $parsed }
    }
    if (-not $SmtpHost)          { $SmtpHost          = Prompt-Value "SmtpHost"          "SMTP relay host" }
    if (-not $AdminEmail)        { $AdminEmail        = Prompt-Value "AdminEmail"        "Admin notification email(s), comma-separated" }
    if (-not $CloudTargetDomain) { $CloudTargetDomain = Prompt-Value "CloudTargetDomain" "Cloud target delivery domain (e.g. contoso.mail.onmicrosoft.com)" }
    if (-not $AllowedGroups -or $AllowedGroups.Count -eq 0) {
        $groupInput = Prompt-Value "AllowedGroups" "Authorized AD groups (comma-separated)"
        if ($groupInput) { $AllowedGroups = $groupInput -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
    }
    if (-not $AdminGroups -or $AdminGroups.Count -eq 0) {
        $adminInput = Prompt-Value "AdminGroups" "Admin settings AD groups (comma-separated, controls /admin-settings access)"
        if ($adminInput) { $AdminGroups = $adminInput -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
    }

    # Validate
    $errors = @()
    $guid = [guid]::Empty
    if (-not $EXOAppId -or -not [guid]::TryParse($EXOAppId, [ref]$guid))       { $errors += "EXOAppId must be a valid GUID" }
    if (-not $EXOOrganization -or $EXOOrganization -notlike '*.*')              { $errors += "EXOOrganization must contain a dot (e.g. contoso.onmicrosoft.com)" }
    if (-not $OnPremServerUri -or -not [uri]::IsWellFormedUriString($OnPremServerUri, [System.UriKind]::Absolute)) { $errors += "OnPremServerUri must be a valid URI" }
    if (-not $OnPremTargetDomain -or $OnPremTargetDomain -notlike '*.*')        { $errors += "OnPremTargetDomain must contain a dot (e.g. yourcompany.com)" }
    if (-not $DelineaUrl -or -not [uri]::IsWellFormedUriString($DelineaUrl, [System.UriKind]::Absolute) -or $DelineaUrl -notlike 'https://*') { $errors += "DelineaUrl must be a valid HTTPS URI" }
    if ($DelineaSecretId -le 0)                                                 { $errors += "DelineaSecretId must be > 0" }
    if (-not $SmtpHost)                                                         { $errors += "SmtpHost is required" }
    if (-not $AdminEmail -or ($AdminEmail -split ',' | Where-Object { $_.Trim() -notlike '*@*' })) { $errors += "AdminEmail must contain valid email(s)" }
    if (-not $AllowedGroups -or $AllowedGroups.Count -eq 0)                     { $errors += "AllowedGroups must have at least one entry" }
    if (-not $AdminGroups -or $AdminGroups.Count -eq 0)                         { $errors += "AdminGroups must have at least one entry (controls /admin-settings access)" }
    if (-not $CloudTargetDomain -or $CloudTargetDomain -notlike '*.*')          { $errors += "CloudTargetDomain must contain a dot" }

    if ($errors.Count -gt 0) {
        Write-Host ""
        Write-Host "Validation errors:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        Write-Fail "Fix the above errors and retry."
    }

    # Domain-prefix AllowedGroups and AdminGroups
    $qualifiedGroups = $AllowedGroups | ForEach-Object {
        if ($_ -like '*\*') { $_ } else { "$DomainPrefix\$_" }
    }
    $qualifiedAdminGroups = @()
    if ($AdminGroups -and $AdminGroups.Count -gt 0) {
        $qualifiedAdminGroups = $AdminGroups | ForEach-Object {
            if ($_ -like '*\*') { $_ } else { "$DomainPrefix\$_" }
        }
    }

    Write-Success "Configuration validated"
}

# --- Resolve identity & password ---

Write-Step "Resolving app pool identity"

$needsPassword = $false
$poolExists = Test-Path "IIS:\AppPools\$AppPoolName"

if ($poolExists) {
    $existingUser = (Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel).userName
    if (-not $ServiceAccount) {
        $ServiceAccount = $existingUser
    } elseif ($existingUser -ne $ServiceAccount) {
        Write-Warn "App pool identity changing: $existingUser -> $ServiceAccount"
        $needsPassword = $true
    }
} else {
    if (-not $ServiceAccount) {
        if ($NonInteractive) {
            Write-Fail "ServiceAccount is required for new app pool but not supplied."
        }
        $ServiceAccount = Prompt-Value "ServiceAccount" "App pool service account (DOMAIN\username)"
        if (-not $ServiceAccount) { Write-Fail "ServiceAccount is required." }
    }
    $needsPassword = $true
}

if ($needsPassword -and -not $ServiceAccountPassword) {
    if ($NonInteractive) {
        Write-Fail "ServiceAccountPassword is required (new pool or account change) but not supplied in NonInteractive mode."
    }
    $ServiceAccountPassword = Prompt-SecureValue "Enter password for $ServiceAccount"
    if (-not $ServiceAccountPassword) {
        Write-Fail "ServiceAccountPassword is required for new pool or account change."
    }
}

# --- Configure app pool identity (before stopping) ---

Write-Step "Configuring app pool"

if (-not $poolExists) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}

Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion        -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode                    -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType    -Value 3
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName        -Value $ServiceAccount

if ($ServiceAccountPassword) {
    $plainPassword = SecureString-ToPlain $ServiceAccountPassword
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value $plainPassword
    Write-Host "       Identity: $ServiceAccount (password set)" -ForegroundColor DarkGray
} else {
    Write-Host "       Identity: $ServiceAccount (credentials retained)" -ForegroundColor DarkGray
}

Write-Success "App pool configured"

# --- ACL grants (idempotent) ---

Write-Step "Setting ACLs"

# Certificate private key
$exoCert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
    Sort-Object NotBefore -Descending |
    Select-Object -First 1

if (-not $exoCert) {
    Write-Warn "Certificate '$CertSubject' not found in LocalMachine\My -- skipping key ACL"
} else {
    try {
        $rsaCert = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($exoCert)
        $keyName = $rsaCert.Key.UniqueName
        $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyName
        if (-not (Test-Path $keyPath)) {
            $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyName
        }
        if (Test-Path $keyPath) {
            icacls $keyPath /grant "${ServiceAccount}:(R)" | Out-Null
            Write-Success "Certificate private key ACL set (thumbprint: $($exoCert.Thumbprint))"
        } else {
            Write-Warn "Private key file not found at expected location"
        }
    } catch {
        Write-Warn "Could not set private key ACL: $($_.Exception.Message)"
    }
}

# Audit log folder
$auditLogFolder = Join-Path $LogRoot "ExchangeAdminWeb"
if (-not (Test-Path $auditLogFolder)) {
    New-Item -ItemType Directory -Path $auditLogFolder -Force | Out-Null
}
icacls $auditLogFolder /grant "${ServiceAccount}:(OI)(CI)M" | Out-Null
Write-Success "Audit log folder ACL set: $auditLogFolder"

# App log folder (Serilog)
$appLogFolder = Join-Path $PublishPath "logs"
if (-not (Test-Path $appLogFolder)) {
    New-Item -ItemType Directory -Path $appLogFolder -Force | Out-Null
}
icacls $appLogFolder /grant "${ServiceAccount}:(OI)(CI)M" | Out-Null
Write-Success "App log folder ACL set: $appLogFolder"

# Config fragment folder (section access)
$configFolder = Join-Path $PublishPath "config"
if (-not (Test-Path $configFolder)) {
    New-Item -ItemType Directory -Path $configFolder -Force | Out-Null
}
icacls $configFolder /grant "${ServiceAccount}:(OI)(CI)M" | Out-Null
Write-Success "Config folder ACL set: $configFolder"

# --- Publish to staging ---

Write-Step "Publishing application"

$StagingPath = "$PublishPath.staging.$timestamp"

Push-Location $ProjectRoot
try {
    dotnet publish -c Release -o $StagingPath --nologo
    if ($LASTEXITCODE -ne 0) { Write-Fail "dotnet publish failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Success "Published to staging: $StagingPath"

# --- Mode-specific deployment ---

if ($isUpgrade) {

    # --- UPGRADE ---

    Write-Step "Backing up configuration"
    $backupName = "appsettings.${timestamp}.bak"
    Copy-Item $configPath (Join-Path $BackupDir $backupName)
    Write-Success "Config backed up to $BackupDir\$backupName"

    Write-Step "Stopping app pool"
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    try {
        Write-Step "Deploying files (robocopy /MIR)"
        $robocopyArgs = @(
            $StagingPath, $PublishPath,
            '/MIR',
            '/XF', 'appsettings*.json',
            '/XD', (Join-Path $PublishPath 'logs'), (Join-Path $PublishPath 'config'),
            '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
        )
        & robocopy @robocopyArgs
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
        Write-Success "Files deployed"

        # Config reconciliation
        Write-Step "Reconciling configuration"
        $config = Get-Content $configPath -Raw | ConvertFrom-Json

        $configChanged = $false

        # Auto-rename OnPremTargetDatabases → OnPremTargetDAG
        if ($config.Migration -and
            (Get-Member -InputObject $config.Migration -Name "OnPremTargetDatabases" -MemberType NoteProperty) -and
            -not (Get-Member -InputObject $config.Migration -Name "OnPremTargetDAG" -MemberType NoteProperty)) {

            $oldValue = $config.Migration.OnPremTargetDatabases
            if ($oldValue -is [array] -or ($oldValue -is [string] -and $oldValue -like '*,*')) {
                Write-Warn "OnPremTargetDatabases contains multiple values -- manual migration required (new key OnPremTargetDAG expects a single DAG name)"
            } elseif ($oldValue -is [string]) {
                $config.Migration | Add-Member -NotePropertyName "OnPremTargetDAG" -NotePropertyValue $oldValue
                $config.Migration.PSObject.Properties.Remove("OnPremTargetDatabases")
                $configChanged = $true
                Write-Host "  OK  Migrated config key: OnPremTargetDatabases -> OnPremTargetDAG" -ForegroundColor Green
            }
        }

        # Auto-add AdminGroups if supplied via parameter and missing from config
        $hasAdminGroups = (Get-Member -InputObject $config.Security -Name "AdminGroups" -MemberType NoteProperty -ErrorAction SilentlyContinue) -and
                          $config.Security.AdminGroups -and $config.Security.AdminGroups.Count -gt 0
        if (-not $hasAdminGroups) {
            if ($AdminGroups -and $AdminGroups.Count -gt 0) {
                $qualifiedAdmin = $AdminGroups | ForEach-Object { if ($_ -like '*\*') { $_ } else { "$DomainPrefix\$_" } }
                if (Get-Member -InputObject $config.Security -Name "AdminGroups" -MemberType NoteProperty -ErrorAction SilentlyContinue) {
                    $config.Security.AdminGroups = @($qualifiedAdmin)
                } else {
                    $config.Security | Add-Member -NotePropertyName "AdminGroups" -NotePropertyValue @($qualifiedAdmin)
                }
                $configChanged = $true
                Write-Success "Set Security:AdminGroups in config"
            } else {
                Write-Warn "Security:AdminGroups is missing or empty -- /admin-settings page will be inaccessible until configured (see appsettings.json.sample)"
            }
        }

        if ($configChanged) {
            $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
        }

        $sectionAccessFragment = Join-Path $PublishPath "config\sectionaccess.json"
        if (-not (Get-Member -InputObject $config.Security -Name "SectionAccess" -MemberType NoteProperty -ErrorAction SilentlyContinue) -and
            -not (Test-Path $sectionAccessFragment)) {
            Write-Warn "Section-level permissions not configured -- use /admin-settings or create config\sectionaccess.json"
        }

        Write-Success "Configuration reconciled"
    } finally {
        Write-Step "Starting app pool"
        Start-WebAppPool -Name $AppPoolName
        Write-Success "App pool started"
    }

    Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue

} else {

    # --- FRESH INSTALL ---

    # Web application
    Write-Step "Configuring web application: $ParentSite/$AppAlias"

    if (-not (Get-Website -Name $ParentSite -ErrorAction SilentlyContinue)) {
        Write-Fail "Parent site '$ParentSite' not found. Check -ParentSite parameter."
    }

    $existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue
    if ($existingApp) {
        Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath    -Value $PublishPath
        Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
    } else {
        if (-not (Test-Path $PublishPath)) {
            New-Item -ItemType Directory -Path $PublishPath -Force | Out-Null
        }
        New-WebApplication -Name $AppAlias `
                           -Site $ParentSite `
                           -PhysicalPath $PublishPath `
                           -ApplicationPool $AppPoolName | Out-Null
    }

    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name enabled -Value $true -PSPath "IIS:\" -Location "$ParentSite/$AppAlias"

    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/anonymousAuthentication" `
        -Name enabled -Value $false -PSPath "IIS:\" -Location "$ParentSite/$AppAlias"

    Write-Success "Web application configured"

    # Stop pool before file swap
    Write-Step "Stopping app pool for file deployment"
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    try {
        # Deploy files
        Write-Step "Deploying files (robocopy /MIR)"
        $robocopyArgs = @(
            $StagingPath, $PublishPath,
            '/MIR',
            '/XF', 'appsettings*.json',
            '/XD', (Join-Path $PublishPath 'logs'), (Join-Path $PublishPath 'config'),
            '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
        )
        & robocopy @robocopyArgs
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
        Write-Success "Files deployed"

        Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue

        # Generate appsettings.json
        Write-Step "Generating appsettings.json"

        $config = [ordered]@{
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
                AppId = $EXOAppId
                Organization = $EXOOrganization
                CertificateSubject = $CertSubject
            }
            OnPremExchange = [ordered]@{
                ServerUri = $OnPremServerUri
            }
            Delinea = [ordered]@{
                SecretServerUrl = $DelineaUrl
                ExchangeSecretId = $DelineaSecretId
            }
            Audit = [ordered]@{
                LogRoot = $LogRoot
                RotationPeriod = "daily"
            }
            Email = [ordered]@{
                SmtpHost = $SmtpHost
                SmtpPort = 25
                SmtpUseSsl = $false
                FromAddress = "ExchangeAdmins@$($EXOOrganization -replace '\.onmicrosoft\.com$','.com')"
                FromName = "Exchange Admins"
                AdminNotificationEmail = $AdminEmail
                NotifyUsersOnPermissionGrant = $false
            }
            Security = [ordered]@{
                ExcludedUsers = @()
                PreventSelfGrant = $true
                AllowedGroups = @($qualifiedGroups)
                AdminGroups = @($qualifiedAdminGroups)
            }
            Migration = [ordered]@{
                HybridEndpoint = $HybridEndpoint
                CloudTargetDeliveryDomain = $CloudTargetDomain
                OnPremTargetDeliveryDomain = $OnPremTargetDomain
                OnPremTargetDAG = $OnPremTargetDAG
                CloudQuotaGB = 100
                ExcludedADGroups = @()
            }
            ServiceNow = [ordered]@{
                Enabled = $false
                InstanceUrl = ""
                Username = ""
                Password = ""
            }
            Application = [ordered]@{
                PathBase = $PathBase
            }
            AllowedHosts = "*"
        }

        $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
        Write-Success "appsettings.json generated"
    } finally {
        # Always restart pool even if config generation fails
        Write-Step "Starting app pool"
        Start-WebAppPool -Name $AppPoolName
        Write-Success "App pool started"
    }
}

# --- Final output ---

function Get-SiteUrl {
    param($SiteName, $AppPath)
    $binding = Get-WebBinding -Name $SiteName | Where-Object { $_.protocol -eq 'https' } | Select-Object -First 1
    $scheme = 'https'
    if (-not $binding) {
        $binding = Get-WebBinding -Name $SiteName | Select-Object -First 1
        $scheme = 'http'
        Write-Warn "No HTTPS binding found on '$SiteName'. Ensure HTTPS is configured for production use."
    }
    if (-not $binding) { return "${scheme}://$('localhost')/$AppPath" }
    # bindingInformation format: ip:port:host
    $parts = $binding.bindingInformation -split ':'
    $port = $parts[1]
    $host_ = if ($parts[2]) { $parts[2] } else { 'localhost' }
    $portSuffix = if (($scheme -eq 'https' -and $port -eq '443') -or ($scheme -eq 'http' -and $port -eq '80')) { '' } else { ":$port" }
    return "$scheme" + "://" + $host_ + $portSuffix + "/$AppPath"
}

$siteUrl = Get-SiteUrl $ParentSite $AppAlias

Write-Host ""
Write-Host "Deploy complete ($mode)." -ForegroundColor Green
Write-Host "  URL : $siteUrl" -ForegroundColor Cyan

if (-not $isUpgrade) {
    Write-Host ""
    Write-Host "  Post-install:" -ForegroundColor Yellow
    Write-Host "    - Review appsettings.json for additional tuning" -ForegroundColor Yellow
    Write-Host "    - Security:AdminGroups controls /admin-settings page access" -ForegroundColor Yellow
    Write-Host "    - Use /admin-settings to configure per-section group access" -ForegroundColor Yellow
    Write-Host "    - Add Security:ExcludedUsers to protect executive mailboxes" -ForegroundColor Yellow
}

Write-Host ""

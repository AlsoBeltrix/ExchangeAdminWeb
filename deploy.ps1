#Requires -RunAsAdministrator

param(
    # Defaults target the DEV site: this script is the ADI dev deploy (AGENTS.md).
    # Before the 2026-06-12 incident the defaults silently targeted the PROD
    # alias/pool/path. Prod is reached only via tools/deploy-pipeline.ps1 -Prod
    # (promote), never by this script's defaults.
    [string]$ParentSite          = "Default Web Site",
    [string]$AppAlias            = "ExchangeAdminWebDev",
    [string]$AppPoolName         = "ExchangeAdminWebDev",
    [string]$ServiceAccount,
    [securestring]$ServiceAccountPassword,
    [string]$PublishPath         = "D:\inetpub\ExchangeAdminWebDev",
    [string]$LogRoot             = "E:\WWWOutput",
    [string]$CertSubject         = "CN=EXO-Automation",

    # Config values (used on fresh install; ignored on upgrade unless -Force)
    [string]$EXOAppId,
    [string]$EXOOrganization,
    [string]$OnPremServerUri,
    [string]$OnPremTargetDomain,
    [string]$DelineaUrl,
    [string]$SmtpHost,
    [string]$AdminEmail,
    [string[]]$AllowedGroups,
    [string[]]$AdminGroups,
    [string]$DomainPrefix        = "DOMAIN",
    [string]$PathBase,
    [string]$CloudTargetDomain,
    [string]$HybridEndpoint      = "hybrid1",

    [switch]$Force,
    [switch]$ConfirmFreshInstall,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

# The IIS: drive exists only after WebAdministration loads, and referencing an
# unknown drive does NOT auto-load the module. Without this import, the
# existing-app-pool Test-Path check silently returned false when the script was
# run directly, so upgrades wrongly demanded a ServiceAccount instead of
# reusing the pool's existing identity.
Import-Module WebAdministration -ErrorAction Stop
if (-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue)) {
    Write-Host "  X  The IIS: drive is unavailable after importing WebAdministration." -ForegroundColor Red
    throw "Run this script in Windows PowerShell 5.1 on the IIS server - the WebAdministration provider does not load under PowerShell 7."
}

# --- Helpers ---

function Write-Step    { param($m) Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Success { param($m) Write-Host " OK  $m" -ForegroundColor Green }
function Write-Warn    { param($m) Write-Host "  !  $m" -ForegroundColor Yellow }
# Throw (not exit) per the repo error model: callers like deploy-pipeline.ps1 run
# under $ErrorActionPreference = "Stop" and must see failures as terminating errors.
function Write-Fail    { param($m) Write-Host "  X  $m" -ForegroundColor Red; throw $m }

# icacls is a native exe: $ErrorActionPreference = "Stop" does NOT catch its failures,
# and "| Out-Null" discards the error text too, so a denied/failed grant printed nothing
# and the next Write-Success falsely reported the ACL was set. Route every icacls call
# through here so a nonzero exit becomes a terminating error (icacls returns 0 on success).
function Set-AclChecked {
    param([string]$Path, [string]$Identity, [string]$Rights)
    & icacls $Path /grant "${Identity}:$Rights" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "icacls failed (exit $LASTEXITCODE) granting $Identity ${Rights} on $Path"
    }
}

function Start-AppPoolWithRetry {
    param([string]$Name, [int]$MaxAttempts = 5, [int]$DelaySeconds = 3)
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        try {
            $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
            if ($state -eq 'Started') { return }
            Start-WebAppPool -Name $Name -ErrorAction Stop
            return
        } catch {
            if ($i -eq $MaxAttempts) { throw }
            Write-Host "       Pool not ready, retrying in ${DelaySeconds}s ($i/$MaxAttempts)..." -ForegroundColor DarkGray
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

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

function Write-DeploymentManifest {
    param([string]$PublishFolder, [string]$RepoRoot)

    $dllPath = Join-Path $PublishFolder "ExchangeAdminWeb.dll"
    $version = if (Test-Path -LiteralPath $dllPath) {
        (Get-Item $dllPath | Select-Object -ExpandProperty VersionInfo).FileVersion
    } else { "unknown" }

    $gitCommit = try { (git -C $RepoRoot rev-parse HEAD 2>$null) } catch { "unknown" }
    $gitBranch = try { (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null) } catch { "unknown" }
    $gitDirty = try { [bool](git -C $RepoRoot status --porcelain 2>$null) } catch { $false }

    $manifest = [ordered]@{
        version   = ($version -split '\.')[0..2] -join '.'
        gitCommit = $gitCommit
        gitBranch = $gitBranch
        gitDirty  = $gitDirty
        buildTime = (Get-Date).ToUniversalTime().ToString("o")
        repoPath  = $RepoRoot
    }

    $manifestPath = Join-Path $PublishFolder "deployment-manifest.json"
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Success "Deployment manifest written: v$($manifest.version) @ $($gitCommit.Substring(0, [Math]::Min(8, $gitCommit.Length)))"
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
    $forceConfigDir = Join-Path $PublishPath "config"
    if (Test-Path $forceConfigDir) {
        $forceConfigBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"
        Copy-Item $forceConfigDir $forceConfigBackup -Recurse
        Write-Warn "Existing runtime config directory backed up to $forceConfigBackup"
    }
}

$mode = if ($isUpgrade) { "UPGRADE" } else { "INSTALL" }

# A fresh install must be deliberate: an unexpected INSTALL usually means the
# target parameters point at the wrong site (the 2026-06-12 incident's attempt #1
# nearly fresh-installed over an existing deployment). -Force is its own consent.
if (-not $isUpgrade -and -not $Force -and -not $ConfirmFreshInstall) {
    Write-Fail "No existing deployment at $PublishPath (no appsettings.json) -- this would be a FRESH INSTALL creating IIS app '$ParentSite/$AppAlias'. Re-run with -ConfirmFreshInstall if intended, or check -PublishPath/-AppAlias. Generic installs: tools/Install-ExchangeAdminWeb.ps1."
}

Write-Host ""
Write-Host "ExchangeAdminWeb - Deploy ($mode)" -ForegroundColor Magenta
Write-Host "  Source  : $ProjectRoot" -ForegroundColor DarkGray
Write-Host "  Publish : $PublishPath" -ForegroundColor DarkGray
Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray
Write-Host ""

# --- Collect & validate config (fresh install only) ---

if (-not $isUpgrade) {
    Write-Step "Collecting configuration values"

    $requiredParams = @{}

    if (-not $EXOAppId)          { $EXOAppId          = Prompt-Value "EXOAppId"          "Azure App Registration ID (GUID)" }
    if (-not $EXOOrganization)   { $EXOOrganization   = Prompt-Value "EXOOrganization"   "Exchange Online org (e.g. contoso.onmicrosoft.com)" }
    if (-not $OnPremServerUri)   { $OnPremServerUri   = Prompt-Value "OnPremServerUri"   "On-prem Exchange PowerShell URI (e.g. http://exch01.domain.com/PowerShell/)" }
    if (-not $OnPremTargetDomain){ $OnPremTargetDomain = Prompt-Value "OnPremTargetDomain" "On-prem email domain (e.g. yourcompany.com)" }
    if (-not $DelineaUrl)        { $DelineaUrl        = Prompt-Value "DelineaUrl"        "Delinea Secret Server URL" }
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
            Set-AclChecked $keyPath $ServiceAccount "(R)"
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
Set-AclChecked $auditLogFolder $ServiceAccount "(OI)(CI)M"
Write-Success "Audit log folder ACL set: $auditLogFolder"

# App log folder (Serilog)
$appLogFolder = Join-Path $PublishPath "logs"
if (-not (Test-Path $appLogFolder)) {
    New-Item -ItemType Directory -Path $appLogFolder -Force | Out-Null
}
Set-AclChecked $appLogFolder $ServiceAccount "(OI)(CI)M"
Write-Success "App log folder ACL set: $appLogFolder"

# Config fragment folder (section access)
$configFolder = Join-Path $PublishPath "config"
if (-not (Test-Path $configFolder)) {
    New-Item -ItemType Directory -Path $configFolder -Force | Out-Null
}
Set-AclChecked $configFolder $ServiceAccount "(OI)(CI)M"
Write-Success "Config folder ACL set: $configFolder"

# --- Ensure IIS auth settings (idempotent, runs on both upgrade and fresh) ---

Write-Step "Verifying IIS web application and authentication"

$iisLocation = "$ParentSite/$AppAlias"
$existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue
if (-not $existingApp) {
    if (-not (Test-Path $PublishPath)) {
        New-Item -ItemType Directory -Path $PublishPath -Force | Out-Null
    }
    New-WebApplication -Name $AppAlias -Site $ParentSite -PhysicalPath $PublishPath -ApplicationPool $AppPoolName | Out-Null
    Write-Success "Web application created: $ParentSite/$AppAlias"
    $existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite
}
if ($existingApp) {
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name enabled -Value $true -PSPath "IIS:\" -Location $iisLocation
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name useKernelMode -Value $false -PSPath "IIS:\" -Location $iisLocation
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name useAppPoolCredentials -Value $true -PSPath "IIS:\" -Location $iisLocation
    # Remove Negotiate provider - forces NTLM which works without SPN registration
    $providers = Get-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication/providers" `
        -Name "." -PSPath "IIS:\" -Location $iisLocation
    $hasNegotiate = $providers.Collection | Where-Object { $_.Value -eq "Negotiate" }
    if ($hasNegotiate) {
        Remove-WebConfigurationProperty `
            -Filter "system.webServer/security/authentication/windowsAuthentication/providers" `
            -Name "." -PSPath "IIS:\" -Location $iisLocation `
            -AtElement @{value="Negotiate"}
    }
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/anonymousAuthentication" `
        -Name enabled -Value $false -PSPath "IIS:\" -Location $iisLocation
    Write-Success "IIS auth: Windows Auth (NTLM), kernel mode off, app pool credentials, anonymous off"
} else {
    Write-Host "       Web application not yet created - auth will be set during install" -ForegroundColor DarkGray
}

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

    # The 2026-06-12 incident had no pre-deploy snapshot of config/, leaving the
    # pre-incident enablement state unknowable. Back up the whole runtime config
    # directory, retained alongside the appsettings backups.
    $runtimeConfigDir = Join-Path $PublishPath "config"
    if (Test-Path $runtimeConfigDir) {
        $configDirBackup = Join-Path $BackupDir "config.${timestamp}.bak"
        Copy-Item $runtimeConfigDir $configDirBackup -Recurse
        Write-Success "Runtime config directory backed up to $configDirBackup"
    } else {
        $configDirBackup = $null
        Write-Warn "No runtime config directory at $runtimeConfigDir -- nothing to back up"
    }

    # Pre-deploy snapshot for the post-deploy drift check (incident fix #5): the
    # 2026-06-12 incident surfaced as silent runtime-state divergence with nothing
    # comparing before to after.
    # extended-log-level.txt is rewritten by the app on every startup
    # (ExtendedLogService.LoadLevel -> SetLevel), so it always differs across a
    # pool restart and would false-positive the drift check.
    $driftCheckExclusions = @('extended-log-level.txt')
    $preConfigInventory = @{}
    if (Test-Path $runtimeConfigDir) {
        Get-ChildItem $runtimeConfigDir -File |
            Where-Object { $_.Name -notin $driftCheckExclusions } |
            ForEach-Object {
                $preConfigInventory[$_.Name] = "$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
            }
    }
    $preAppSettingsKeys = @((Get-Content $configPath -Raw | ConvertFrom-Json).PSObject.Properties.Name)

    Write-Step "Stopping app pool"
    try { Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop } catch {}
    Start-Sleep -Seconds 3

    $deploymentReadyForManifest = $false
    try {
        Write-Step "Deploying files (robocopy /MIR)"
        $robocopyArgs = @(
            $StagingPath, $PublishPath,
            '/MIR',
            '/XF', 'appsettings*.json',
            '/XD', 'logs', 'config',
            '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
        )
        & robocopy @robocopyArgs
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
        Write-Success "Files deployed"

        # Config reconciliation
        Write-Step "Reconciling configuration"
        $config = Get-Content $configPath -Raw | ConvertFrom-Json

        if ($config.Migration -and (Get-Member -InputObject $config.Migration -Name "OnPremTargetDAG" -MemberType NoteProperty -ErrorAction SilentlyContinue)) {
            $hasTargetDatabases = (Get-Member -InputObject $config.Migration -Name "OnPremTargetDatabases" -MemberType NoteProperty -ErrorAction SilentlyContinue) -and
                                  $config.Migration.OnPremTargetDatabases -and $config.Migration.OnPremTargetDatabases.Count -gt 0
            if (-not $hasTargetDatabases) {
                Write-Warn "Migration:OnPremTargetDatabases is not configured. Move-back batches will fail until target databases are set in Module Config."
            }
            Write-Warn "Migration:OnPremTargetDAG is obsolete. Leaving appsettings.json unchanged during deploy; remove it manually after confirming Migration:OnPremTargetDatabases."
        }

        if ($config.Delinea -and (Get-Member -InputObject $config.Delinea -Name "ExchangeSecretId" -MemberType NoteProperty -ErrorAction SilentlyContinue)) {
            Write-Warn "Delinea:ExchangeSecretId is obsolete. Leaving appsettings.json unchanged during deploy; configure DelineaSecretId separately per module."
        }

        $hasAdminGroups = $config.Security -and
                          (Get-Member -InputObject $config.Security -Name "AdminGroups" -MemberType NoteProperty -ErrorAction SilentlyContinue) -and
                          $config.Security.AdminGroups -and $config.Security.AdminGroups.Count -gt 0
        if (-not $hasAdminGroups) {
            if ($AdminGroups -and $AdminGroups.Count -gt 0) {
                Write-Warn "Security:AdminGroups is missing or empty. Leaving appsettings.json unchanged during deploy; add AdminGroups manually."
            } else {
                Write-Warn "Security:AdminGroups is missing or empty -- /admin-settings page will be inaccessible until configured (see appsettings.json.sample)"
            }
        }

        $sectionAccessFragment = Join-Path $PublishPath "config\sectionaccess.json"
        if ((-not $config.Security -or -not (Get-Member -InputObject $config.Security -Name "SectionAccess" -MemberType NoteProperty -ErrorAction SilentlyContinue)) -and
            -not (Test-Path $sectionAccessFragment)) {
            Write-Warn "Section-level permissions not configured -- use per-module config pages or create config\sectionaccess.json"
        }

        $protectedPrincipals = Join-Path $PublishPath "config\protected-principals.json"
        if (-not (Test-Path $protectedPrincipals)) {
            Write-Warn "config\protected-principals.json not found -- protected principal rules will use defaults until configured"
        }

        $adEditableAttributes = Join-Path $PublishPath "config\ad-editable-attributes.json"
        if (-not (Test-Path $adEditableAttributes)) {
            Write-Warn "config\ad-editable-attributes.json not found -- AD attribute editor will use defaults until configured"
        }

        Write-Success "Configuration reconciled"
        $deploymentReadyForManifest = $true
    } finally {
        Write-Step "Starting app pool"
        Start-AppPoolWithRetry -Name $AppPoolName
        Write-Success "App pool started"
        if ($deploymentReadyForManifest) {
            Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
        }
        # Staging contains the dev appsettings.json (real environment values).
        # Clean it on failure paths too, not only after a successful deploy.
        Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    # --- Post-deploy config drift check (incident fix #5) ---
    # Only reached on success (failures above throw past this point). The pool has
    # restarted; nothing about this deploy should have changed runtime config.

    Write-Step "Verifying runtime config integrity"

    $driftFound = $false
    $postFiles = @{}
    if (Test-Path $runtimeConfigDir) {
        Get-ChildItem $runtimeConfigDir -File |
            Where-Object { $_.Name -notin $driftCheckExclusions } |
            ForEach-Object {
                $postFiles[$_.Name] = "$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
            }
    }
    foreach ($name in $preConfigInventory.Keys) {
        if (-not $postFiles.ContainsKey($name)) {
            Write-Warn "POST-DEPLOY CHECK: config\$name existed before the deploy but is MISSING now"
            $driftFound = $true
        } elseif ($postFiles[$name] -ne $preConfigInventory[$name]) {
            Write-Warn "POST-DEPLOY CHECK: config\$name was MODIFIED during the deploy (size or timestamp changed)"
            $driftFound = $true
        }
    }
    foreach ($name in $postFiles.Keys) {
        if (-not $preConfigInventory.ContainsKey($name)) {
            Write-Warn "POST-DEPLOY CHECK: config\$name APPEARED during the deploy"
            $driftFound = $true
        }
    }

    $postAppSettingsKeys = @((Get-Content $configPath -Raw | ConvertFrom-Json).PSObject.Properties.Name)
    $lostKeys = @($preAppSettingsKeys | Where-Object { $_ -notin $postAppSettingsKeys })
    if ($lostKeys.Count -gt 0) {
        Write-Warn ("POST-DEPLOY CHECK: appsettings.json LOST top-level keys: " + ($lostKeys -join ', '))
        $driftFound = $true
    }

    if ($driftFound) {
        $backupHint = if ($configDirBackup) { "$configDirBackup and " } else { "" }
        Write-Warn "POST-DEPLOY CHECK: runtime config drifted during this deploy. Compare against ${backupHint}$BackupDir\$backupName before trusting the app, and investigate what wrote it."
    } else {
        Write-Success "Runtime config verified: config/ inventory and appsettings.json keys unchanged"
    }

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
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name useKernelMode -Value $false -PSPath "IIS:\" -Location "$ParentSite/$AppAlias"
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication" `
        -Name useAppPoolCredentials -Value $true -PSPath "IIS:\" -Location "$ParentSite/$AppAlias"
    Remove-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/windowsAuthentication/providers" `
        -Name "." -PSPath "IIS:\" -Location "$ParentSite/$AppAlias" `
        -AtElement @{value="Negotiate"} -ErrorAction SilentlyContinue
    Set-WebConfigurationProperty `
        -Filter "system.webServer/security/authentication/anonymousAuthentication" `
        -Name enabled -Value $false -PSPath "IIS:\" -Location "$ParentSite/$AppAlias"

    Write-Success "Web application configured"

    # Stop pool before file swap
    Write-Step "Stopping app pool for file deployment"
    try { Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop } catch {}
    Start-Sleep -Seconds 3

    $deploymentReadyForManifest = $false
    try {
        # Deploy files
        Write-Step "Deploying files (robocopy /MIR)"
        $robocopyArgs = @(
            $StagingPath, $PublishPath,
            '/MIR',
            '/XF', 'appsettings*.json',
            '/XD', 'logs', 'config',
            '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
        )
        & robocopy @robocopyArgs
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
        Write-Success "Files deployed"

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
                CredentialTarget = "Delinea_Client"
            }
            Audit = [ordered]@{
                LogRoot = $LogRoot
                RotationPeriod = "daily"
            }
            OperationTrace = [ordered]@{
                Enabled = $true
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
                AllowedGroups = @($qualifiedGroups)
                AdminGroups = @($qualifiedAdminGroups)
            }
            ServiceNow = [ordered]@{
                Enabled = $false
                InstanceUrl = ""
                Username = ""
                Password = ""
            }
            Application = [ordered]@{
                PathBase = $PathBase
                ContactEmail = $AdminEmail
            }
            AllowedHosts = "*"
        }

        $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
        Write-Success "appsettings.json generated"
        $deploymentReadyForManifest = $true
    } finally {
        Write-Step "Starting app pool"
        Start-AppPoolWithRetry -Name $AppPoolName
        Write-Success "App pool started"
        if ($deploymentReadyForManifest) {
            Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
        }
        # Staging contains the generated appsettings.json; clean on failure paths too.
        Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
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
    Write-Host "    - Security:AdminGroups controls /admin-settings and module config pages" -ForegroundColor Yellow
    Write-Host "    - Use per-module config pages in the sidebar to set section access" -ForegroundColor Yellow
    Write-Host "    - Use Module Config to set Excluded Users for Mailbox/Calendar protection" -ForegroundColor Yellow
}

Write-Host ""

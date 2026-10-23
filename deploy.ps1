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
    [string]$PublicBaseUrl,
    [string]$CloudTargetDomain,
    [string]$HybridEndpoint      = "hybrid1",

    [switch]$Force,
    [switch]$ConfirmFreshInstall,
    # cdt-36: -Force and -ConfirmFreshInstall take the fresh-install path, which points an EXISTING
    # IIS application at this run's -PublishPath and -AppPoolName. A run whose folder or pool differs
    # from the ones IIS serves the application from is refused before anything is configured; this
    # switch is the explicit way to move it. An upgrade never re-points the application, and refuses a
    # pool or folder that does not serve it with or without this switch (cdt-42).
    [switch]$AllowRepoint,
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
    # cdt-24: the exit code alone cannot catch this one - icacls grants an empty name to
    # BUILTIN\BUILTIN (S-1-5-32) and exits 0.
    if ([string]::IsNullOrWhiteSpace($Identity)) {
        Write-Fail "Refusing to grant $Rights on $Path to an empty identity: icacls resolves an empty name to BUILTIN\BUILTIN and reports success, so the grant would silently go to the wrong principal."
    }
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
            if ($state -eq 'Started') { break }
            Start-WebAppPool -Name $Name -ErrorAction Stop
            break
        } catch {
            if ($i -eq $MaxAttempts) { throw }
            Write-Host "       Pool not ready, retrying in ${DelaySeconds}s ($i/$MaxAttempts)..." -ForegroundColor DarkGray
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    # cdt-25: the loop retries a start request that throws; a request that returns only means IIS
    # accepted it, and the pool can still stay stopped (a bad identity password, a missing runtime,
    # rapid-fail protection) while the caller prints "App pool started". Confirm it (A38); a pool that
    # does not reach Started throws, and the caller reports that as its own failure (cdt-16).
    $null = Start-AppPoolVerified -Name $Name
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
    # cdt-27: SecureStringToBSTR copies the password into unmanaged memory as plaintext. Keep the
    # pointer so the finally zeroes and frees that copy on every path; the old one-liner dropped it.
    # The string returned is a managed copy, which .NET cannot zero.
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Write-DeploymentManifest {
    param([string]$PublishFolder, [string]$RepoRoot)

    $dllPath = Join-Path $PublishFolder "ExchangeAdminWeb.dll"
    $version = if (Test-Path -LiteralPath $dllPath) {
        (Get-Item $dllPath | Select-Object -ExpandProperty VersionInfo).FileVersion
    } else { "unknown" }

    $gitCommit = try { (git -C $RepoRoot rev-parse HEAD 2>$null) } catch { "unknown" }
    $gitBranch = try { (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null) } catch { "unknown" }
    # cdt-26: a git status that failed is unknown, not clean. Windows PowerShell 5.1 turns its stderr
    # into an error caught here; PowerShell 7 returns nothing with a non-zero exit. The old [bool](...)
    # read both as $false, which the promotion's "built from a dirty tree" gate takes as clean.
    $gitDirty = 'unknown'
    try {
        $statusOutput = git -C $RepoRoot status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0) { $gitDirty = [bool]$statusOutput }
    } catch {
        $gitDirty = 'unknown'
    }

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
    # Shorten only a commit long enough to shorten (cdt-14). Windows PowerShell 5.1 turns a failed
    # git's stderr into an error the catch above maps to "unknown"; PowerShell 7 does not, so
    # $gitCommit is $null there, and this runs inside the finally that restarts the pool, where a
    # throw would replace the deploy's own outcome. Same guard as tools/deploy-pipeline.ps1.
    $shortCommit = $gitCommit
    if ($gitCommit -and $gitCommit.Length -ge 8) { $shortCommit = $gitCommit.Substring(0, 8) }
    Write-Success "Deployment manifest written: v$($manifest.version) @ $shortCommit"
}

# -Force regenerates appsettings.json from parameters, and ConfigStore:Path is the one key that must
# survive it: it names the config database the instance opens (on a shared-database server the single
# file dev and prod both use, decision 2026-09-04), and without it the app silently opens its own
# config\exchangeadmin.db instead - a different authorization and settings store (cdt-8). The caller
# passes the value exactly as Get-ConfigStorePathSetting read it from the file being replaced. With no
# value nothing is added, so a fresh install's file is what it always was.
function Write-AppSettingsFile {
    param(
        [System.Collections.IDictionary]$Settings,
        [string]$Path,
        [string]$ConfigStorePath
    )

    if ($ConfigStorePath) {
        $Settings['ConfigStore'] = [ordered]@{ Path = $ConfigStorePath }
    }
    $Settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding UTF8
}

# cdt-36. -Force over an existing deployment, and -ConfirmFreshInstall, take the fresh-install branch,
# whose "Configuring web application" step sets an EXISTING application's physicalPath and
# applicationPool to this run's -PublishPath and -AppPoolName. Nothing compared them with what IIS
# serves, so a mistyped or defaulted value moved the live application and the run still ended "Deploy
# complete": to another folder, whose generated appsettings.json has no ConfigStore:Path (it is read
# from that folder's own file), so the app opened a new, empty config database there (the cdt-8
# outcome); and/or into another pool, which the stop before the mirror then stops - another
# instance's, if the name is one. The checks before that stop ask IIS after the re-point, so they
# pass. The installer makes the same check (cdt-15); here -AllowRepoint is the explicit way to move
# the application. Folders compare as Assert-AppPoolServesPath compares them, through the module's
# ConvertTo-ComparablePhysicalPath: by equality, case-insensitively, never by prefix.
# cdt-42. An upgrade never moves the application, but it configures the pool -AppPoolName names and
# grants ACLs under -PublishPath before its own binding check, so a pool or folder that does not serve
# the application was changed and only then refused. On an upgrade (-Upgrade) a difference therefore
# always refuses: -AllowRepoint cannot make an upgrade right, since nothing on that path re-points.
function Assert-ApplicationTargetUnchanged {
    param(
        [string]$Location,
        [bool]$Exists,
        [string]$ExistingPool,
        [string]$ExistingPhysicalPath,
        [string]$AppPoolName,
        [string]$PublishPath,
        [switch]$AllowRepoint,
        [switch]$Upgrade
    )

    if (-not $Exists) {
        Write-Host "       No IIS application $Location yet: this run creates it, with its files at '$PublishPath' under app pool '$AppPoolName'." -ForegroundColor DarkGray
        return
    }

    $differences = @()
    if (-not [string]::Equals($ExistingPool, $AppPoolName, [System.StringComparison]::OrdinalIgnoreCase)) {
        $differences += "app pool '$ExistingPool' (this run: -AppPoolName '$AppPoolName')"
    }
    $existingFolder = ConvertTo-ComparablePhysicalPath $ExistingPhysicalPath
    # A folder IIS does not report counts as different: the run cannot show it deploys in place. The
    # module's Test-SamePhysicalFolder is the one comparison, shared with the serving-folder choice
    # after this check (cdt-44).
    if (-not (Test-SamePhysicalFolder -Path $ExistingPhysicalPath -Other $PublishPath)) {
        $differences += "files at '$ExistingPhysicalPath' (this run: -PublishPath '$PublishPath')"
    }

    if ($differences.Count -eq 0) {
        Write-Success "IIS application $Location runs under app pool '$ExistingPool' with its files at '$ExistingPhysicalPath': the pool and folder this run names, so it is deployed in place"
        return
    }
    if ($Upgrade) {
        $switchNote = ''
        if ($AllowRepoint) { $switchNote = ' -AllowRepoint does not apply: only a -Force or -ConfirmFreshInstall run re-points an application.' }
        Write-Fail "IIS application $Location already exists, with $($differences -join ' and '). An upgrade configures the app pool and the folders it names before it deploys, and never moves the application, so this run would change a pool or folder that does not serve it. Re-run with -AppPoolName '$ExistingPool' -PublishPath '$existingFolder', or move the application in IIS first.$switchNote Nothing was deployed."
    }
    if ($AllowRepoint) {
        Write-Warn "IIS application $Location exists, with $($differences -join ' and '). -AllowRepoint: this run MOVES it to app pool '$AppPoolName' with its files at '$PublishPath'. The old pool and folder are left as they are, no longer serving it."
        return
    }
    Write-Fail "IIS application $Location already exists, with $($differences -join ' and '). -Force and -ConfirmFreshInstall point an existing application at this run's -AppPoolName and -PublishPath, so this run would move the live application. Re-run with -AppPoolName '$ExistingPool' -PublishPath '$existingFolder' to deploy it in place, or add -AllowRepoint if the move is intended. Nothing was deployed."
}

$ProjectRoot = $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMddHHmmss'

# Shared SQLite config-DB backup/integrity helpers (SqliteConfigStore-Plan Phase D).
Import-Module (Join-Path $PSScriptRoot 'tools\SqliteConfigBackup.psm1') -Force

# Shared warning about active bulk jobs before an app-pool recycle (BulkJobRunner-Plan).
Import-Module (Join-Path $PSScriptRoot 'tools\JobStateWarning.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'tools\AppPoolControl.psm1') -Force

if (-not $PathBase) { $PathBase = "/$AppAlias" }
$PublishPath = $PublishPath.TrimEnd('\', '/')

# --- Detect mode ---

$configPath = Join-Path $PublishPath "appsettings.json"
$isUpgrade = (Test-Path $configPath) -and (-not $Force)

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

# --- The IIS application this run deploys to (cdt-36, cdt-42) ---

# Read on every run, before anything below writes - the backup folder and the pre-force copy included
# (cdt-45) - or touches IIS, an ACL or the publish folder. The
# fresh-install branch (-Force, -ConfirmFreshInstall) points an existing application at this run's
# -PublishPath and -AppPoolName, so a run that would move it is refused here unless -AllowRepoint says
# the move is intended (cdt-36). An upgrade never moves it, but the pool step and the ACL grants below
# act on the pool and folder it names before the upgrade branch asks IIS which pool serves the
# application, so an upgrade whose pool or folder does not serve it is refused here, before they run,
# whatever the switches (cdt-42). Read-only, and it prints what it compared, so a plan mode could run
# it as it is (this script has none). A lookup that fails refuses: the run cannot show that the pool
# and folder it names are the ones serving the application.
Write-Step "Checking IIS application $ParentSite/$AppAlias"
$existingApplication = $null
try {
    $existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop
} catch {
    Write-Fail "Cannot read IIS application $ParentSite/$AppAlias ($($_.Exception.Message)), so it cannot be confirmed that the app pool and folder this run names are the ones serving it. Nothing was deployed."
}
Assert-ApplicationTargetUnchanged -Location "$ParentSite/$AppAlias" -Exists ($null -ne $existingApplication) -ExistingPool "$($existingApplication.applicationPool)" -ExistingPhysicalPath "$($existingApplication.PhysicalPath)" -AppPoolName $AppPoolName -PublishPath $PublishPath -AllowRepoint:$AllowRepoint -Upgrade:$isUpgrade

# --- The configuration the application runs on now (cdt-44) ---

# Normally the run's own folder. When -AllowRepoint moves an existing application to another folder,
# the configuration it runs on - its appsettings.json, and the config database that names - is in the
# folder IIS serves it from now, and -PublishPath is only where it is going. So that folder is what is
# backed up below and what the regenerated appsettings.json carries ConfigStore:Path over from. A move
# that would leave a database kept inside the old folder behind is refused here, before anything is
# written.
$servingFolder = $PublishPath
$movesFolder = $false
if (-not $isUpgrade -and $AllowRepoint -and $null -ne $existingApplication) {
    $reportedFolder = ConvertTo-ComparablePhysicalPath "$($existingApplication.PhysicalPath)"
    if ($reportedFolder -and -not (Test-SamePhysicalFolder -Path $reportedFolder -Other $PublishPath)) {
        $servingFolder = $reportedFolder
        $movesFolder = $true
    }
}
$servingConfigPath = Join-Path $servingFolder "appsettings.json"
if ($movesFolder -and (Test-Path -LiteralPath $servingConfigPath)) {
    $servingDbPath = Resolve-ConfigDbPath -PublishPath $servingFolder
    if (-not (Get-ConfigStorePathSetting -PublishPath $servingFolder) -and (Test-IsSqliteConfigDbPresent -DbPath $servingDbPath)) {
        Write-Fail "IIS application $ParentSite/$AppAlias keeps its config database inside the folder it is being moved from ('$servingDbPath'): its appsettings.json names no ConfigStore:Path, so after the move it would open a new, empty database under '$PublishPath\config' and leave that one behind. Move the database to a shared path first (tools\Move-ConfigDbToShared.ps1), so that appsettings.json names it, or move the application in IIS by hand. Nothing was deployed."
    }
    Write-Warn "-AllowRepoint: the configuration is backed up from '$servingFolder', the folder IIS serves the application from now, and its ConfigStore:Path is carried into the appsettings.json generated in '$PublishPath'."
}

# --- Backups taken before anything is configured ---

# Written only once the check above has passed, so a run it refuses leaves no backup folder and no
# copy behind (cdt-45); still before anything below configures IIS, an ACL or a folder.
$BackupDir = Join-Path $LogRoot "ExchangeAdminWeb\ConfigBackups"
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }

# -Force over an existing deployment regenerates appsettings.json, so keep a copy of it first. The
# config database is NOT copied here: a raw copy of a live WAL database can be torn and nothing would
# verify it, and a shared database is not under config\ at all. The verified online backup runs in the
# fresh-install branch below, after the publish (it needs the db tool from staging) and before the pool
# stops or anything is mirrored (cdt-9).
if ($Force -and (Test-Path $configPath)) {
    $backupName = "appsettings.${timestamp}.pre-force.bak"
    Copy-Item $configPath (Join-Path $BackupDir $backupName)
    Write-Warn "Existing config backed up to $BackupDir\$backupName"
}

# cdt-44: a move also keeps the appsettings.json the application runs on now, in the folder it leaves;
# the copy above is of the file this run regenerates.
if ($movesFolder -and (Test-Path -LiteralPath $servingConfigPath)) {
    $servingBackupName = "appsettings.${timestamp}.pre-move.bak"
    Copy-Item -LiteralPath $servingConfigPath -Destination (Join-Path $BackupDir $servingBackupName)
    Write-Warn "The appsettings.json the application runs on now ($servingConfigPath) backed up to $BackupDir\$servingBackupName"
}

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
    # cdt-24: a pool that does not run as a specific user (ApplicationPoolIdentity or a built-in
    # account) has no userName, and taking that as the account set the pool to SpecificUser with an
    # empty name - an identity IIS cannot start - and sent the empty name to every icacls grant below.
    # identityType is matched as text: the provider yields the number under Windows PowerShell 5.1
    # and the name under PowerShell 7 (the cutover script records the same).
    $existingProcessModel = Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel
    $existingUser = $existingProcessModel.userName
    $existingRunsAsUser = ("$($existingProcessModel.identityType)" -in @('SpecificUser', '3')) -and -not [string]::IsNullOrWhiteSpace($existingUser)
    if (-not $ServiceAccount) {
        if (-not $existingRunsAsUser) {
            Write-Fail "App pool $AppPoolName exists but does not run as a specific user (IIS reports identityType '$($existingProcessModel.identityType)', user name '$existingUser'), and this script configures every pool with a service account. Pass -ServiceAccount and its password to move the pool to one, or deploy this instance with tools/Install-ExchangeAdminWeb.ps1, which keeps a pool's existing identity. Nothing was deployed."
        }
        $ServiceAccount = $existingUser
    } elseif (-not $existingRunsAsUser -or $existingUser -ne $ServiceAccount) {
        $currentIdentity = $existingUser
        if (-not $existingRunsAsUser) { $currentIdentity = "identityType '$($existingProcessModel.identityType)'" }
        Write-Warn "App pool identity changing: $currentIdentity -> $ServiceAccount"
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

# Staging holds a published appsettings.json with real environment values. Every failure from
# here to the end of the mode-specific section - a failed publish, a missing app binary, the
# pre-deploy backup refusing a corrupt or missing database, an IIS or pool-stop failure, even a
# throw from the pool restart in the inner finally blocks - must remove it. The inner finally
# blocks still remove it first, so the post-deploy checks run with staging already gone (cdt-6).
try {
    Push-Location $ProjectRoot
    try {
        dotnet publish -c Release -o $StagingPath --nologo
        if ($LASTEXITCODE -ne 0) { Write-Fail "dotnet publish failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }

    Write-Success "Published to staging: $StagingPath"

    # The config DB backup, the job check and the post-deploy integrity check run the app's own
    # db mode from the build just published, through the .NET host in Program Files - never
    # sqlite3.exe and never a PATH search (docs/ConfigDbToolInApp-Plan.md). Resolve it now so a
    # staging folder without the app binary stops the deploy, naming the path, before anything is
    # backed up or mirrored. The enclosing finally removes staging on this failure too.
    $configDbTool = Resolve-ConfigDbTool -AppPath $StagingPath
    Write-Success "Config DB tool: $($configDbTool.Host) $($configDbTool.Assembly) db"

    # --- Mode-specific deployment ---

    # Both paths hand this to the active-job check before the pool stops. It used to be set only
    # inside the upgrade branch, so a fresh install (or -Force) passed $null to a mandatory string
    # parameter and the deploy died on a binding error after IIS was already configured (cdt-5). It is
    # the live configuration directory: the serving folder's, which is -PublishPath's except when
    # -AllowRepoint moves the application to another folder (cdt-44).
    $runtimeConfigDir = Join-Path $servingFolder "config"

    # The files both config backup copies leave out (cdt-10). The config database is backed up
    # separately, by the verified online backup. The jobs and usage databases are per-instance
    # operational state that is deliberately never backed up (Architectural Invariant 3): copying them
    # kept bulk-job records and usage telemetry in a backup nothing prunes, past the retention the pages
    # state, and a raw copy of a live WAL database is not a usable backup anyway. A new database the
    # app keeps in config\ belongs here too; the cdt-10 test reads the app's source to enforce that.
    # The upgrade's drift inventory leaves the same databases out, from this list (cdt-13).
    $configCopyExclusions = @(
        'exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm',
        'exchangeadmin-jobs.db', 'exchangeadmin-jobs.db-wal', 'exchangeadmin-jobs.db-shm',
        'exchangeadmin-usage.db', 'exchangeadmin-usage.db-wal', 'exchangeadmin-usage.db-shm')

    if ($isUpgrade) {

        # --- UPGRADE ---

        Write-Step "Backing up configuration"
        $backupName = "appsettings.${timestamp}.bak"
        Copy-Item $configPath (Join-Path $BackupDir $backupName)
        Write-Success "Config backed up to $BackupDir\$backupName"

        # The 2026-06-12 incident had no pre-deploy snapshot of config/, leaving the
        # pre-incident enablement state unknowable. Back up the runtime config directory,
        # retained alongside the appsettings backups.
        #
        # The config DB is a LIVE SQLite database - a raw recursive Copy-Item of a live WAL DB can be
        # torn/inconsistent, producing a worthless rollback snapshot. Back the DB up via a verified
        # online backup (VACUUM INTO + integrity_check; SqliteConfigStore-Plan Phase D), then copy
        # any remaining non-DB config files (e.g. legacy *.imported-* archives, samples) with a plain
        # recursive copy.
        #
        # WHERE THE DB IS (SharedConfigDb-Plan AC6, decision 2026-09-04): when appsettings.json names
        # ConfigStore:Path, that file - shared by both instances on this server - is what gets backed
        # up, not the publish folder's config\. With the key set the file MUST exist: a mistyped key
        # or a moved file must stop the deploy here, before anything is mirrored, because the app
        # would otherwise refuse to start (it never creates a configured database). Without the key
        # a missing DB just means the first start has not happened yet.
        $configStorePathSetting = Get-ConfigStorePathSetting -PublishPath $PublishPath
        $configDbPath = Resolve-ConfigDbPath -PublishPath $PublishPath
        if ($configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)) {
            Write-Fail "ConfigStore:Path in $configPath names '$configDbPath' but no database exists there. The app will not start against a missing shared database (it never creates one). Fix the key, restore the file, or run tools\Move-ConfigDbToShared.ps1 to create the shared database. Nothing was deployed."
        }

        $configDirBackup = Join-Path $BackupDir "config.${timestamp}.bak"
        New-Item -ItemType Directory -Path $configDirBackup -Force | Out-Null

        # Verified online backup of the live DB (throws + aborts the deploy on integrity failure).
        $dbBackup = Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp -AppPath $StagingPath
        if ($dbBackup) {
            Write-Success "Config DB ($configDbPath) backed up (verified) to $dbBackup"
        } else {
            Write-Warn "No config DB at $configDbPath yet -- nothing to back up (it is created on first app start)"
        }

        if (Test-Path $runtimeConfigDir) {
            # Copy the rest of config/: everything except the live databases in $configCopyExclusions -
            # the config DB triplet (backed up above when it is the configured database, a stale leftover
            # otherwise) and the jobs and usage databases, which are never backed up (cdt-10).
            Get-ChildItem -LiteralPath $runtimeConfigDir -File |
                Where-Object { $_.Name -notin $configCopyExclusions } |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $configDirBackup $_.Name) -Force }
            Write-Success "Runtime config directory backed up to $configDirBackup"
        } else {
            Write-Warn "No runtime config directory at $runtimeConfigDir -- only the config DB backup was taken"
        }

        # Pre-deploy snapshot for the post-deploy drift check (incident fix #5): the
        # 2026-06-12 incident surfaced as silent runtime-state divergence with nothing
        # comparing before to after.
        # extended-log-level.txt is rewritten by the app on every startup
        # (ExtendedLogService.LoadLevel -> SetLevel), so it always differs across a
        # pool restart and would false-positive the drift check.
        # The SQLite databases the app keeps in config\ - the config DB, the jobs DB and the usage DB,
        # each with -wal/-shm - change byte-for-byte across a pool restart by design (a clean close
        # checkpoints the WAL into the main file and deletes the sidecars; startup seeds the config DB
        # and interrupts and prunes jobs and telemetry), so a size/mtime comparison is meaningless for
        # them. The config DB is verified by PRAGMA integrity_check after deploy instead (see the
        # post-deploy DB health check); the jobs and usage DBs are operational state that is never
        # backed up (cdt-10), so a drift warning about them would point at a backup without them.
        # The databases come from $configCopyExclusions, the one list of them (cdt-13).
        $driftCheckExclusions = @('extended-log-level.txt') + $configCopyExclusions
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
        # Warn (do not block) if a durable bulk job is active - recycling interrupts it.
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir -AppPath $StagingPath | Out-Null
        # Review of A35: confirming that $AppPoolName stopped proves nothing if the IIS
        # application being mirrored is served by a DIFFERENT pool. A mistyped -AppPoolName
        # creates and stops that wrong pool, and robocopy /MIR then replaces the binaries of an
        # application still running under the real one - the exact failure A35 set out to
        # prevent, reached by another door. Ask IIS which pool serves this app, and refuse when
        # it is not the one about to be stopped.
        $boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
        # cdt-19: both paths configure the application and the pool above, so a binding that cannot be
        # read, or reads as nothing, is IIS not answering - not a fresh install. Passing it would stop
        # a pool nobody has shown serves these files; refuse it as a wrong one is refused.
        if (-not $boundPool) {
            Write-Fail "Cannot read which app pool serves IIS application $ParentSite/$AppAlias (the lookup failed or returned nothing), although this run configured that application above. Stopping '$AppPoolName' cannot be shown to stop what serves $PublishPath, so its files will not be replaced. Nothing was deployed."
        }
        if ($boundPool -ne $AppPoolName) {
            Write-Fail "IIS application $ParentSite/$AppAlias runs under app pool '$boundPool', not '$AppPoolName'. Stopping '$AppPoolName' would leave the running application untouched while its files are replaced. Re-run with -AppPoolName '$boundPool', or fix the binding. Nothing was deployed."
        }

        # cdt-28: the checks above are about one IIS node, $ParentSite/$AppAlias. Another site or
        # application with its files in $PublishPath under a different pool would keep serving them
        # while the mirror replaces them (and this branch never points the alias at $PublishPath).
        # Ask IIS for every node serving the folder, as the promotion script does: the module throws
        # unless there is one and each runs under $AppPoolName, and nothing here catches it.
        # -NestedNodes Refuse (aud-a38j): the mirror below replaces the whole folder, so nothing may
        # be rooted inside it, whatever its pool (aud-a38i).
        $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse

        # A35: the empty catch here swallowed every stop failure, not only the harmless
        # already-stopped one, and the fixed sleep was a guess about how long a worker takes to
        # exit. robocopy /MIR then replaced the binaries of a RUNNING application. Stop and then
        # CONFIRM, and abort before mirroring if it did not stop.
        $poolState = Stop-AppPoolVerified -Name $AppPoolName
        # cdt-19: only a confirmed stop reaches the mirror. The pool was configured above, so 'Absent'
        # means IIS did not find it (an unreadable configuration, a name Test-Path reads as a wildcard
        # pattern), not that nothing is running.
        if ($poolState -ne 'Stopped') {
            Write-Fail "App pool $AppPoolName was reported '$poolState', not confirmed stopped, although this run configured it above. Refusing to replace the files of an application that cannot be shown to be stopped. Nothing was deployed."
        }
        Write-Success "App pool $AppPoolName confirmed stopped"

        # cdt-16: the pool restarts only once the mirror is known to be complete. robocopy /MIR failing
        # part-way leaves a mix of the old and new builds in the publish folder, and the restart in the
        # finally used to serve it. A restart that throws is recorded and reported, never thrown from
        # the finally, where it would replace the error already propagating (Known Failure Class 1);
        # the check after the finally makes it its own failure when nothing else failed.
        $mirrorSucceeded = $false
        $poolStartError = $null
        $manifestError = $null
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
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE, so $PublishPath may now hold a mix of the old and new builds. App pool $AppPoolName was deliberately LEFT STOPPED: starting it would serve that mix. The mirror does not touch the config database ($(if ($dbBackup) { "verified pre-deploy backup: $dbBackup" } else { 'no pre-deploy backup was taken: there was no database yet' })). The staging publish $StagingPath is removed when this script exits, so fix the cause and re-run this deploy from $ProjectRoot; it publishes afresh and starts the pool." }
            $mirrorSucceeded = $true
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
            if ($mirrorSucceeded) {
                Write-Step "Starting app pool"
                # Recorded, not thrown: a throw here would replace whatever is propagating (cdt-16).
                try {
                    Start-AppPoolWithRetry -Name $AppPoolName
                    Write-Success "App pool started"
                } catch {
                    $poolStartError = $_.Exception.Message
                    Write-Host "  X  App pool $AppPoolName did not start: $poolStartError" -ForegroundColor Red
                }
                if ($deploymentReadyForManifest) {
                    # cdt-31: recorded like the restart, never thrown. Unguarded, a failed write here
                    # became the script's error, and the check below that says the application is DOWN
                    # never ran. The checks after the finally report both, the restart's first.
                    try {
                        Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
                    } catch {
                        $manifestError = $_.Exception.Message
                        Write-Host "  X  The deployment manifest was not written: $manifestError" -ForegroundColor Red
                    }
                }
            } else {
                Write-Warn "App pool $AppPoolName is NOT being restarted: the file mirror did not complete, so the pool stays stopped."
            }
            # Staging contains the dev appsettings.json (real environment values).
            # Clean it on failure paths too, not only after a successful deploy.
            Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        # Reached only when the try body succeeded: the deploy is complete and the application is down.
        if ($poolStartError) {
            throw "The files and configuration are deployed, but app pool $AppPoolName did not start ($poolStartError). The application is DOWN until the pool is started by hand.$(if ($manifestError) { " The deployment manifest was not written either ($manifestError)." })"
        }
        # cdt-31: the pool runs, but nothing records which build the folder holds - still a failure.
        if ($manifestError) {
            throw "The files and configuration are deployed and app pool $AppPoolName is running, but the deployment manifest was not written to $PublishPath ($manifestError), so nothing records which build it holds. Fix the cause and re-run this deploy."
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

        # Post-deploy live DB health check. The DB triplet is excluded from the file-drift inventory
        # above (it changes by design across the pool restart via WAL checkpoint + startup seeding),
        # so verify it properly here instead. Starting the pool does not start the application - IIS
        # does that on the first request (aud-a40) - so this verifies the database the new build is
        # about to open, not the result of its migrations (cdt-12 corrected this comment, which said
        # the schema had been migrated by now). A missing or corrupt live DB must be flagged loudly
        # rather than ending on the success banner. Warn (not throw) - the deploy has already happened; this surfaces a bad
        # outcome to the operator with the verified pre-deploy backup as the rollback. The path is
        # resolved again from appsettings.json (deploy never rewrites it) so a shared database is
        # checked where it actually lives (SharedConfigDb-Plan AC6).
        # The staging folder was removed in the finally above, so the tool runs from the publish
        # folder, which now holds the same build.
        $liveDb = Resolve-ConfigDbPath -PublishPath $PublishPath
        if (Test-Path -LiteralPath $liveDb) {
            try {
                Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath $PublishPath | Out-Null
                Write-Success "Config DB verified: integrity_check passed on the live store at $liveDb"
            } catch {
                Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED: $($_.Exception.Message). The app may not function correctly - restore from the pre-deploy backup in $BackupDir and investigate."
            }
        } elseif ($configStorePathSetting) {
            Write-Warn "POST-DEPLOY CHECK: ConfigStore:Path names $liveDb but it does not exist after deploy. The app will not start against a missing shared database - investigate."
        } else {
            Write-Warn "POST-DEPLOY CHECK: no config DB at $liveDb after deploy. If this is the first deploy of the SQLite build it will be created on first app start; otherwise investigate."
        }

    } else {

        # --- FRESH INSTALL ---

        # Read before this branch changes anything. -Force over an existing deployment lands here and
        # regenerates appsettings.json below, which must carry this key (cdt-8). A fresh install has no
        # appsettings.json, so this is $null and nothing is carried. A file that does not parse stops the
        # run here (ConvertFrom-Json raises a terminating error and "Stop" makes it fatal), which is the
        # fail-closed answer: overwriting a file this script cannot read could drop the key. Both reads
        # are of the serving folder - the configuration the application runs on now - which is
        # -PublishPath except when -AllowRepoint moves the application to another folder (cdt-44).
        $configStorePathSetting = Get-ConfigStorePathSetting -PublishPath $servingFolder

        # The database the instance opens: ConfigStore:Path when set (the shared file, outside config\),
        # else config\exchangeadmin.db. A configured file that is missing stops the run here, as on an
        # upgrade, before anything is stopped or mirrored: the regenerated appsettings.json would point
        # the app at it and the app never creates a configured database (cdt-9). A fresh install has no
        # key, so this cannot fire there.
        $configDbPath = Resolve-ConfigDbPath -PublishPath $servingFolder
        if ($configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)) {
            Write-Fail "ConfigStore:Path in $servingConfigPath names '$configDbPath' but no database exists there. The app will not start against a missing shared database (it never creates one). Fix the key, restore the file, or run tools\Move-ConfigDbToShared.ps1 to create the shared database. Nothing was deployed."
        }
        if ($configStorePathSetting) {
            Write-Success "ConfigStore:Path in the existing appsettings.json is kept: $configStorePathSetting"
        }

        # Architectural Invariant 3 on -Force: the verified online backup the upgrade path takes, in the
        # same layout - one timestamped directory holding the backup of the database the instance opens
        # (VACUUM INTO + integrity_check; it throws and aborts the deploy on failure) and the rest of
        # config\ minus the live databases in $configCopyExclusions. It used to be a raw recursive copy
        # of config\, taken before the publish and verified by nothing, that could be torn and never
        # touched a shared database (cdt-9). It sits after the publish because the tool is the build in
        # staging, and before the IIS changes, the job check, the pool stop and the mirror. A real fresh
        # install (no appsettings.json) has nothing to back up. The gate is the serving folder's
        # appsettings.json (cdt-44): outside a move that is the old gate - a -ConfirmFreshInstall run
        # has none there, or it would be an upgrade - and a -AllowRepoint move, -Force or not, backs up
        # the configuration it is moving the application off.
        if (Test-Path -LiteralPath $servingConfigPath) {
            Write-Step "Backing up the existing runtime configuration"
            $configDirBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"
            New-Item -ItemType Directory -Path $configDirBackup -Force | Out-Null

            $dbBackup = Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp -AppPath $StagingPath
            if ($dbBackup) {
                Write-Success "Config DB ($configDbPath) backed up (verified) to $dbBackup"
            } else {
                Write-Warn "No config DB at $configDbPath yet -- nothing to back up (it is created on first app start)"
            }

            if (Test-Path $runtimeConfigDir) {
                Get-ChildItem -LiteralPath $runtimeConfigDir -File |
                    Where-Object { $_.Name -notin $configCopyExclusions } |
                    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $configDirBackup $_.Name) -Force }
                Write-Success "Runtime config directory backed up to $configDirBackup"
            } else {
                Write-Warn "No runtime config directory at $runtimeConfigDir -- only the config DB backup was taken"
            }
        }

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
        # Warn (do not block) if a durable bulk job is active - recycling interrupts it.
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir -AppPath $StagingPath | Out-Null
        # Review of A35: confirming that $AppPoolName stopped proves nothing if the IIS
        # application being mirrored is served by a DIFFERENT pool. A mistyped -AppPoolName
        # creates and stops that wrong pool, and robocopy /MIR then replaces the binaries of an
        # application still running under the real one - the exact failure A35 set out to
        # prevent, reached by another door. Ask IIS which pool serves this app, and refuse when
        # it is not the one about to be stopped.
        $boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
        # cdt-19: both paths configure the application and the pool above, so a binding that cannot be
        # read, or reads as nothing, is IIS not answering - not a fresh install. Passing it would stop
        # a pool nobody has shown serves these files; refuse it as a wrong one is refused.
        if (-not $boundPool) {
            Write-Fail "Cannot read which app pool serves IIS application $ParentSite/$AppAlias (the lookup failed or returned nothing), although this run configured that application above. Stopping '$AppPoolName' cannot be shown to stop what serves $PublishPath, so its files will not be replaced. Nothing was deployed."
        }
        if ($boundPool -ne $AppPoolName) {
            Write-Fail "IIS application $ParentSite/$AppAlias runs under app pool '$boundPool', not '$AppPoolName'. Stopping '$AppPoolName' would leave the running application untouched while its files are replaced. Re-run with -AppPoolName '$boundPool', or fix the binding. Nothing was deployed."
        }

        # cdt-28: the checks above are about one IIS node, $ParentSite/$AppAlias. Another site or
        # application with its files in $PublishPath under a different pool would keep serving them
        # while the mirror replaces them. Ask IIS for every node serving the folder, as the promotion
        # script does: the module throws unless there is one and each runs under $AppPoolName, and
        # nothing here catches it. -NestedNodes Refuse (aud-a38j): the mirror below replaces the whole
        # folder, so nothing may be rooted inside it, whatever its pool (aud-a38i).
        $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse

        # A35: the empty catch here swallowed every stop failure, not only the harmless
        # already-stopped one, and the fixed sleep was a guess about how long a worker takes to
        # exit. robocopy /MIR then replaced the binaries of a RUNNING application. Stop and then
        # CONFIRM, and abort before mirroring if it did not stop.
        $poolState = Stop-AppPoolVerified -Name $AppPoolName
        # cdt-19: only a confirmed stop reaches the mirror. The pool was configured above, so 'Absent'
        # means IIS did not find it (an unreadable configuration, a name Test-Path reads as a wildcard
        # pattern), not that nothing is running.
        if ($poolState -ne 'Stopped') {
            Write-Fail "App pool $AppPoolName was reported '$poolState', not confirmed stopped, although this run configured it above. Refusing to replace the files of an application that cannot be shown to be stopped. Nothing was deployed."
        }
        Write-Success "App pool $AppPoolName confirmed stopped"

        # cdt-16: the pool restarts only once the mirror is known to be complete. robocopy /MIR failing
        # part-way leaves a mix of the old and new builds in the publish folder, and the restart in the
        # finally used to serve it. A restart that throws is recorded and reported, never thrown from
        # the finally, where it would replace the error already propagating (Known Failure Class 1);
        # the check after the finally makes it its own failure when nothing else failed.
        $mirrorSucceeded = $false
        $poolStartError = $null
        $manifestError = $null
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
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE, so $PublishPath may now hold a mix of the old and new builds. App pool $AppPoolName was deliberately LEFT STOPPED: starting it would serve that mix. The mirror does not touch the config database ($(if ($dbBackup) { "verified pre-deploy backup: $dbBackup" } else { 'no pre-deploy backup was taken: there was no database yet' })). The staging publish $StagingPath is removed when this script exits, so fix the cause and re-run this deploy from $ProjectRoot; it publishes afresh and starts the pool." }
            $mirrorSucceeded = $true
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
                    PublicBaseUrl = $PublicBaseUrl
                }
                AllowedHosts = "*"
            }

            Write-AppSettingsFile -Settings $config -Path $configPath -ConfigStorePath $configStorePathSetting
            if ($configStorePathSetting) {
                Write-Success "appsettings.json generated, ConfigStore:Path kept: $configStorePathSetting"
            } else {
                Write-Success "appsettings.json generated"
            }
            $deploymentReadyForManifest = $true
        } finally {
            if ($mirrorSucceeded) {
                Write-Step "Starting app pool"
                # Recorded, not thrown: a throw here would replace whatever is propagating (cdt-16).
                try {
                    Start-AppPoolWithRetry -Name $AppPoolName
                    Write-Success "App pool started"
                } catch {
                    $poolStartError = $_.Exception.Message
                    Write-Host "  X  App pool $AppPoolName did not start: $poolStartError" -ForegroundColor Red
                }
                if ($deploymentReadyForManifest) {
                    # cdt-31: recorded like the restart, never thrown. Unguarded, a failed write here
                    # became the script's error, and the check below that says the application is DOWN
                    # never ran. The checks after the finally report both, the restart's first.
                    try {
                        Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
                    } catch {
                        $manifestError = $_.Exception.Message
                        Write-Host "  X  The deployment manifest was not written: $manifestError" -ForegroundColor Red
                    }
                }
            } else {
                Write-Warn "App pool $AppPoolName is NOT being restarted: the file mirror did not complete, so the pool stays stopped."
            }
            # Staging contains the checkout's published appsettings.json (real environment values), not
            # the generated one; clean on failure paths too.
            Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        # Reached only when the try body succeeded: the deploy is complete and the application is down.
        if ($poolStartError) {
            throw "The files and configuration are deployed, but app pool $AppPoolName did not start ($poolStartError). The application is DOWN until the pool is started by hand.$(if ($manifestError) { " The deployment manifest was not written either ($manifestError)." })"
        }
        # cdt-31: the pool runs, but nothing records which build the folder holds - still a failure.
        if ($manifestError) {
            throw "The files and configuration are deployed and app pool $AppPoolName is running, but the deployment manifest was not written to $PublishPath ($manifestError), so nothing records which build it holds. Fix the cause and re-run this deploy."
        }

        # --- Post-deploy live DB health check (cdt-12) ---
        # The upgrade path above has always ended with this check; -Force, the path that
        # regenerates appsettings.json and so the one most able to point the app at a different,
        # missing or damaged database, ended on the success banner without it. Same check, same
        # posture: the files are deployed and the pool is running, so a failure WARNS and names the
        # pre-deploy backup instead of throwing. Only reached on success (a throw above propagates
        # past it). The path is resolved from the regenerated appsettings.json, which carries the
        # kept ConfigStore:Path (cdt-8), so this checks the file the app will open; staging was
        # removed in the finally above, so the tool runs from the publish folder, which holds the
        # same build. Starting the pool does not start the application - IIS does that on the first
        # request (aud-a40) - so this verifies the database the new build is about to open, not the
        # result of its migrations. A genuine fresh install has no database yet and gets the warning
        # that says it is created on first start.
        $liveDb = Resolve-ConfigDbPath -PublishPath $PublishPath
        if (Test-Path -LiteralPath $liveDb) {
            try {
                Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath $PublishPath | Out-Null
                Write-Success "Config DB verified: integrity_check passed on the live store at $liveDb"
            } catch {
                Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED: $($_.Exception.Message). The app may not function correctly - restore from the pre-deploy backup in $BackupDir and investigate."
            }
        } elseif ($configStorePathSetting) {
            Write-Warn "POST-DEPLOY CHECK: ConfigStore:Path names $liveDb but it does not exist after deploy. The app will not start against a missing shared database - investigate."
        } else {
            Write-Warn "POST-DEPLOY CHECK: no config DB at $liveDb after deploy. If this is the first deploy of the SQLite build it will be created on first app start; otherwise investigate."
        }
    }
} finally {
    Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
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
    Write-Host "    - Set Application:PublicBaseUrl to $siteUrl so notification emails can link to in-app pages" -ForegroundColor Yellow
    Write-Host "    - Security:AdminGroups controls /admin-settings and module config pages" -ForegroundColor Yellow
    Write-Host "    - Use per-module config pages in the sidebar to set section access" -ForegroundColor Yellow
    Write-Host "    - Use Module Config to set Excluded Users for Mailbox/Calendar protection" -ForegroundColor Yellow
}

Write-Host ""

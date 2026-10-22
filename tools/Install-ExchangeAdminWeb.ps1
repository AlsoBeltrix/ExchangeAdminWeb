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

    Hardening (optional):
      - -NarrowPublishAcl grants the app pool identity ReadAndExecute rather than Modify on
        the publish folder, so the identity running the binaries cannot rewrite them. logs\,
        config\ and any shared config directory keep Modify. Validate on dev first: a module
        that writes under the publish root would fail only after the pool restarts.

    Update:
      - Builds the app from the current source tree.
      - Takes a verified backup of an existing config database into <LogRoot>\ConfigBackups
        before mirroring new binaries. The backup runs the freshly built app's own db mode
        through <ProgramFiles>\dotnet\dotnet.exe (no sqlite3.exe needed); pass -SkipConfigBackup
        to upgrade without a snapshot.
      - Keeps the app pool's existing custom identity when -ServiceAccount is omitted.
        Pass -ServiceAccount to change it, or -ResetAppPoolIdentity to move it back to
        ApplicationPoolIdentity.
      - Updates the IIS application in place: -AppPoolName and -PublishPath must be the pool and
        folder IIS already serves it from (both default to fixed values when omitted). A run that
        would move an existing application is refused before anything changes.
      - Copies binaries/static files while preserving appsettings.json, config, and logs.
      - Seeds only newly missing config fragments.

    Shared config database (optional, -ConfigStorePath): when several instances on one server
    are meant to open ONE config database, pass the absolute local path of that database (a
    drive letter, a colon, then \ or /). The installer writes it, with backslashes, as
    ConfigStore:Path in the generated appsettings.json and grants the
    app pool identity Modify on the shared directory in addition to the instance's own config
    folder (which is still created and ACLed: the per-instance jobs database and the first-run
    seed files live there). The shared database must already exist - the installer never
    creates it; tools\Move-ConfigDbToShared.ps1 does, once, from an existing instance.
    Without -ConfigStorePath an existing instance keeps the ConfigStore:Path its appsettings.json
    already names - also under -RegenerateAppSettings, which regenerates every other value - and
    a new instance uses its own config\exchangeadmin.db. A -ConfigStorePath naming a different
    database from the one an existing appsettings.json names is refused, -RegenerateAppSettings or
    not: moving an instance to another config database is tools\Move-ConfigDbToShared.ps1's job.

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
    [string]$PublicBaseUrl,

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

    [string]$ConfigStorePath,
    [switch]$RegenerateAppSettings,
    # A34: moving an existing pool back to ApplicationPoolIdentity is destructive to every
    # access the running account has, so it is opt-in rather than the default for an omitted
    # -ServiceAccount. Modelled on -RegenerateAppSettings above.
    [switch]$ResetAppPoolIdentity,
    # A36: an upgrade starts new binaries against an existing config database, and those
    # binaries may run schema migrations on first start. Skipping the pre-upgrade snapshot
    # is a deliberate acceptance of having no way back, so it is a switch, not a default.
    [switch]$SkipConfigBackup,
    # Operational hardening (lower-priority Wave 4). The publish folder holds the binaries
    # the app pool executes; the identity that runs them does not need to be able to rewrite
    # them. With this switch the publish folder is granted ReadAndExecute instead of Modify,
    # which removes the "a compromised worker can overwrite its own DLLs" step.
    #
    # OPT-IN on purpose. It cannot be validated from a build machine: the app is an IIS-
    # hosted ASP.NET Core application and anything it writes under the publish root - a
    # module that caches to disk, Data Protection falling back to the content root, a file
    # provider - would start failing only once the pool restarts in a real environment.
    # Shipping a silent permission reduction to every installation on reasoning alone is the
    # kind of change that breaks production at a moment nobody connects to the deploy.
    #
    # To adopt it: run a dev install with -NarrowPublishAcl, exercise the modules, confirm
    # the app log is clean, and then make it the default here.
    [switch]$NarrowPublishAcl,
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

    # cdt-34: resolved against PowerShell's current location first. GetFullPath alone resolves a
    # relative path against the PROCESS directory, which Set-Location does not move, so a relative
    # -PublishPath or -LogRoot landed under wherever the PowerShell process had started.
    $full = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\', '/')
    if ($full.TrimEnd('\', '/') -eq $root) {
        Write-Fail "$Name cannot be a drive root: $full"
    }
    return $full.TrimEnd('\', '/')
}

function Assert-LocalAbsoluteFilePath {
    # cdt-37: no copy of the rule here. The installer's own copy - a drive letter, a colon and a
    # BACKSLASH - refused D:/..., a path the app opens and the read side reads back, so this asks the
    # sibling SqliteConfigBackup module (imported before the one call below), whose
    # Get-ConfigStorePathForm is the scripts' one copy of the app's ConfigStorePath.Resolve rule:
    # any two leading separators name a network share, and only an ASCII drive letter, a colon,
    # then \ or / is an absolute local path. "D:file.db" names a drive but resolves against that
    # drive's current directory, so two instances could open two different files (review finding
    # scdi-2). Anything but Local is refused.
    param([string]$Path, [string]$Name)

    $form = Get-ConfigStorePathForm -Value $Path
    if ($form -eq 'NetworkShare') {
        Write-Fail "$Name must be a local path, not a network share: $Path"
    }
    if ($form -ne 'Local') {
        Write-Fail "$Name must be an absolute local file path (a drive letter, a colon, then \ or / - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Path"
    }
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

    # cdt-11: name a carried ConfigStore:Path, so a plan shows the key is kept.
    $configStoreNote = ''
    if ($Settings.ContainsKey('ConfigStore')) { $configStoreNote = " with ConfigStore:Path = $($Settings['ConfigStore']['Path'])" }
    Invoke-PlanOrAction "Write appsettings.json at $Path$configStoreNote" {
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
    # cdt-26: a git status that failed is unknown, not clean. Windows PowerShell 5.1 turns its stderr
    # into an error caught here; PowerShell 7 returns nothing with a non-zero exit. The old [bool](...)
    # read both as $false, so anything reading the manifest was told the tree was clean.
    $gitDirty = 'unknown'
    try {
        $statusOutput = git -C $RepoRoot status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0) { $gitDirty = [bool]$statusOutput }
    } catch {
        $gitDirty = 'unknown'
    }

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
        [string[]]$SecurityAdminGroups,
        [string]$ConfigStorePath
    )

    $settings = [ordered]@{
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
            PublicBaseUrl = $PublicBaseUrl
        }
        AllowedHosts = "*"
    }

    # Only when a shared database was named: without the key the app uses its own
    # config\exchangeadmin.db, and the generated file must stay byte-identical to before.
    if (-not [string]::IsNullOrWhiteSpace($ConfigStorePath)) {
        $settings.ConfigStore = [ordered]@{
            Path = $ConfigStorePath
        }
    }

    return $settings
}

function Resolve-ConfigStorePlan {
    # cdt-11. Decides ONCE, before anything is written, which ConfigStore:Path this run leaves in
    # appsettings.json - and so which config database the instance opens afterwards, which is the
    # one the banner reports and the pre-upgrade backup snapshots. -RegenerateAppSettings used to
    # rebuild the file from parameters alone, so an omitted -ConfigStorePath silently dropped the
    # key naming a shared database, and the backup then re-read the rewritten file and snapshotted
    # the stale per-instance copy instead.
    #
    # When this run writes the file, a supplied path is written - over a file with no key to replace;
    # a supplied path naming a different database from the existing key is refused in every mode
    # (cdt-29). Otherwise the existing key is carried exactly as Get-ConfigStorePathSetting reads it.
    # A file this run keeps keeps its own key, and a supplied path it would not carry is refused
    # (cdt-20). A file that does not parse stops the run here, before anything changes: overwriting
    # a file the installer cannot read could drop the key.
    param(
        [string]$PublishPath,
        [string]$SuppliedPath,
        [bool]$RegenerateAppSettings
    )

    $appSettingsExists = Test-Path -LiteralPath (Join-Path $PublishPath 'appsettings.json') -PathType Leaf
    $existingPath = Get-ConfigStorePathSetting -PublishPath $PublishPath
    # Write-AppSettings' own rule: it writes when there is no file, or when told to regenerate.
    $writesAppSettings = (-not $appSettingsExists) -or $RegenerateAppSettings
    $suppliedPathGiven = -not [string]::IsNullOrWhiteSpace($SuppliedPath)
    $suppliedIsExisting = $suppliedPathGiven -and $existingPath -and [string]::Equals([System.IO.Path]::GetFullPath($existingPath).TrimEnd('\'), [System.IO.Path]::GetFullPath($SuppliedPath).TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)

    # cdt-29. The existing key names the database this instance opens - on a shared-database server
    # the one its sibling opens too. -RegenerateAppSettings -ConfigStorePath <another file> used to
    # write the other file's path over it: the instance moved to a different authorization and
    # settings store, the shared-folder grant and the pre-upgrade backup followed the new path, and
    # nothing said so. Refused here, in plan and apply mode alike, before anything is written; the
    # same database restated (compared as cdt-20 compares it) is fine, and no key at all is the first
    # configuration, which takes the supplied path.
    if ($suppliedPathGiven -and $existingPath -and -not $suppliedIsExisting) {
        $refusal = "-ConfigStorePath $SuppliedPath was given, but the existing appsettings.json in $PublishPath names ConfigStore:Path = $existingPath, a different config database. This installer does not move an existing instance from one config database to another, with or without -RegenerateAppSettings: the instance would open a different authorization and settings store from its next start, and the pre-upgrade backup would snapshot the new database instead of the one in use. Changing the database an existing instance opens is tools\Move-ConfigDbToShared.ps1's job - the only sanctioned way. Re-run without -ConfigStorePath to keep $existingPath, or with -ConfigStorePath $existingPath. Nothing was changed."
        if ($PlanOnly) { Write-Plan "REFUSED: $refusal" }
        Write-Fail $refusal
    }

    # cdt-20. A kept file is not rewritten, so a supplied path it does not already name would never
    # take effect: the run used to validate it, ACL its folder and complete while the instance kept
    # its own database - two instances believed to share settings and authorization that did not.
    # Re-stating the key the file already names is harmless and accepted.
    if (-not $writesAppSettings -and $suppliedPathGiven -and -not $existingPath) {
        Write-Fail "-ConfigStorePath $SuppliedPath was given, but this run keeps the existing appsettings.json in $PublishPath, which names no ConfigStore:Path, so the supplied path would not take effect. Re-run with -RegenerateAppSettings to write it (that regenerates the file's other values too), or without -ConfigStorePath to keep the instance as it is. Moving an existing instance onto a shared database is tools\Move-ConfigDbToShared.ps1's job. Nothing was changed."
    }

    $path = $null
    $source = 'None'
    if ($writesAppSettings -and -not [string]::IsNullOrWhiteSpace($SuppliedPath)) {
        $path = $SuppliedPath
        $source = 'Supplied'
    } elseif ($existingPath) {
        $path = $existingPath
        $source = 'Existing'
    }

    # No key: the per-instance default, from the same resolver the app's rule is mirrored in.
    if ($path) {
        $dbPath = $path
    } else {
        $dbPath = Resolve-ConfigDbPath -PublishPath $PublishPath
    }

    $summary = "$dbPath (this instance only)"
    if ($source -eq 'Supplied') { $summary = "$path (shared, from -ConfigStorePath)" }
    if ($source -eq 'Existing') { $summary = "$path (shared, ConfigStore:Path kept from the existing appsettings.json)" }

    return [pscustomobject]@{
        Path              = $path
        Source            = $source
        DbPath            = $dbPath
        WritesAppSettings = $writesAppSettings
        Summary           = $summary
    }
}

function Assert-ConfigStorePlanDatabase {
    # cdt-21. The app opens a configured database without creating it, so a decided key naming a
    # missing file is an instance that cannot start. A supplied path was refused early; a kept one
    # was caught only inside the backup step - after the host had been reconfigured, never in a plan,
    # and not at all with -SkipConfigBackup, which then mirrored and reported success. No key needs
    # no check: the per-instance default is created on first start.
    param($Plan, [string]$AppSettingsPath)

    if (-not $Plan.Path) { return }
    if (Test-Path -LiteralPath $Plan.Path -PathType Leaf) { return }

    $from = "the existing $AppSettingsPath"
    if ($Plan.Source -eq 'Supplied') { $from = '-ConfigStorePath' }
    Write-Fail "ConfigStore:Path from $from names '$($Plan.Path)' but no database exists there. The app opens a configured database without creating it, so this instance cannot start on it. Fix the key, restore the file, run tools\Move-ConfigDbToShared.ps1, or re-run with -RegenerateAppSettings -ConfigStorePath naming a database that exists. Nothing was changed."
}

function ConvertTo-InstallerComparablePath {
    # One spelling per folder (cdt-15): environment variables expanded (IIS often stores
    # %SystemDrive%\...), made absolute and normalised, trailing separators dropped. Callers
    # compare the results by equality, case-insensitively - never by prefix, because one
    # instance's folder is easily a prefix of a sibling's.
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    try {
        $full = [System.IO.Path]::GetFullPath($expanded)
    } catch {
        $full = $expanded
    }
    return $full.TrimEnd('\', '/')
}

function Assert-ApplicationTargetUnchanged {
    # cdt-15. "Create or update IIS web application" below sets an EXISTING application's pool and
    # folder to this run's -AppPoolName and -PublishPath, and both default to fixed values when
    # omitted. So an update that did not repeat a customised value moved the live application: to a
    # new pool, created with ApplicationPoolIdentity (the identity its credential store, certificate
    # key and directory access depend on, lost - aud-a34 by another door), and/or to a new folder
    # with no appsettings.json, which this run then generates from defaults (a fresh, empty instance
    # at the same URL - the cdt-8 outcome). An existing application must be updated in place; moving
    # one is an IIS change made deliberately first, not a side effect of an omitted parameter.
    param(
        [string]$Location,
        [bool]$Exists,
        [string]$ExistingPool,
        [string]$ExistingPhysicalPath,
        [string]$AppPoolName,
        [string]$PublishPath
    )

    if (-not $Exists) { return }

    $differences = @()
    if (-not [string]::Equals($ExistingPool, $AppPoolName, [System.StringComparison]::OrdinalIgnoreCase)) {
        $differences += "app pool '$ExistingPool' (this run: -AppPoolName '$AppPoolName')"
    }
    $existingFolder = ConvertTo-InstallerComparablePath $ExistingPhysicalPath
    $runFolder = ConvertTo-InstallerComparablePath $PublishPath
    if ([string]::IsNullOrWhiteSpace($existingFolder) -or -not [string]::Equals($existingFolder, $runFolder, [System.StringComparison]::OrdinalIgnoreCase)) {
        $differences += "files at '$ExistingPhysicalPath' (this run: -PublishPath '$PublishPath')"
    }
    if ($differences.Count -gt 0) {
        Write-Fail "IIS application $Location already exists, with $($differences -join ' and '). An update must name the pool and folder it was installed with - -AppPoolName and -PublishPath default to fixed values when omitted - or this run would move the live application. Re-run with -AppPoolName '$ExistingPool' -PublishPath '$ExistingPhysicalPath', or move the application in IIS first if that is intended. Nothing was changed."
    }
}

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$defaultPublishPath = Join-Path $env:SystemDrive "inetpub\$AppAlias"
$defaultLogRoot = Join-Path $env:ProgramData "ExchangeAdminWeb\Logs"

$PublishPath = Read-Value "PublishPath" "Publish path" $PublishPath $defaultPublishPath
$LogRoot = Read-Value "LogRoot" "Audit/log root" $LogRoot $defaultLogRoot
$PathBase = Read-Value "PathBase" "Application path base" $PathBase "/$AppAlias"
$ContactEmail = Read-Value "ContactEmail" "Support/contact email shown in the app" $ContactEmail ""
# Absolute external URL of the app (e.g. https://server/ExchangeAdminWeb). Notification emails use
# it to link to in-app pages; an email client cannot resolve a relative path. Blank is supported and
# environment-neutral: the app omits the hyperlink and describes the page in prose instead.
$PublicBaseUrl = Read-Value "PublicBaseUrl" "Absolute external URL of the app for email links (blank to configure later)" $PublicBaseUrl ""
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

# A36. A sibling module that names no host, domain or configuration, so importing it keeps this
# installer standalone and environment-neutral (Architectural Invariant 1). Imported before the
# banner because the config-store decision below reads appsettings.json through it (cdt-11).
Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force

# Optional shared config database. The path must be absolute and local (SQLite locking is not
# reliable over a network share) and the file MUST already exist: the app opens a configured
# path without create and stops if it is missing, so the installer refuses here rather than
# hand over an instance that cannot start. Creating the shared database from an existing
# instance is tools\Move-ConfigDbToShared.ps1's job, run once.
if (-not [string]::IsNullOrWhiteSpace($ConfigStorePath)) {
    Assert-LocalAbsoluteFilePath -Path $ConfigStorePath -Name 'ConfigStorePath'
    # cdt-37: GetFullPath also turns / into \ (under Windows PowerShell 5.1 and 7 alike), so the
    # banner, the plan, the comparisons and the appsettings.json write below all carry the one
    # spelling the read side returns.
    $ConfigStorePath = [System.IO.Path]::GetFullPath($ConfigStorePath)
    if (-not (Test-Path -LiteralPath $ConfigStorePath -PathType Leaf)) {
        Write-Fail "ConfigStorePath names $ConfigStorePath but no database exists there. The installer never creates the shared database; create it once from an existing instance with tools\Move-ConfigDbToShared.ps1, then re-run the installer."
    }
}

# cdt-11. Decided here, once: after -ConfigStorePath is validated, before the banner and before
# anything is changed or written. The banner, the appsettings.json write and the pre-upgrade
# backup all read this plan; nothing re-reads ConfigStore:Path from the file this run may rewrite.
$configStorePlan = Resolve-ConfigStorePlan -PublishPath $PublishPath -SuppliedPath $ConfigStorePath -RegenerateAppSettings $RegenerateAppSettings.IsPresent
# cdt-21: in plan and apply mode alike, and whatever -SkipConfigBackup says.
Assert-ConfigStorePlanDatabase -Plan $configStorePlan -AppSettingsPath $configPath
# cdt-22: the shared database's folder follows the decision - the key the instance will open, kept
# or supplied - not the parameter, so a run that keeps the key and changes the pool identity still
# grants the new identity the folder SQLite writes -wal/-shm into.
$sharedConfigDir = $null
if ($configStorePlan.Path) {
    $sharedConfigDir = Split-Path -Parent $configStorePlan.Path
}

Write-Host ""
Write-Host "ExchangeAdminWeb installer" -ForegroundColor Magenta
Write-Host "  Source      : $repoRoot" -ForegroundColor DarkGray
Write-Host "  Site        : $ParentSite/$AppAlias" -ForegroundColor DarkGray
Write-Host "  App pool    : $AppPoolName" -ForegroundColor DarkGray
Write-Host "  Publish     : $PublishPath" -ForegroundColor DarkGray
Write-Host "  PathBase    : $PathBase" -ForegroundColor DarkGray
Write-Host "  Log root    : $LogRoot" -ForegroundColor DarkGray
Write-Host "  Config DB   : $($configStorePlan.Summary)" -ForegroundColor DarkGray
Write-Host "  Mode        : $(if ($PlanOnly) { 'PLAN ONLY' } else { 'APPLY' })" -ForegroundColor DarkGray
Write-Host ""

if (-not $PlanOnly -and -not (Test-IsAdministrator)) {
    Write-Fail "Run this installer from an elevated PowerShell session, or use -PlanOnly for a non-elevated preview."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail "dotnet CLI was not found. Install the .NET SDK before running the installer."
}

# A35/A34. Ships beside this script and is environment-neutral - it names no host, domain
# or configuration - so importing it keeps this installer standalone and uncoupled from any
# environment-specific deploy tooling (Architectural Invariant 1). Imported outside the
# PlanOnly guard because the identity decision below is reported during a dry run too.
Import-Module (Join-Path $PSScriptRoot 'AppPoolControl.psm1') -Force

if ($PlanOnly) {
    Write-Plan "Import WebAdministration and verify IIS site '$ParentSite'"
    # Review of A34: plan mode forced $poolExists = $false, so the identity plan could never
    # see an existing custom identity and always reported ApplicationPoolIdentity and the
    # virtual account's ACLs - the opposite of what apply would do. Importing the module and
    # reading pool state are side-effect free, so a dry run can and must do both. A machine
    # without IIS still plans, with the limitation stated rather than hidden.
    try {
        Import-Module WebAdministration -ErrorAction Stop
    } catch {
        Write-Warn "WebAdministration is unavailable here, so this plan cannot read the existing app pool. The identity decision below assumes a NEW pool; a real run on the target host may preserve an existing identity instead."
    }
} else {
    Import-Module WebAdministration -ErrorAction Stop
    if (-not (Get-Website -Name $ParentSite -ErrorAction SilentlyContinue)) {
        Write-Fail "IIS site '$ParentSite' was not found."
    }
}

# cdt-15. Read the application this run is about to update, before anything about the pool is
# decided or changed, and refuse a run that would move it. Side-effect free, so a plan does it too;
# a plan that cannot read IIS says so rather than implying the check passed.
$existingApplication = $null
$applicationLookupFailed = $false
try {
    $existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop
} catch {
    $applicationLookupFailed = $true
}
if ($applicationLookupFailed) {
    if ($PlanOnly) {
        Write-Warn "This plan cannot read IIS application $ParentSite/$AppAlias, so it cannot check that the application is updated in place; a real run refuses if IIS serves it from a different folder or pool."
    } else {
        Write-Fail "Cannot read IIS application $ParentSite/$AppAlias, so it cannot be confirmed that this run updates it in place rather than moving it. Nothing was changed."
    }
}
Assert-ApplicationTargetUnchanged -Location "$ParentSite/$AppAlias" -Exists ($null -ne $existingApplication) -ExistingPool "$($existingApplication.applicationPool)" -ExistingPhysicalPath "$($existingApplication.PhysicalPath)" -AppPoolName $AppPoolName -PublishPath $PublishPath

# Read it in both modes - see the plan-mode note above. Guarded because the IIS provider
# may not exist on a machine that is only producing a plan.
$poolExists = try { [bool](Test-Path "IIS:\AppPools\$AppPoolName") } catch { $false }

# A34. Read what the pool runs as TODAY before deciding anything. An omitted -ServiceAccount
# used to mean "set identityType 4", so an update silently moved a pool off its domain
# service account - and the Set-DirectoryAcl calls below then granted Modify to the virtual
# account instead of to the one the app actually runs as.
$existingIdentityType = $null
$existingUser = $null
if ($poolExists) {
    $existingProcessModel = Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel
    $existingIdentityType = [int]$existingProcessModel.identityType
    $existingUser = $existingProcessModel.userName
}

$identityPlan = Resolve-AppPoolIdentityPlan `
    -ServiceAccount $ServiceAccount `
    -AppPoolName $AppPoolName `
    -PoolExists $poolExists `
    -ExistingIdentityType $existingIdentityType `
    -ExistingUserName $existingUser `
    -ResetRequested $ResetAppPoolIdentity.IsPresent

if ($identityPlan.Reason) { Write-Warn $identityPlan.Reason }

# Every folder ACL below is granted to this. Preserving an identity and then ACLing the
# wrong principal would be the same outage by a longer route.
$appPoolIdentity = $identityPlan.IdentityForAcl

if ($PlanOnly) {
    Write-Plan "App pool identity: $($identityPlan.Action) (an existing custom identity is kept unless -ServiceAccount or -ResetAppPoolIdentity is passed)"
}

$needsPassword = $false
if ($ServiceAccount -and -not $PlanOnly) {
    $needsPassword = (-not $poolExists) -or ($existingUser -ne $ServiceAccount)
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

    # A34: three outcomes, not two. PreserveExisting deliberately touches nothing - an
    # omitted parameter is not an instruction to change the identity.
    if ($identityPlan.Action -eq 'SetCustom') {
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 3
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName -Value $ServiceAccount
        if ($ServiceAccountPassword) {
            Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value (ConvertTo-PlainText $ServiceAccountPassword)
        }
    } elseif ($identityPlan.Action -eq 'SetApplicationPoolIdentity') {
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 4
    }
}

# logs\, config\ and the shared config directory keep Modify below - those are the paths
# the app genuinely writes. Only the publish root narrows.
$publishRights = if ($NarrowPublishAcl) { "(OI)(CI)RX" } else { "(OI)(CI)M" }
if ($NarrowPublishAcl) {
    Write-Warn "-NarrowPublishAcl: granting $appPoolIdentity ReadAndExecute (not Modify) on $PublishPath. If any module writes under the publish root, it will fail after the pool restarts - verify on dev before using this in production."
}
Set-DirectoryAcl -Path $PublishPath -Identity $appPoolIdentity -Rights $publishRights
Set-DirectoryAcl -Path $LogRoot -Identity $appPoolIdentity -Rights "(OI)(CI)M"
Set-DirectoryAcl -Path $configDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"
Set-DirectoryAcl -Path $appLogDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"
if ($sharedConfigDir) {
    # In ADDITION to the instance's own config\ above (the jobs database and the seed files
    # still live there): the shared database's directory needs Modify too, for the WAL/SHM
    # sidecars SQLite writes next to the file.
    Set-DirectoryAcl -Path $sharedConfigDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"
}

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
    -SecurityAdminGroups $adminGroupsQualified `
    -ConfigStorePath $configStorePlan.Path)

# Config seeds (SQLite note): runtime config now lives in config/exchangeadmin.db, created by
# the app on first start. These JSON seeds are written only if missing and are consumed as
# ONE-TIME IMPORT SEED - on first start the app imports each into the DB and archives the file
# as *.imported-<timestamp> (SqliteConfigStore-Plan section 4). So a fresh install still gets correct
# initial config (incl. the section-access authorization seed) without this script needing to
# speak SQLite. The config/ ACL granted above is (OI)(CI)M, so the DB the app creates inside
# config/ inherits Modify for the pool identity (WAL needs write) - no DB-specific ACL needed.
# This script stays environment-neutral and standalone. It does not speak SQLite to SEED
# config - the JSON seeds above are the whole mechanism - but as of A36 it does take a
# verified backup of an existing database before an upgrade, via the sibling
# SqliteConfigBackup module. That module names no host or environment either.
Write-JsonFileIfMissing -Path (Join-Path $configDir "sectionaccess.json") -Value (New-SectionAccessSeed -Groups $adminGroupsQualified) -Description "section access config (first-run import seed)"
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

# cdt-23: the staging publish holds a full copy of the build and any appsettings.json the source tree
# carries. It used to be removed only after a successful mirror, so every failure from here on left it
# beside the publish folder. The finally removes it on every path; -ErrorAction SilentlyContinue keeps
# a failed removal from replacing the error that is propagating. A plan creates none.
try {
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

    # cdt-2. Checked once, right after publish and before the backup decision, so every run
    # refuses a staging folder without a usable ExchangeAdminWeb.dll - including a fresh install
    # with no database, and a -SkipConfigBackup run. Deliberately fatal: mirroring a payload that
    # cannot run reports success over an unusable instance. The backup reuses this staging folder.
    Invoke-PlanOrAction "Confirm the published build carries the config DB tool at $(Join-Path $stagingPath 'ExchangeAdminWeb.dll')" {
        Resolve-ConfigDbTool -AppPath $stagingPath | Out-Null
    }

    # A36. An upgrade mirrors new binaries over an existing instance, and those binaries may
    # run schema migrations against the config database the moment the pool starts. There was no
    # snapshot taken anywhere in this script, so a migration that went wrong had nothing to roll
    # back to - deploy tooling elsewhere takes this backup, but this installer is the supported
    # way to upgrade a generic environment and it did not.
    #
    # Online backup (VACUUM INTO + integrity_check), so it does not matter that this runs before
    # the pool stops - and running it first means a failure here aborts BEFORE anything is
    # touched.
    # Review of A36: the skip was decided INSIDE the action block, so a dry run printed "Back
    # up the config database" and said nothing about the real run skipping it. An operator could
    # approve a plan believing a snapshot was coming. Decide before planning.
    if ($SkipConfigBackup) {
        if ($PlanOnly) {
            Write-Plan "SKIP the config database backup entirely (-SkipConfigBackup was passed). The real run will NOT take a snapshot, and a failed schema migration will have nothing to roll back to."
        } else {
            Write-Warn "-SkipConfigBackup was passed: no pre-upgrade snapshot. A failed schema migration will have nothing to roll back to."
        }
    } else {

    # The backup runs the app's db mode from the build just published to staging, through the
    # .NET host in Program Files (docs/ConfigDbToolInApp-Plan.md). A plan runs before publish, so
    # it prints the paths it would use without requiring them to exist.
    $configDbToolPlan = Resolve-ConfigDbTool -AppPath $stagingPath -PlanOnly
    Invoke-PlanOrAction "Back up the config database before upgrading (via $($configDbToolPlan.Summary) backup): $($configStorePlan.DbPath)" {
        # cdt-11: the database decided before appsettings.json was written. Re-reading the file here
        # read it AFTER this run had rewritten it, so a dropped key sent the backup to the wrong file.
        $existingDb = $configStorePlan.DbPath
        $configuredDbPath = $configStorePlan.Path

        if (-not (Test-IsSqliteConfigDbPresent -DbPath $existingDb)) {
            # Review of A36: these two cases were collapsed into "nothing to back up", and only
            # one of them is harmless.
            #
            # With ConfigStore:Path SET, a missing file is not a fresh install - it is a broken
            # instance. The app refuses to start against a configured database it did not find
            # and never creates one, which is why the ADI deployment tooling fails on exactly
            # this condition too. Proceeding would mirror new binaries over an instance that
            # cannot start, with no snapshot, and report success.
            if ($configuredDbPath) {
                Write-Fail "ConfigStore:Path names '$existingDb' but no database exists there. The app will not start against a missing configured database (it never creates one). Fix the key, restore the file, or run tools\Move-ConfigDbToShared.ps1. Nothing was deployed."
            }

            # No key and no file: a fresh install, or an instance that has never started.
            Write-Ok "No existing config database at $existingDb - nothing to back up."
            return
        }

        $backupDir = Join-Path $LogRoot "ConfigBackups"
        if (-not (Test-Path -LiteralPath $backupDir -PathType Container)) {
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        }

        $backupStamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupFile = Backup-SqliteConfigDb -DbPath $existingDb -DestDir $backupDir -Timestamp $backupStamp -AppPath $stagingPath
        Write-Ok "Config database backed up (verified) to $backupFile"
    }

    }

    # cdt-32: the stop below proves that -AppPoolName stopped, not that nothing else serves the folder
    # the mirror replaces. Another site or application with its files in $PublishPath under a
    # different pool would keep serving them; cdt-15's check above compares only this application.
    # Ask IIS for every node serving the folder (aud-a38c, the promotion script's check): the module
    # throws unless there is one and each runs under -AppPoolName, and nothing here catches it. A
    # step of its own, so a plan prints it - on a fresh install the application exists only once the
    # apply run has created it above. -NestedNodes Refuse (aud-a38j): the mirror below replaces the
    # whole folder, so nothing may be rooted inside it, whatever its pool (aud-a38i).
    Invoke-PlanOrAction "Confirm that IIS serves $PublishPath from app pool $AppPoolName and no other, with nothing else rooted inside it (the mirror replaces the whole folder)" {
        $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse
    }

    Invoke-PlanOrAction "Stop app pool $AppPoolName and confirm it stopped" {
        # A35: see tools/AppPoolControl.psm1. The empty catch hid a pool that would not stop,
        # and the robocopy below mirrors over a live application.
        $poolState = Stop-AppPoolVerified -Name $AppPoolName
        # cdt-33: only a confirmed stop reaches the mirror. The pool step above creates the pool when it
        # is missing, so 'Absent' here means IIS did not answer what was asked (an unreadable
        # configuration, a name the existence probe reads as a wildcard pattern), not a fresh install.
        if ($poolState -ne 'Stopped') {
            Write-Fail "App pool $AppPoolName was reported '$poolState', not confirmed stopped, although this run created or updated it above. Refusing to replace the files of an application that cannot be shown to be stopped; the publish folder is unchanged."
        }
    }

    # cdt-18: the pool restarts only once the mirror is complete, and only a confirmed start counts.
    # robocopy /MIR failing part-way leaves a mix of the old and new builds in the publish folder, and the
    # restart in the finally used to serve it; a start that threw there replaced the mirror failure; and
    # Start-WebAppPool returning means IIS accepted the request, not that the pool runs (A38). A failed
    # restart is recorded and reported, never thrown from the finally, and fails the run below, after the
    # manifest, when nothing else failed.
    $mirrorSucceeded = $false
    $poolStartError = $null
    try {
        Invoke-RobocopyChecked -Description "Deploy application files" -Arguments @(
            $stagingPath, $PublishPath,
            "/MIR",
            "/XF", "appsettings*.json",
            "/XD", "logs", "config",
            "/NFL", "/NDL", "/NJH", "/NJS", "/R:2", "/W:1"
        )
        $mirrorSucceeded = $true
    } catch {
        Write-Fail "$($_.Exception.Message). App pool $AppPoolName was deliberately LEFT STOPPED: $PublishPath may now hold a mix of the old and new builds, and starting it would serve that mix. The mirror does not touch the config database (the pre-upgrade backup, when one was taken, is under $(Join-Path $LogRoot 'ConfigBackups')). The staging publish $stagingPath is removed when the installer exits; fix the cause and re-run the installer, which publishes and mirrors afresh."
    } finally {
        if ($mirrorSucceeded) {
            # Recorded, not thrown: a throw here would replace whatever is propagating (cdt-18).
            try {
                Invoke-PlanOrAction "Start app pool $AppPoolName and confirm it started" {
                    $null = Start-AppPoolVerified -Name $AppPoolName
                    Write-Ok "App pool $AppPoolName confirmed running"
                }
            } catch {
                $poolStartError = $_.Exception.Message
                Write-Host "  X  App pool $AppPoolName did not start: $poolStartError" -ForegroundColor Red
            }
        } else {
            Write-Warn "App pool $AppPoolName is NOT being restarted: the file mirror did not complete, so the pool stays stopped."
        }
    }

    # cdt-31: recorded, not thrown, like the restart above. Unguarded, a failed manifest write was the
    # error the caller got, and the check below that says the application is DOWN never ran. Both are
    # reported below, the restart's first; a manifest failure with the pool running still fails.
    $manifestError = $null
    try {
        Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $repoRoot
    } catch {
        $manifestError = $_.Exception.Message
        Write-Host "  X  The deployment manifest was not written: $manifestError" -ForegroundColor Red
    }

    # The files are deployed and the application is down: a failure, not "complete" (cdt-18).
    if ($poolStartError) {
        Write-Fail "The files are deployed, but app pool $AppPoolName did not start ($poolStartError). The application is DOWN until the pool is started by hand.$(if ($manifestError) { " The deployment manifest was not written either ($manifestError)." })"
    }
    if ($manifestError) {
        Write-Fail "The files are deployed and app pool $AppPoolName is running, but the deployment manifest was not written to $PublishPath ($manifestError), so nothing records which build it holds. Fix the cause and re-run the installer."
    }
} finally {
    if (-not $PlanOnly) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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

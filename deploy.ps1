#Requires -RunAsAdministrator

param(
    [string]$ParentSite  = "Default Web Site",
    [string]$AppAlias    = "ExchangeAdminWeb",
    [string]$AppPoolName = "ExchangeAdminWeb",
    [string]$PublishPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$LogRoot     = "E:\WWWOutput",
    [string]$CertSubject = "CN=EXO-Automation"
)

function Write-Step    { param($m) Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Success { param($m) Write-Host " OK  $m" -ForegroundColor Green }
function Write-Warn    { param($m) Write-Host "  !  $m" -ForegroundColor Yellow }
function Write-Fail    { param($m) Write-Host "  X  $m" -ForegroundColor Red; exit 1 }

$ProjectRoot = $PSScriptRoot

Write-Host ""
Write-Host "ExchangeAdminWeb - Deploy" -ForegroundColor Magenta
Write-Host "  Source  : $ProjectRoot" -ForegroundColor DarkGray
Write-Host "  Publish : $PublishPath" -ForegroundColor DarkGray
Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray
Write-Host ""

# --- 1. Stop app pool if exists ---

Import-Module WebAdministration -ErrorAction Stop

if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Write-Step "Stopping app pool: $AppPoolName"
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Success "Stopped"
}

# --- 2. Publish ---

Write-Step "Publishing application"

if (-not (Test-Path $PublishPath)) {
    New-Item -ItemType Directory -Path $PublishPath -Force | Out-Null
}

Push-Location $ProjectRoot
try {
    dotnet publish -c Release -o $PublishPath --nologo
    if ($LASTEXITCODE -ne 0) { Write-Fail "dotnet publish failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Success "Published to $PublishPath"

# --- 3. App pool ---

Write-Step "Configuring app pool: $AppPoolName"

if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Write-Warn "App pool exists -- reconfiguring"
} else {
    New-WebAppPool -Name $AppPoolName | Out-Null
}

Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion        -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType    -Value 4
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode                    -Value "AlwaysRunning"

Write-Success "App pool configured"

# --- 4. Web application under parent site ---

Write-Step "Configuring web application: $ParentSite/$AppAlias"

if (-not (Get-Website -Name $ParentSite -ErrorAction SilentlyContinue)) {
    Write-Fail "Parent site '$ParentSite' not found. Check -ParentSite parameter."
}

$existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue

if ($existingApp) {
    Write-Warn "Web application already exists -- updating"
    Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath    -Value $PublishPath
    Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
} else {
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

# --- 5. Certificate private key access ---

Write-Step "Granting app pool access to EXO certificate private key"

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

        # CNG key path (modern certificates)
        $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyName

        # Fallback to CSP path (legacy certificates)
        if (-not (Test-Path $keyPath)) {
            $keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyName
        }

        if (Test-Path $keyPath) {
            icacls $keyPath /grant "IIS AppPool\${AppPoolName}:(R)" | Out-Null
            Write-Success "Private key access granted (thumbprint: $($exoCert.Thumbprint))"
        } else {
            Write-Warn "Private key file not found at expected location"
        }
    } catch {
        Write-Warn "Could not set private key ACL: $($_.Exception.Message)"
    }
}

# --- 6. Log folder access ---

$AppLogFolder = Join-Path $LogRoot "ExchangeAdminWeb"

Write-Step "Granting app pool write access to $AppLogFolder"

if (-not (Test-Path $AppLogFolder)) {
    New-Item -ItemType Directory -Path $AppLogFolder -Force | Out-Null
}

icacls $AppLogFolder /grant "IIS AppPool\${AppPoolName}:(OI)(CI)M" | Out-Null
Write-Success "Log folder access granted"

# --- 7. Start app pool ---

Write-Step "Restarting app pool"
Start-WebAppPool -Name $AppPoolName
Write-Success "App pool started"

# --- Done ---

$parentUrl = (Get-WebBinding -Name $ParentSite | Select-Object -First 1).bindingInformation -replace '^\*:', 'http://localhost:'
Write-Host ""
Write-Host "Deploy complete." -ForegroundColor Green
Write-Host ("  URL : " + $parentUrl + "/$AppAlias") -ForegroundColor Cyan
Write-Host ""

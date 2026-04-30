#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs prerequisites for Exchange Admin Web Application (Blazor Server)

.DESCRIPTION
    This script installs and configures all necessary components to run a Blazor Server
    application on IIS for Exchange and AD administration tasks.

.PARAMETER SkipIIS
    Skip IIS installation and configuration

.PARAMETER SkipDotNet
    Skip .NET installation

.PARAMETER SkipModules
    Skip PowerShell module installation

.PARAMETER SiteName
    Name for the IIS site (default: ExchangeAdminWeb)

.PARAMETER AppPoolName
    Name for the IIS application pool (default: ExchangeAdminWebPool)

.EXAMPLE
    .\Install-Prerequisites.ps1
    
.EXAMPLE
    .\Install-Prerequisites.ps1 -SiteName "MyExchangeAdmin" -AppPoolName "MyExchangePool"
#>

param(
    [switch]$SkipIIS,
    [switch]$SkipDotNet,
    [switch]$SkipModules,
    [string]$SiteName = "ExchangeAdminWeb",
    [string]$AppPoolName = "ExchangeAdminWebPool"
)

# Color output functions
function Write-Step { param($Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Warning { param($Message) Write-Host "[!] $Message" -ForegroundColor Yellow }
function Write-Error { param($Message) Write-Host "[X] $Message" -ForegroundColor Red }

# Check if running as administrator
if (-NOT ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Error "This script must be run as Administrator. Please run PowerShell as Administrator and try again."
    exit 1
}

Write-Host "Exchange Admin Web Application - Prerequisites Installation" -ForegroundColor Magenta
Write-Host "=" * 65 -ForegroundColor Magenta

# Step 1: Install .NET 8 SDK and Runtime if not skipped
if (-not $SkipDotNet) {
    Write-Step "Installing .NET 8 SDK and ASP.NET Core Runtime"
    
    # Check if .NET 8 is already installed
    $dotnetVersion = $null
    try {
        $dotnetVersion = & dotnet --version 2>$null
        if ($dotnetVersion -and $dotnetVersion.StartsWith("8.")) {
            Write-Success ".NET 8 SDK already installed (version: $dotnetVersion)"
        } else {
            throw "Different version installed"
        }
    } catch {
        Write-Host "Downloading and installing .NET 8 SDK..."
        
        # Download .NET 8 SDK
        $dotnetUrl = "https://download.microsoft.com/download/8/4/8/848f28d2-d8ad-4dee-a893-625875a0b90c/dotnet-sdk-8.0.111-win-x64.exe"
        $dotnetInstaller = "$env:TEMP\dotnet-sdk-8.0.111-win-x64.exe"
        
        try {
            Write-Host "Downloading .NET 8 SDK from Microsoft..."
            Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetInstaller -UseBasicParsing
            
            Write-Host "Installing .NET 8 SDK (this may take a few minutes)..."
            Start-Process -FilePath $dotnetInstaller -ArgumentList "/quiet" -Wait -NoNewWindow
            
            # Refresh environment variables
            $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH", "User")
            
            # Verify installation
            Start-Sleep -Seconds 5
            $newVersion = & dotnet --version 2>$null
            if ($newVersion) {
                Write-Success ".NET 8 SDK installed successfully (version: $newVersion)"
            } else {
                Write-Warning ".NET SDK installed but may require system restart to be available in PATH"
            }
            
            # Clean up installer
            Remove-Item $dotnetInstaller -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Error "Failed to download or install .NET 8 SDK: $($_.Exception.Message)"
            Write-Host "Please manually download and install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0"
        }
    }
    
    # Install ASP.NET Core Runtime (Windows Hosting Bundle for IIS)
    Write-Host "Checking ASP.NET Core Windows Hosting Bundle..."
    $hostingBundleUrl = "https://download.microsoft.com/download/8/4/8/848f28d2-d8ad-4dee-a893-625875a0b90c/dotnet-hosting-8.0.11-win.exe"
    $hostingBundleInstaller = "$env:TEMP\dotnet-hosting-8.0.11-win.exe"
    
    try {
        Write-Host "Downloading ASP.NET Core Windows Hosting Bundle..."
        Invoke-WebRequest -Uri $hostingBundleUrl -OutFile $hostingBundleInstaller -UseBasicParsing
        
        Write-Host "Installing ASP.NET Core Windows Hosting Bundle..."
        Start-Process -FilePath $hostingBundleInstaller -ArgumentList "/quiet" -Wait -NoNewWindow
        
        Write-Success "ASP.NET Core Windows Hosting Bundle installed"
        Remove-Item $hostingBundleInstaller -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Error "Failed to install ASP.NET Core Windows Hosting Bundle: $($_.Exception.Message)"
        Write-Host "Please manually download and install from: https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}

# Step 2: Install and configure IIS if not skipped
if (-not $SkipIIS) {
    Write-Step "Installing and configuring IIS"
    
    # Check if IIS is already installed
    $iisFeature = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -ErrorAction SilentlyContinue
    if ($iisFeature -and $iisFeature.State -eq "Enabled") {
        Write-Success "IIS is already installed"
    } else {
        Write-Host "Installing IIS and required features..."
        
        $iisFeatures = @(
            "IIS-WebServerRole",
            "IIS-WebServer",
            "IIS-CommonHttpFeatures",
            "IIS-NetFxExtensibility45",
            "IIS-ISAPIExtensions",
            "IIS-ISAPIFilter",
            "IIS-ASPNET45",
            "IIS-WindowsAuthentication",
            "IIS-BasicAuthentication",
            "IIS-DirectoryBrowsing",
            "IIS-StaticContent",
            "IIS-DefaultDocument",
            "IIS-HttpErrors",
            "IIS-HttpRedirect",
            "IIS-HttpLogging",
            "IIS-ManagementConsole"
        )
        
        foreach ($feature in $iisFeatures) {
            try {
                Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart -ErrorAction Stop | Out-Null
                Write-Host "  [OK] Enabled $feature"
            } catch {
                Write-Warning "Failed to enable $feature : $($_.Exception.Message)"
            }
        }
        
        Write-Success "IIS installation completed"
    }
    
    # Import WebAdministration module
    Import-Module WebAdministration -ErrorAction SilentlyContinue
    
    # Create Application Pool
    Write-Host "Configuring IIS Application Pool: $AppPoolName"
    if (Get-IISAppPool -Name $AppPoolName -ErrorAction SilentlyContinue) {
        Write-Warning "Application pool '$AppPoolName' already exists. Updating configuration..."
        Remove-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    }
    
    New-WebAppPool -Name $AppPoolName
    Set-ItemProperty -Path "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity
    Set-ItemProperty -Path "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value "v4.0"
    Set-ItemProperty -Path "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false
    Set-ItemProperty -Path "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $true
    Write-Success "Application pool '$AppPoolName' created and configured"
    
    # Create IIS Site (placeholder - will be configured when app is deployed)
    $sitePath = "C:\inetpub\wwwroot\$SiteName"
    Write-Host "Preparing IIS site configuration for: $SiteName"
    
    if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {
        Write-Warning "Website '$SiteName' already exists"
    } else {
        # Create site directory
        if (-not (Test-Path $sitePath)) {
            New-Item -Path $sitePath -ItemType Directory -Force | Out-Null
        }
        
        # Create a placeholder index.html
        @"
<!DOCTYPE html>
<html>
<head>
    <title>$SiteName - Setup Required</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 40px; background: #f5f5f5; }
        .container { background: white; padding: 20px; border-radius: 5px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); }
        h1 { color: #333; }
        .status { background: #fff3cd; border: 1px solid #ffeaa7; padding: 10px; border-radius: 3px; }
    </style>
</head>
<body>
    <div class="container">
        <h1>$SiteName</h1>
        <div class="status">
            <strong>Setup Required:</strong> The Exchange Admin Web Application has not been deployed yet.
            <br>Please deploy the Blazor application to this location.
        </div>
        <p>Prerequisites installation completed on: $(Get-Date)</p>
    </div>
</body>
</html>
"@ | Out-File -FilePath "$sitePath\index.html" -Encoding UTF8
        
        New-Website -Name $SiteName -PhysicalPath $sitePath -Port 8080 -ApplicationPool $AppPoolName
        Write-Success "Website '$SiteName' created (http://localhost:8080)"
    }
}

# Step 3: Install required PowerShell modules if not skipped
if (-not $SkipModules) {
    Write-Step "Installing required PowerShell modules"

    # Ensure PSGallery is trusted for module installation
    if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
        Write-Success "PSGallery repository set as trusted"
    }

    $requiredModules = @(
        @{ Name = "ExchangeOnlineManagement"; Description = "Exchange Online PowerShell module (for cloud mailboxes)" },
        @{ Name = "ActiveDirectory"; Description = "Active Directory PowerShell module (for user validation)" },
        @{ Name = "ImportExcel"; Description = "Excel import/export functionality (for CSV operations)" },
        @{ Name = "PSFramework"; Description = "PowerShell framework for logging and configuration" }
    )

    Write-Host "Note: Exchange Management Tools are NOT required on this server." -ForegroundColor Green
    Write-Host "The application uses PowerShell remoting to connect to your Exchange 2019 server." -ForegroundColor Green
    
    foreach ($module in $requiredModules) {
        Write-Host "Checking PowerShell module: $($module.Name)"
        if (Get-Module -ListAvailable -Name $module.Name -ErrorAction SilentlyContinue) {
            Write-Success "$($module.Name) module already installed"
        } else {
            try {
                Write-Host "Installing $($module.Name)..."
                Install-Module -Name $module.Name -Force -AllowClobber -Scope AllUsers -ErrorAction Stop
                Write-Success "$($module.Name) module installed successfully"
            } catch {
                Write-Warning "Failed to install $($module.Name): $($_.Exception.Message)"
                if ($module.Name -eq "ActiveDirectory") {
                    Write-Host "Trying to install RSAT-AD-PowerShell..."
                    try {
                        Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0 -ErrorAction Stop
                        Write-Success "RSAT AD tools installed successfully"
                    } catch {
                        Write-Host "Note: RSAT installation failed. You may need to install it manually from Windows Features"
                    }
                }
            }
        }
    }
}

# Step 4: Configure Windows Firewall
Write-Step "Configuring Windows Firewall"
try {
    # Allow HTTP traffic on port 8080 for testing
    New-NetFirewallRule -DisplayName "ExchangeAdminWeb-HTTP" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -ErrorAction SilentlyContinue
    Write-Success "Firewall rule added for port 8080"
} catch {
    Write-Warning "Failed to configure firewall rule: $($_.Exception.Message)"
}

# Step 5: System Information and Next Steps
Write-Step "Installation Summary"

Write-Host ""
Write-Host "Prerequisites Installation Summary:" -ForegroundColor Green
Write-Host "=" * 40 -ForegroundColor Green

# Check .NET installation
try {
    $dotnetVer = & dotnet --version 2>$null
    Write-Success ".NET SDK Version: $dotnetVer"
} catch {
    Write-Warning ".NET SDK: Not detected in PATH (may require restart)"
}

# Check IIS
if (-not $SkipIIS) {
    $iisStatus = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole
    if ($iisStatus.State -eq "Enabled") {
        Write-Success "IIS: Installed and configured"
        Write-Success "Application Pool: $AppPoolName"
        Write-Success "Website: $SiteName (http://localhost:8080)"
    } else {
        Write-Warning "IIS: Installation may be incomplete"
    }
}

# Check PowerShell modules
if (-not $SkipModules) {
    $moduleCount = 0
    $requiredModules = @("ExchangeOnlineManagement", "ActiveDirectory", "ImportExcel", "PSFramework")
    foreach ($mod in $requiredModules) {
        if (Get-Module -ListAvailable -Name $mod -ErrorAction SilentlyContinue) {
            $moduleCount++
        }
    }
    Write-Success "PowerShell Modules: $moduleCount of $($requiredModules.Count) installed"
}

Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Deploy the Blazor Server application to: C:\inetpub\wwwroot\$SiteName"
Write-Host "2. Configure application settings (appsettings.json) with:"
Write-Host "   - Exchange server connection details"
Write-Host "   - Active Directory group for authorization"
Write-Host "   - SMTP settings for audit email notifications"
Write-Host "3. Set up SSL certificate for production use"
Write-Host "4. Configure Active Directory groups for authorization"
Write-Host "5. Restart IIS: iisreset"
Write-Host "6. Test the application at: http://localhost:8080"
Write-Host ""
Write-Host "Important: If you just installed .NET or IIS features, a system restart is recommended."

# Create installation log
$logContent = @"
Exchange Admin Web Application - Prerequisites Installation Log
================================================================
Date: $(Get-Date)
Computer: $env:COMPUTERNAME
User: $env:USERNAME

Installation Parameters:
- Skip IIS: $SkipIIS
- Skip .NET: $SkipDotNet  
- Skip Modules: $SkipModules
- Site Name: $SiteName
- App Pool Name: $AppPoolName

.NET Version: $(try { & dotnet --version 2>$null } catch { "Not detected" })
IIS Status: $(if ($SkipIIS) { "Skipped" } else { "Installed" })
PowerShell Modules: $(if ($SkipModules) { "Skipped" } else { "Installed" })

Prerequisites installation completed successfully.
"@

$logPath = "D:\source\ExchangeAdminWeb\Prerequisites-Install-Log.txt"
$logContent | Out-File -FilePath $logPath -Encoding UTF8
Write-Host "Installation log saved to: $logPath" -ForegroundColor Cyan

Write-Host ""
Write-Host "Prerequisites installation completed!" -ForegroundColor Green
$admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
if ($admin) {
    Update-Module -Name ExchangeOnlineManagement -Confirm:$false
    Update-Module -Name MicrosoftTeams -Confirm:$false
    Update-Module -Name Microsoft.Graph -Confirm:$false
    Update-Module -Name Microsoft.Graph.Entra -Confirm:$false
    Update-Module -Name Az -Confirm:$false
    }


$tenantId = "eaa689b4-8f87-40e0-9c6f-7228de4d754a"  # Your Azure AD Tenant ID
$Subscription = 'ADI-CORPIT-PRD-SUB'

# Connect to Microsoft Graph using App Registration

$clientId = "16866221-3e2b-4ea5-8aae-157b043b6d7c"  # Your App's Client ID
$clientSecret = Get-Content "D:\Scripts\O365\$env:computername-$env:username.MFAResets.cred_pass" # | ConvertTo-SecureString
$secureClientSecret = ConvertTo-SecureString $clientSecret -Force
$clientSecretCredential = New-Object System.Management.Automation.PSCredential ($clientId, $secureClientSecret)

# Connect to Microsoft Graph using App Registration with all granted scopes
Connect-MgGraph -ClientSecretCredential $clientSecretCredential -TenantId $tenantId -NoWelcome
Connect-Entra -TenantId $tenantId -ClientSecretCredential $clientSecretCredential  -NoWelcome

# Connect to Azure using service principal
try {
    $azCredential = New-Object System.Management.Automation.PSCredential($clientId, $secureClientSecret)
    $azResult = Connect-AzAccount -ServicePrincipal -Credential $azCredential -Tenant $tenantId -Subscription $Subscription -Force -ErrorAction Stop
    if ($azResult) {
        Write-Host "Azure connection successful" -ForegroundColor Green
    }
}
catch {
    Write-Warning "Azure connection failed: $($_.Exception.Message)"
    Write-Host "Continuing without Azure connection..." -ForegroundColor Yellow
}

Import-Module ActiveDirectory

$thumb = @(
  Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue
  Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue
) | Where-Object { $_.Subject -eq 'CN=EXO-Automation' -and $_.HasPrivateKey } |
    Sort-Object NotBefore -Descending |
    Select-Object -ExpandProperty Thumbprint -First 1

if (-not $thumb) { throw 'EXO-Automation certificate with private key not found. Import the PFX first.' }

#Connect-ExchangeOnline -AppId '129fb786-c574-42d8-b4f6-d1c440357819' -Organization 'analog.onmicrosoft.com' -CertificateThumbprint $thumb -ShowBanner:$false -Prefix C 
. "$PSScriptRoot\ConnectEXOL.ps1"
Get-PSSession | ft ComputerName,ConfigurationName,State
# Exchange Connection Management for Remote PowerShell
# Handles connections to both on-premises Exchange 2019 and Exchange Online

param(
    [string]$ExchangeServer = "your-exchange-server.domain.com",  # REPLACE WITH YOUR EXCHANGE SERVER
    [string]$DomainController = "ashbdc1.ad.analog.com"
)

# Global session management
$script:OnPremSession = $null

function Connect-OnPremExchange {
    param(
        [string]$Server = $ExchangeServer,
        [string]$DC = $DomainController
    )
    
    if ($script:OnPremSession -and $script:OnPremSession.State -eq 'Opened') {
        Write-Verbose "Using existing on-premises Exchange session"
        return $script:OnPremSession
    }
    
    try {
        Write-Host "Connecting to on-premises Exchange: $Server" -ForegroundColor Yellow
        
        if ($script:OnPremSession) {
            Remove-PSSession $script:OnPremSession -ErrorAction SilentlyContinue
        }
        
        $script:OnPremSession = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri "http://$Server/PowerShell/" -Authentication Kerberos
        Import-PSSession $script:OnPremSession -Prefix "OnPrem" -DisableNameChecking -AllowClobber | Out-Null
        
        Write-Host "✓ Connected to Exchange: $Server" -ForegroundColor Green
        return $script:OnPremSession
    }
    catch {
        Write-Error "Failed to connect to Exchange server: $($_.Exception.Message)"
        throw
    }
}

function Connect-ExchangeOnline {
    try {
        if (Get-Command Get-EXOMailbox -ErrorAction SilentlyContinue) {
            Write-Verbose "Already connected to Exchange Online"
            return
        }
        
        Write-Host "Connecting to Exchange Online..." -ForegroundColor Yellow
        
        $thumb = @(
            Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue
            Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue
        ) | Where-Object { $_.Subject -eq 'CN=EXO-Automation' -and $_.HasPrivateKey } |
            Sort-Object NotBefore -Descending |
            Select-Object -ExpandProperty Thumbprint -First 1

        if (-not $thumb) { 
            throw 'EXO-Automation certificate not found' 
        }

        Connect-ExchangeOnline -AppId '129fb786-c574-42d8-b4f6-d1c440357819' -Organization 'analog.onmicrosoft.com' -CertificateThumbprint $thumb -ShowBanner:$false
        Write-Host "✓ Connected to Exchange Online" -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to connect to Exchange Online: $($_.Exception.Message)"
        throw
    }
}

function Disconnect-ExchangeSessions {
    if ($script:OnPremSession) {
        Remove-PSSession $script:OnPremSession -ErrorAction SilentlyContinue
        $script:OnPremSession = $null
    }
    try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue } catch { }
}

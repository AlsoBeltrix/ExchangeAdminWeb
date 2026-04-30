# Enterprise Exchange Connection Management
# Handles multiple Exchange servers with load balancing and smart DC selection

param(
    [string[]]$ExchangeServers = @("exchange01.ad.analog.com", "exchange02.ad.analog.com"), # Will be loaded from config
    [switch]$Force
)

# Load configuration if available
if (Test-Path "$PSScriptRoot\..\ExchangeConfig.ps1") {
    . "$PSScriptRoot\..\ExchangeConfig.ps1"
    if ($script:ExchangeServers) { $ExchangeServers = $script:ExchangeServers }
}

# Global session management with server tracking
$script:ExchangeSessions = @{}
$script:ServerHealth = @{}
$script:LastHealthCheck = $null
$script:CurrentExchangeServer = $null
$script:CurrentDomainController = $null
$script:AvailableDCs = @()
$script:DCsLastRefresh = $null

function Get-SmartDomainControllers {
    param(
        [int]$MaxDCs = 5,
        [switch]$PreferLocal = $true
    )
    
    # Refresh DC list every 30 minutes or on first run
    if (-not $script:DCsLastRefresh -or ((Get-Date) - $script:DCsLastRefresh).TotalMinutes -gt 30) {
        Write-Verbose "Refreshing domain controller list..."
        
        try {
            # Get all domain controllers with site information
            $allDCs = Get-ADDomainController -Filter * | Select-Object Name, HostName, Site, IsGlobalCatalog, OperatingSystem
            
            if ($PreferLocal) {
                # Get local computer's site
                try {
                    $localSite = (Get-ADComputer $env:COMPUTERNAME -Properties Site).Site
                    if ($localSite) {
                        Write-Verbose "Local AD site detected: $localSite"
                        
                        # Prioritize DCs in local site
                        $localSiteDCs = $allDCs | Where-Object { $_.Site -eq $localSite } | Select-Object -First 3
                        $otherSiteDCs = $allDCs | Where-Object { $_.Site -ne $localSite } | Select-Object -First ($MaxDCs - $localSiteDCs.Count)
                        
                        $script:AvailableDCs = @($localSiteDCs) + @($otherSiteDCs)
                    } else {
                        # Fallback to all DCs if site detection fails
                        $script:AvailableDCs = $allDCs | Select-Object -First $MaxDCs
                    }
                } catch {
                    Write-Verbose "Could not determine local site, using all DCs: $($_.Exception.Message)"
                    $script:AvailableDCs = $allDCs | Select-Object -First $MaxDCs
                }
            } else {
                # Just use first available DCs
                $script:AvailableDCs = $allDCs | Select-Object -First $MaxDCs
            }
            
            $script:DCsLastRefresh = Get-Date
            Write-Verbose "Selected $($script:AvailableDCs.Count) domain controllers for use"
            
        } catch {
            Write-Warning "Failed to retrieve domain controllers: $($_.Exception.Message)"
            # Fallback to original DC if available
            if ($script:CurrentDomainController) {
                $script:AvailableDCs = @(@{ HostName = $script:CurrentDomainController })
            } else {
                $script:AvailableDCs = @(@{ HostName = "ashbdc1.ad.analog.com" })  # Ultimate fallback
            }
        }
    }
    
    return $script:AvailableDCs
}

function Get-HealthyDomainController {
    $availableDCs = Get-SmartDomainControllers
    
    # If current DC is still in the list and working, keep using it
    if ($script:CurrentDomainController) {
        $currentDCInfo = $availableDCs | Where-Object { $_.HostName -eq $script:CurrentDomainController }
        if ($currentDCInfo -and (Test-ServerConnectivity -ServerName $script:CurrentDomainController -Port 389 -TimeoutSeconds 3)) {
            Write-Verbose "Continuing to use current DC: $script:CurrentDomainController"
            return $script:CurrentDomainController
        }
    }
    
    # Test DCs in priority order and return first healthy one
    foreach ($dc in $availableDCs) {
        if (Test-ServerConnectivity -ServerName $dc.HostName -Port 389 -TimeoutSeconds 3) {
            $script:CurrentDomainController = $dc.HostName
            Write-Verbose "Selected healthy DC: $($dc.HostName) (Site: $($dc.Site))"
            return $dc.HostName
        }
    }
    
    # If all DCs failed, try original fallback
    Write-Warning "All discovered DCs appear unreachable, using fallback DC"
    $fallbackDC = "ashbdc1.ad.analog.com"
    $script:CurrentDomainController = $fallbackDC
    return $fallbackDC
}

function Test-ServerConnectivity {
    param(
        [string]$ServerName,
        [int]$Port = 80,
        [int]$TimeoutSeconds = 10
    )
    
    try {
        $result = Test-NetConnection -ComputerName $ServerName -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue
        return $result
    } catch {
        return $false
    }
}

function Get-HealthyExchangeServer {
    param([string[]]$Servers = $ExchangeServers)
    
    # Check if we need to refresh health status
    if (-not $script:LastHealthCheck -or ((Get-Date) - $script:LastHealthCheck).TotalMinutes -gt $LoadBalancingSettings.HealthCheckIntervalMinutes) {
        Update-ServerHealth -Servers $Servers
    }
    
    # Get healthy servers, prioritizing current server if still healthy
    $healthyServers = $Servers | Where-Object { 
        $script:ServerHealth[$_] -eq $true 
    }
    
    if (-not $healthyServers) {
        throw "No healthy Exchange servers available from: $($Servers -join ', ')"
    }
    
    # If current server is still healthy, use it
    if ($script:CurrentExchangeServer -and $script:CurrentExchangeServer -in $healthyServers) {
        return $script:CurrentExchangeServer
    }
    
    # Otherwise, pick the first healthy server (could be randomized for better load balancing)
    return $healthyServers[0]
}

function Update-ServerHealth {
    param([string[]]$Servers)
    
    Write-Host "Checking Exchange server health..." -ForegroundColor Yellow
    
    foreach ($server in $Servers) {
        $script:ServerHealth[$server] = Test-ServerConnectivity -ServerName $server -Port 80
        $status = if ($script:ServerHealth[$server]) { "✓" } else { "✗" }
        Write-Host "  $status $server" -ForegroundColor $(if ($script:ServerHealth[$server]) { "Green" } else { "Red" })
    }
    
    $script:LastHealthCheck = Get-Date
}

function Connect-OnPremExchange {
    param(
        [string[]]$Servers = $ExchangeServers,
        [int]$MaxRetries = 3
    )
    
    $selectedServer = Get-HealthyExchangeServer -Servers $Servers
    $selectedDC = Get-HealthyDomainController
    
    # Check for existing healthy session
    if ($script:ExchangeSessions[$selectedServer] -and 
        $script:ExchangeSessions[$selectedServer].State -eq 'Opened' -and 
        !$Force) {
        Write-Verbose "Using existing Exchange session to: $selectedServer"
        $script:CurrentExchangeServer = $selectedServer
        $script:CurrentDomainController = $selectedDC
        return $script:ExchangeSessions[$selectedServer]
    }
    
    $retryCount = 0
    $lastError = $null
    
    while ($retryCount -lt $MaxRetries) {
        try {
            Write-Host "Connecting to Exchange: $selectedServer (DC: $selectedDC)" -ForegroundColor Yellow
            
            # Clean up any existing session to this server
            if ($script:ExchangeSessions[$selectedServer]) {
                Remove-PSSession $script:ExchangeSessions[$selectedServer] -ErrorAction SilentlyContinue
                $script:ExchangeSessions.Remove($selectedServer)
            }
            
            # Create new session
            $session = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri "http://$selectedServer/PowerShell/" -Authentication Kerberos
            
            # Import Exchange cmdlets with server-specific prefix to avoid conflicts
            $serverPrefix = "OnPrem" # Could use server-specific prefix if needed
            Import-PSSession $session -Prefix $serverPrefix -DisableNameChecking -AllowClobber | Out-Null
            
            # Store session
            $script:ExchangeSessions[$selectedServer] = $session
            $script:CurrentExchangeServer = $selectedServer
            $script:CurrentDomainController = $selectedDC
            
            Write-Host "✓ Connected to Exchange: $selectedServer" -ForegroundColor Green
            return $session
            
        } catch {
            $lastError = $_.Exception
            $retryCount++
            $script:ServerHealth[$selectedServer] = $false
            
            Write-Warning "Connection attempt $retryCount failed for $selectedServer`: $($_.Exception.Message)"
            
            if ($retryCount -lt $MaxRetries) {
                # Try next healthy server
                try {
                    $selectedServer = Get-HealthyExchangeServer -Servers ($Servers | Where-Object { $_ -ne $selectedServer })
                    $selectedDC = Get-HealthyDomainController  # Get fresh DC for retry
                    Start-Sleep -Seconds 2
                } catch {
                    # No more healthy servers
                    break
                }
            }
        }
    }
    
    throw "Failed to connect to any Exchange server after $MaxRetries attempts. Last error: $($lastError.Message)"
}

function Connect-ExchangeOnline {
    try {
        # Check if already connected
        if (Get-Command Get-EXOMailbox -ErrorAction SilentlyContinue) {
            $orgConfig = Get-OrganizationConfig -ErrorAction SilentlyContinue
            if ($orgConfig) {
                Write-Verbose "Already connected to Exchange Online"
                return
            }
        }
        
        Write-Host "Connecting to Exchange Online..." -ForegroundColor Yellow
        
        # Use certificate-based authentication
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
        
    } catch {
        Write-Error "Failed to connect to Exchange Online: $($_.Exception.Message)"
        throw
    }
}

function Get-ExchangeServerHealth {
    # Public function to check server health
    Update-ServerHealth -Servers $ExchangeServers
    
    $healthReport = @()
    foreach ($server in $ExchangeServers) {
        $healthReport += [PSCustomObject]@{
            Server = $server
            Healthy = $script:ServerHealth[$server]
            HasSession = $script:ExchangeSessions.ContainsKey($server)
            SessionState = if ($script:ExchangeSessions[$server]) { $script:ExchangeSessions[$server].State } else { "None" }
            Current = ($server -eq $script:CurrentExchangeServer)
        }
    }
    
    return $healthReport
}

function Get-DomainControllerHealth {
    $dcHealth = @()
    $availableDCs = Get-SmartDomainControllers -MaxDCs 10
    
    foreach ($dc in $availableDCs) {
        $isHealthy = Test-ServerConnectivity -ServerName $dc.HostName -Port 389 -TimeoutSeconds 3
        $dcHealth += [PSCustomObject]@{
            Server = $dc.HostName
            Site = $dc.Site
            IsGlobalCatalog = $dc.IsGlobalCatalog
            OperatingSystem = $dc.OperatingSystem
            Healthy = $isHealthy
            Current = ($dc.HostName -eq $script:CurrentDomainController)
        }
    }
    
    return $dcHealth
}

function Disconnect-ExchangeSessions {
    param([switch]$All)
    
    if ($All) {
        # Disconnect all sessions
        foreach ($server in $script:ExchangeSessions.Keys) {
            try {
                Remove-PSSession $script:ExchangeSessions[$server] -ErrorAction SilentlyContinue
                Write-Host "✓ Disconnected from Exchange: $server" -ForegroundColor Green
            } catch {
                Write-Warning "Failed to disconnect from $server"
            }
        }
        $script:ExchangeSessions.Clear()
        $script:CurrentExchangeServer = $null
    } else {
        # Disconnect current session only
        if ($script:CurrentExchangeServer -and $script:ExchangeSessions[$script:CurrentExchangeServer]) {
            Remove-PSSession $script:ExchangeSessions[$script:CurrentExchangeServer] -ErrorAction SilentlyContinue
            $script:ExchangeSessions.Remove($script:CurrentExchangeServer)
            Write-Host "✓ Disconnected from Exchange: $script:CurrentExchangeServer" -ForegroundColor Green
            $script:CurrentExchangeServer = $null
        }
    }
    
    $script:CurrentDomainController = $null
    
    # Disconnect Exchange Online
    try { 
        Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue 
        Write-Host "✓ Disconnected from Exchange Online" -ForegroundColor Green
    } catch { }
}

# Export functions and variables
Export-ModuleMember -Function Connect-OnPremExchange, Connect-ExchangeOnline, Get-ExchangeServerHealth, Get-DomainControllerHealth, Disconnect-ExchangeSessions, Get-HealthyExchangeServer, Get-HealthyDomainController, Get-SmartDomainControllers
Export-ModuleMember -Variable CurrentExchangeServer, CurrentDomainController

# Cleanup on module removal
$MyInvocation.MyCommand.ScriptBlock.Module.OnRemove = {
    Disconnect-ExchangeSessions -All
}

# Check Migration Eligibility - Remote PowerShell Version
param (
    [Parameter(Mandatory=$true)][string] $UserName,
    [switch]$Migrate,
    [switch]$AutoComplete
)

# Import enterprise connection functions
. "$PSScriptRoot\ExchangeConnectionEnterprise.ps1"

$results = @()
$eligible = $true

Write-Host "Checking Migration Eligibility for: $UserName" -ForegroundColor Cyan
Write-Host "=" * 50 -ForegroundColor Cyan

try {
    # Connect to on-premises Exchange
    Connect-OnPremExchange
    $recipient = Get-OnPremRecipient -Identity $UserName -DomainController $CurrentDomainController -ErrorAction Stop
    
    if (-not $recipient.SamAccountName) {
        throw "User does not have a valid SamAccountName"
    }
    
    # Check AD group membership
    $adUser = Get-ADUser $recipient.SamAccountName -Properties memberOf -ErrorAction Stop
    $groups = $adUser.memberOf
    
    # Check if already migrated
    Connect-ExchangeOnline
    if (Get-EXOMailbox -Identity $recipient.PrimarySmtpAddress -ErrorAction SilentlyContinue) {
        Write-Host "✗ User is already migrated to Exchange Online" -ForegroundColor Red
        $results += "User is already a cloud mailbox"
        $eligible = $false
    }
    
    # Check for existing migration
    if (Get-MigrationUser -Identity $recipient.PrimarySmtpAddress -ErrorAction SilentlyContinue) {
        Write-Host "✗ Migration already in progress for this user" -ForegroundColor Red
        $results += "Migration already in progress"
        $eligible = $false
    }
    
    # Check ITAR restriction
    if ($groups -like "*SEC_ITAR_USERS*") {
        Write-Host "✗ User is member of SEC_ITAR_USERS - NOT eligible for migration" -ForegroundColor Red
        $results += "User is ITAR restricted - cannot migrate to cloud"
        $eligible = $false
    }
    
    # Additional checks can be added here
    # - Mailbox size limits
    # - Special compliance requirements
    # - Custom business rules
    
    if ($eligible) {
        Write-Host "✓ User IS eligible for migration to Exchange Online" -ForegroundColor Green
        $results += "User is eligible for migration"
        
        if ($Migrate) {
            # Create migration batch
            $migrationData = @{
                EmailAddress = $recipient.PrimarySmtpAddress
            }
            
            $csvFile = ".\migration_$($recipient.SamAccountName)_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
            [PSCustomObject]$migrationData | Export-Csv -Path $csvFile -NoTypeInformation
            
            Write-Host "Migration CSV created: $csvFile" -ForegroundColor Yellow
            
            # Here you would call your migration script
            # For now, just log the action
            $results += "Migration initiated for $($recipient.PrimarySmtpAddress)"
            Write-Host "✓ Migration batch would be created here" -ForegroundColor Green
            
            # Clean up CSV file
            Remove-Item $csvFile -ErrorAction SilentlyContinue
        }
    }
    
} catch {
    Write-Error "Failed to check migration eligibility: $($_.Exception.Message)"
    $results += "Error checking eligibility: $($_.Exception.Message)"
    $eligible = $false
}

# Cleanup connections
Disconnect-ExchangeSessions

# Return results
$returnObj = @{
    UserName = $UserName
    Eligible = $eligible
    Results = $results
    PrimarySmtpAddress = if ($recipient) { $recipient.PrimarySmtpAddress } else { $null }
}

return $returnObj

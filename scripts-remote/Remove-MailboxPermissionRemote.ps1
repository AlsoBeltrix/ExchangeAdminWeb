# Remove Mailbox Permissions - Remote PowerShell Version
param (
    [Parameter(Mandatory=$true)][string[]] $Target, 
    [Parameter(Mandatory=$true)][string[]] $UserNames, 
    [string]$Ticket, 
    [string]$Rights="FullAccess"
)

# Import enterprise connection functions
. "$PSScriptRoot\ExchangeConnectionEnterprise.ps1"

$results = @()

Write-Host "Removing Mailbox Permissions" -ForegroundColor Red
Write-Host "=" * 50 -ForegroundColor Red

foreach ($targetMailbox in $Target) {
    try {
        Write-Host "Processing mailbox: $targetMailbox" -ForegroundColor Cyan
        
        # Connect to on-premises to determine mailbox type
        Connect-OnPremExchange
        $recipient = Get-OnPremRecipient -Identity $targetMailbox -DomainController $CurrentDomainController -ErrorAction Stop
        
        $users = @()
        $isRemoteMailbox = $recipient.RecipientTypeDetails -like "Remote*"
        
        # Process each user
        foreach ($user in $UserNames) {
            if ($isRemoteMailbox) {
                # Cloud mailbox - use Exchange Online
                Connect-ExchangeOnline
                $userObject = Get-EXORecipient -Identity $user -ErrorAction SilentlyContinue
                if ($userObject) {
                    $users += $userObject.PrimarySmtpAddress
                } else {
                    Write-Warning "User '$user' not found in Exchange Online"
                }
            } else {
                # On-premises mailbox
                $userObject = Get-OnPremRecipient -Identity $user -DomainController $CurrentDomainController -ErrorAction SilentlyContinue
                if ($userObject) {
                    $users += $userObject.PrimarySmtpAddress
                } else {
                    Write-Warning "User '$user' not found in on-premises Exchange"
                }
            }
        }
        
        # Remove permissions
        foreach ($userEmail in $users) {
            try {
                if ($isRemoteMailbox) {
                    # Exchange Online permission removal
                    Connect-ExchangeOnline
                    $existingPermissions = Get-MailboxPermission -Identity $recipient.PrimarySmtpAddress -User $userEmail -ErrorAction SilentlyContinue
                    if ($existingPermissions) {
                        Remove-MailboxPermission -User $userEmail -AccessRights $Rights -Identity $recipient.PrimarySmtpAddress -Confirm:$false | Out-Null
                        $message = if ($Ticket) { "$Ticket`: Removed $Rights from $($recipient.PrimarySmtpAddress) for $userEmail" } else { "Removed $Rights from $($recipient.PrimarySmtpAddress) for $userEmail" }
                        $results += $message
                        Write-Host "✓ $message" -ForegroundColor Green
                    } else {
                        Write-Host "⚠ $userEmail has no permissions on $($recipient.PrimarySmtpAddress)" -ForegroundColor Yellow
                    }
                } else {
                    # On-premises permission removal
                    $existingPermissions = Get-OnPremMailboxPermission -Identity $recipient.PrimarySmtpAddress -User $userEmail -ErrorAction SilentlyContinue
                    if ($existingPermissions) {
                        Remove-OnPremMailboxPermission -User $userEmail -AccessRights $Rights -Identity $recipient.PrimarySmtpAddress -DomainController $CurrentDomainController -Confirm:$false | Out-Null
                        $message = if ($Ticket) { "$Ticket`: Removed $Rights from $($recipient.PrimarySmtpAddress) for $userEmail" } else { "Removed $Rights from $($recipient.PrimarySmtpAddress) for $userEmail" }
                        $results += $message
                        Write-Host "✓ $message" -ForegroundColor Green
                    } else {
                        Write-Host "⚠ $userEmail has no permissions on $($recipient.PrimarySmtpAddress)" -ForegroundColor Yellow
                    }
                }
                
            } catch {
                $errorMsg = "Failed to remove permission for $userEmail`: $($_.Exception.Message)"
                $results += $errorMsg
                Write-Error $errorMsg
            }
        }
        
    } catch {
        $errorMsg = "Failed to process mailbox '$targetMailbox'`: $($_.Exception.Message)"
        $results += $errorMsg
        Write-Error $errorMsg
    }
    
    Write-Host "-" * 50 -ForegroundColor Gray
}

# Cleanup connections
Disconnect-ExchangeSessions

# Return results for logging
return $results

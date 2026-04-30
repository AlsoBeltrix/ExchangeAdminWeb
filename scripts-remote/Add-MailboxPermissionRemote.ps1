# Add Mailbox Permissions - Remote PowerShell Version
param (
    [Parameter(Mandatory=$true)][string[]] $Target, 
    [Parameter(Mandatory=$true)][string[]] $UserNames, 
    [string]$Ticket, 
    [string]$Rights="FullAccess",
    [switch]$NoAutomap
)

# Import enterprise connection functions
. "$PSScriptRoot\ExchangeConnectionEnterprise.ps1"

$automap = -not $NoAutomap
$results = @()

Write-Host "Adding Mailbox Permissions" -ForegroundColor Green
Write-Host "=" * 50 -ForegroundColor Green

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
        
        # Add permissions
        foreach ($userEmail in $users) {
            try {
                if ($isRemoteMailbox) {
                    # Exchange Online permission
                    Connect-ExchangeOnline
                    Add-MailboxPermission -User $userEmail -AccessRights $Rights -Identity $recipient.PrimarySmtpAddress -AutoMapping $automap -Confirm:$false | Out-Null
                    $link = "https://outlook.office.com/mail/$($recipient.PrimarySmtpAddress)/"
                } else {
                    # On-premises permission
                    Add-OnPremMailboxPermission -User $userEmail -AccessRights $Rights -Identity $recipient.PrimarySmtpAddress -AutoMapping $automap -DomainController $CurrentDomainController -Confirm:$false | Out-Null
                    $link = "https://owa.analog.com/owa/$($recipient.PrimarySmtpAddress)/#path=/mail"
                }
                
                $message = if ($Ticket) { "$Ticket`: $userEmail granted $Rights to $($recipient.PrimarySmtpAddress)" } else { "$userEmail granted $Rights to $($recipient.PrimarySmtpAddress)" }
                $results += $message
                Write-Host "✓ $message" -ForegroundColor Green
                
            } catch {
                $errorMsg = "Failed to add permission for $userEmail`: $($_.Exception.Message)"
                $results += $errorMsg
                Write-Error $errorMsg
            }
        }
        
        if ($users.Count -gt 0) {
            Write-Host "Access link: $link" -ForegroundColor Cyan
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

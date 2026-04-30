# Set Calendar Permissions for List - Remote PowerShell Version
param(
    [Parameter(Mandatory=$false)][string]$CsvFile
)

# Import enterprise connection functions
. "$PSScriptRoot\ExchangeConnectionEnterprise.ps1"

if (-not $CsvFile) {
    $CsvFile = Read-Host "Enter CSV file path"
}

if (-not (Test-Path $CsvFile)) {
    Write-Error "CSV file not found: $CsvFile"
    return
}

$results = @()
$calendarList = Import-Csv $CsvFile

Write-Host "Setting Calendar Permissions from CSV" -ForegroundColor Green
Write-Host "=" * 50 -ForegroundColor Green

foreach ($entry in $calendarList) {
    try {
        Write-Host "Processing: $($entry.Identity)" -ForegroundColor Cyan
        
        # Connect to on-premises to determine mailbox type
        Connect-OnPremExchange
        $recipient = Get-OnPremRecipient -Identity $entry.Identity -DomainController $CurrentDomainController -ErrorAction SilentlyContinue
        
        if (-not $recipient) {
            Write-Warning "Recipient '$($entry.Identity)' not found"
            $results += "Recipient not found: $($entry.Identity)"
            continue
        }
        
        $isRemoteMailbox = $recipient.RecipientTypeDetails -like "Remote*"
        
        if ($isRemoteMailbox) {
            # Exchange Online calendar permissions
            Connect-ExchangeOnline
            $mailbox = Get-EXOMailbox -Identity $recipient.PrimarySmtpAddress -ErrorAction SilentlyContinue
            if (-not $mailbox) {
                Write-Warning "Cloud mailbox not found for $($recipient.PrimarySmtpAddress)"
                continue
            }
            
            # Get calendar folder
            $calendarFolder = Get-EXOMailboxFolderStatistics -Identity $recipient.PrimarySmtpAddress | Where-Object { $_.FolderType -eq 'Calendar' } | Select-Object -First 1
            if (-not $calendarFolder) {
                Write-Warning "Calendar folder not found for $($recipient.PrimarySmtpAddress)"
                continue
            }
            
            $calendarPath = "$($recipient.PrimarySmtpAddress):\$($calendarFolder.Name)"
            
            # Set calendar permission
            try {
                Set-MailboxFolderPermission -Identity $calendarPath -User $entry.User -AccessRights $entry.AccessRights -ErrorAction Stop
                $message = "Calendar permission set: $($entry.User) -> $($entry.AccessRights) on $calendarPath"
                Write-Host "✓ $message" -ForegroundColor Green
                $results += $message
            } catch {
                # Try adding permission if set failed
                try {
                    Add-MailboxFolderPermission -Identity $calendarPath -User $entry.User -AccessRights $entry.AccessRights -ErrorAction Stop
                    $message = "Calendar permission added: $($entry.User) -> $($entry.AccessRights) on $calendarPath"
                    Write-Host "✓ $message" -ForegroundColor Green
                    $results += $message
                } catch {
                    $errorMsg = "Failed to set calendar permission: $($_.Exception.Message)"
                    Write-Error $errorMsg
                    $results += $errorMsg
                }
            }
            
        } else {
            # On-premises calendar permissions
            $mailbox = Get-OnPremMailbox -Identity $recipient.Identity -DomainController $CurrentDomainController -ErrorAction SilentlyContinue
            if (-not $mailbox) {
                Write-Warning "On-premises mailbox not found for $($recipient.Identity)"
                continue
            }
            
            # Get calendar folder
            $calendarFolder = Get-OnPremMailboxFolderStatistics -Identity $recipient.Identity -DomainController $CurrentDomainController | Where-Object { $_.FolderType -eq 'Calendar' } | Select-Object -First 1
            if (-not $calendarFolder) {
                Write-Warning "Calendar folder not found for $($recipient.Identity)"
                continue
            }
            
            $calendarPath = "$($recipient.Identity):\$($calendarFolder.Name)"
            
            # Set calendar permission
            try {
                Set-OnPremMailboxFolderPermission -Identity $calendarPath -User $entry.User -AccessRights $entry.AccessRights -DomainController $CurrentDomainController -ErrorAction Stop
                $message = "Calendar permission set: $($entry.User) -> $($entry.AccessRights) on $calendarPath"
                Write-Host "✓ $message" -ForegroundColor Green
                $results += $message
            } catch {
                # Try adding permission if set failed
                try {
                    Add-OnPremMailboxFolderPermission -Identity $calendarPath -User $entry.User -AccessRights $entry.AccessRights -DomainController $CurrentDomainController -ErrorAction Stop
                    $message = "Calendar permission added: $($entry.User) -> $($entry.AccessRights) on $calendarPath"
                    Write-Host "✓ $message" -ForegroundColor Green
                    $results += $message
                } catch {
                    $errorMsg = "Failed to set calendar permission: $($_.Exception.Message)"
                    Write-Error $errorMsg
                    $results += $errorMsg
                }
            }
        }
        
    } catch {
        $errorMsg = "Failed to process calendar permission for '$($entry.Identity)': $($_.Exception.Message)"
        Write-Error $errorMsg
        $results += $errorMsg
    }
}

# Cleanup connections
Disconnect-ExchangeSessions

Write-Host "Calendar permission processing completed" -ForegroundColor Green
return $results

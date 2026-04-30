# CheckMigrationElligibility
param ([Parameter(Mandatory=$true)][string] $UserName,  [switch]$migrate, [switch]$AutoComplete)

if (!(get-command get-CMailbox -ErrorAction SilentlyContinue)) {
    "Connecting..."
    . "$PSScriptRoot\ConnectM365\ConnectEXOL.ps1"
    }

$migrationfile = @()
$ntid = get-recipient $UserName
$nogo=$false
if ($ntid.SamAccountName ) {
    $groups = Get-ADUser $ntid.SamAccountName -Properties memberOf | select -ExpandProperty memberOf
    [string]$email = $ntid.PrimarySmtpAddress
    if (get-cmigrationuser $email  -erroraction SilentlyContinue) {
        $nogo = $true
        Write-Host -ForegroundColor Red "$username has a migration already in-progress."
        }
    if (get-cmailbox $email -erroraction SilentlyContinue) {
        $nogo = $true
        Write-Host -ForegroundColor Red "$username is already a cloud mailbox."
        }
    if (($groups -like "*SEC_ITAR_USERS*")) {
        Write-Host -ForegroundColor Red "$username IS NOT eligible to migrate to Exchange Online because they are a member of SEC_ITAR_USERS"
	    $nogo = $true
        }
    if (!($nogo)) {
        Write-Host -ForegroundColor Green "$username IS elligible to migrate to Exchange Online. :)"
        $migrationfiledata = @{}
        $migrationfiledata.EmailAddress = $ntid.PrimarySmtpAddress
        $migrationfile += [pscustomobject]$migrationfiledata
        $migrationfilename = ".\$($ntid.PrimarySmtpAddress).csv" 
        $migrationfile | export-csv -Path ".\$($ntid.PrimarySmtpAddress).csv" -NoTypeInformation
        if ($migrate) {
            if ($AutoComplete) {
                D:\Scripts\MailMigrations\NewMigrationBatch.ps1 -CSVFile $migrationfilename -start -AutoComplete 
                }
            else {
                D:\Scripts\MailMigrations\NewMigrationBatch.ps1 -CSVFile $migrationfilename -start
                }
            Remove-Item $migrationfilename
            Write-Host "Mailbox ""$($ntid.PrimarySmtpAddress)"" migration is in progress. Please monitor it in the portal." -ForegroundColor Magenta
            }
        }
    }


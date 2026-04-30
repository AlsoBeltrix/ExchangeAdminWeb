param ([Parameter(Mandatory=$true)][Alias("t","targets")][string[]] $Target, [Parameter(Mandatory=$true)][Alias("u","username")][string[]]$UserNames, [string]$ticket, [string]$Rights="FullAccess")
$totusers = @()
if (!(get-command get-CMailbox -ErrorAction SilentlyContinue)) {
    "Connecting..."
    . "$PSScriptRoot\ConnectM365\ConnectEXOL.ps1"
    }
Write-Host "---------------------------------------------------------------------------------------------------" -ForegroundColor Green
foreach ($t in $target) {
    $targetType = (get-recipient $t -DomainController ashbdc1.ad.analog.com).RecipientTypeDetails
       $users = @()
    
    if ($targetType -like "Remote*") {
        $targetinfo = get-cmailbox -identity $t -ErrorAction SilentlyContinue
        if (!($targetinfo)) {
            Write-Error "$t not found"
            break
            }
        $link = "https://outlook.office.com/mail/$($targetinfo.PrimarySMTPAddress)/" 
        foreach ($user in $UserNames){
            $mailobj = Get-crecipient -identity $user  -ErrorAction SilentlyContinue
            if (!($mailobj)) {
                "$user not found"
                }
            else {
                $users += "$($mailobj.PrimarySmtpAddress)"
                }
            }
        foreach ($userid in $users) {
            if (get-cmailboxPermission -User $userid -Identity "$($targetinfo.PrimarySmtpAddress)") {
                $null = Remove-cMailboxPermission -User $userid -AccessRights $rights -Confirm:$false -Identity "$($targetinfo.PrimarySmtpAddress)"
                }
            else {
                "$userid does not have any permissions granted for mailbox $($targetinfo.PrimarySmtpAddress)"
               }
            }
        }
    else {
        $targetinfo = get-mailbox $t -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
        if (!($targetinfo)) {
            Write-Error "$t not found"
            break
            }
        foreach ($user in $UserNames){
            $mailobj = Get-Mailbox -identity $user -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
                #"$user not a mailbox..."
            if (!($mailobj)) {
                $mailobj = Get-RemoteMailbox -identity $user -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
                $users += "$($mailobj.PrimarySmtpAddress)"
                if (!($mailobj)) {
                    }
                }
            else {
                $users += "$($mailobj.PrimarySmtpAddress)"
                }
            }

        foreach ($userid in $users) {
            if (get-MailboxPermission -User $userid -Identity "$($targetinfo.PrimarySmtpAddress)") {
                $null = Remove-MailboxPermission -User $userid -AccessRights $rights -Confirm:$false -Identity "$($targetinfo.PrimarySmtpAddress)" -DomainController ashbdc1.ad.analog.com
                }
            else {
                "$userid does not have any permissions granted for mailbox $($targetinfo.PrimarySmtpAddress)"
                }
            }
        }

    foreach ($userid in $users) {
        if ($ticket) {$totusers += "`n$($ticket): $rights rights to $($targetinfo.PrimarySMTPAddress) for $userid have been removed"}
        else {$totusers += "`n$rights rights to $($targetinfo.PrimarySMTPAddress) for $userid have been removed"}

        }
    Write-Host "$totusers" -foregroundcolor Magenta
    $totusers = @()
    }

<#
Write-Host "$totusers" -foregroundcolor Magenta
Write-Host "`nUsers can access this mailbox in Outlook or at the following link:`n$($link)" -foregroundcolor Cyan
Write-Host "---------------------------------------------------------------------------------------------------" -ForegroundColor Green
#Write-Host "$userid has been granted $rights rights to $($targetinfo.PrimarySMTPAddress)" -foregroundcolor Magenta
#>   
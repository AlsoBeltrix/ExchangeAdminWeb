param ([Parameter(Mandatory=$true)][Alias("t","targets")][string[]] $Target, [Parameter(Mandatory=$true)][Alias("u","username")][string[]]$UserNames, [string]$ticket, [string]$Rights="FullAccess",[switch]$NoAutomap)
$totusers = @()
. "$PSScriptRoot\ConnectM365\ConnectEXOL.ps1"

if ($NoAutomap) { $automap = $false }
else { $automap = $true }

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
        #$users
        foreach ($userid in $users) {
            #"Adding $userid..."
            $null = Add-cMailboxPermission -User $userid -AccessRights $rights -Confirm:$false -AutoMapping $automap -Identity "$($targetinfo.PrimarySmtpAddress)" # -WarningAction SilentlyContinue # -whatif
            }
        }
    else {
        $targetinfo = get-mailbox $t -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
        if (!($targetinfo)) {
            Write-Error "$t not found"
            break
            }
        $link = "https://owa.analog.com/owa/$($targetinfo.PrimarySMTPAddress)/#path=/mail"
        foreach ($user in $UserNames){
            $mailobj = Get-Mailbox -identity $user -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
                #"$user not a mailbox..."
            if (!($mailobj)) {
                $mailobj = Get-RemoteMailbox -identity $user -ErrorAction SilentlyContinue -DomainController ashbdc1.ad.analog.com
                $users += "$($mailobj.PrimarySmtpAddress)"
                if (!($mailobj)) {
                   # "$user not a remotemailbox"
                    }
                }
            else {
                #"Adding $($mailobj.PrimarySmtpAddress)"
                $users += "$($mailobj.PrimarySmtpAddress)"
                }
            }
        #$users
        foreach ($userid in $users) {
            #"Adding $userid..."
            $null = Add-MailboxPermission -User $userid -AccessRights $rights -Confirm:$false -AutoMapping $automap -Identity "$($targetinfo.PrimarySmtpAddress)" -DomainController ashbdc1.ad.analog.com # -WarningAction SilentlyContinue
            }
        }

    

    foreach ($userid in $users) {
        #Write-Host "$userid has been granted $rights rights to $($targetinfo.PrimarySMTPAddress)" -foregroundcolor Magenta
        if ($ticket) {$totusers += "`n$($ticket): $userid has been granted $rights rights to $($targetinfo.PrimarySMTPAddress)"}
        else {$totusers += "`n$userid has been granted $rights rights to $($targetinfo.PrimarySMTPAddress)"}

        }
    Write-Host "$totusers" -foregroundcolor Magenta
    Write-Host "`nUsers can access this mailbox in Outlook or at the following link:`n$($link)" -foregroundcolor Cyan
    Write-Host "---------------------------------------------------------------------------------------------------" -ForegroundColor Green
    $totusers = @()
    }

<#
Write-Host "$totusers" -foregroundcolor Magenta
Write-Host "`nUsers can access this mailbox in Outlook or at the following link:`n$($link)" -foregroundcolor Cyan
Write-Host "---------------------------------------------------------------------------------------------------" -ForegroundColor Green
#Write-Host "$userid has been granted $rights rights to $($targetinfo.PrimarySMTPAddress)" -foregroundcolor Magenta
#>   
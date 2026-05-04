param (
    [Parameter(Mandatory=$true)]
    [Alias("u","email")]
    [string]$User
    )
$date = (Get-Date).AddDays(-1)

Get-CMigrationUser $User
Set-CMigrationUser $User -ApproveSkippedItems 
sleep 5
Set-CMigrationUser $User -CompleteAfter $date
sleep 5
Set-CMigrationBatch $User -ApproveSkippedItems
sleep 5
Set-CMigrationBatch $User -CompleteAfter $date
Set-CMoveRequest $User -SkippedItemApprovalTime $date 
sleep 15
Resume-CMoveRequest $User
Complete-CMigrationBatch $User -Confirm:$false
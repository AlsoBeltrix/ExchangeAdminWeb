param(
    [Parameter(Mandatory=$false)][string]$CsvFile
)

if (-not $CsvFile) {
    $CsvFile = Read-Host "Enter CSV file"
}

if (-not (Test-Path $CsvFile)) {
    Write-Error "$CsvFile not found"
    exit 1
}

$results = @()
$list = Import-Csv $CsvFile
foreach ($target in $list) {
    $mailbox = Get-Mailbox -Identity $target.Identity -ResultSize 1 -ErrorAction SilentlyContinue
    if (-not $mailbox) {
        $results += "Mailbox $($target.Identity) not found"
        continue
    }

    $calendarFolderName = ($mailbox | Get-MailboxFolderStatistics | Where-Object { $_.FolderType -eq 'Calendar' }).Name
    if (-not $calendarFolderName) {
        $results += "No calendar folder found for $($target.Identity)"
        continue
    }

    $calFolder = "$($target.Identity):\$calendarFolderName"
    Add-MailboxFolderPermission -User $target.User -AccessRights $target.AccessRights -Identity $calFolder -ErrorAction SilentlyContinue | Out-Null
    Set-MailboxFolderPermission -User $target.User -AccessRights $target.AccessRights -Identity $calFolder -ErrorAction Stop
    $results += "User $($target.User) permission set to $($target.AccessRights) for $calFolder"
}

$results

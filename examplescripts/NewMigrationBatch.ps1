param([Parameter(Mandatory=$true)] [string]$CSVFile, [switch]$start, [switch]$connect, [switch]$AutoComplete, [switch]$MoveBack )

# Find repository root - look for parent directory containing Modules folder
$repoRoot = $PSScriptRoot
while ($repoRoot) {
    if (Test-Path "$repoRoot\Modules\ScriptLogging.psd1") {
        break  # Found the repository root
    }
    $parent = Split-Path -Parent $repoRoot
    if ($parent -eq $repoRoot) {
        # Reached filesystem root, fallback to assuming parent of script directory
        $repoRoot = Split-Path -Parent $PSScriptRoot
        break
    }
    $repoRoot = $parent
}

# Import centralized modules
Import-Module "$repoRoot\Modules\ScriptLogging.psd1" -Force
Import-Module "$repoRoot\Modules\M365Connections.psd1" -Force

# Initialize logging
$logs = Initialize-Logging -ScriptPath $PSCommandPath -StartTranscript

# Connect to Exchange Online only
ExolConnect

If (!(Test-Path $CSVFile) -or !($CSVFile)) {
    do { $csvfile = Read-Host "CSV not found. Enter a valid CSV file" } while (!(Test-Path $CSVFile))
    }

$CSVFile = gci $CSVFile
$BatchName = (Split-Path $csvfile -Leaf).Replace(".csv","")

$continue = $true

if (!($MoveBack)) {
    New-CMigrationBatch -Name $BatchName -SourceEndpoint hybrid1 -TargetDeliveryDomain analog.mail.onmicrosoft.com -CSVData ([System.IO.File]::ReadAllBytes("$CSVFile")) -NotificationEmails @('michael.coelho@analog.com','jose.hernandez@analog.com') # -Verbose

    # Log to CSV
    $logEntry = @{
        Date = Get-Date
        BatchName = $BatchName
        Direction = "ToCloud"
        CSVFile = $CSVFile.Name
        Started = $start
        AutoComplete = $AutoComplete
    }
    Write-CSVLog -LogFile $logs.CSVLogFile -LogEntry $logEntry

    if ($start -eq $true) {
        "Waiting 30 seconds..."
        sleep 30
        start-Cmigrationbatch -identity $BatchName
        }
    if ($AutoComplete) {
        set-Cmigrationbatch -identity $BatchName -CompleteAfter ((get-date).AddHours(-1))
        }
    }
elseif ($MoveBack) {
    $dbs = (Get-MailboxDatabase | ? {$_.MasterServerOrAvailabilityGroup -eq 'DAG2019'}).name
    New-CMigrationBatch -Name $BatchName -TargetEndpoint hybrid1 -TargetDeliveryDomain analog.com -TargetDatabases $dbs -CSVData ([System.IO.File]::ReadAllBytes("$CSVFile")) -NotificationEmails @('michael.coelho@analog.com','jose.hernandez@analog.com') # -Verbose

    # Log to CSV
    $logEntry = @{
        Date = Get-Date
        BatchName = $BatchName
        Direction = "ToOnPrem"
        CSVFile = $CSVFile.Name
        Started = $start
        AutoComplete = $AutoComplete
    }
    Write-CSVLog -LogFile $logs.CSVLogFile -LogEntry $logEntry

    if ($start -eq $true) {
        "Waiting 30 seconds..."
        sleep 30
        start-Cmigrationbatch -identity $BatchName
        }
    if ($AutoComplete) {
        set-Cmigrationbatch -identity $BatchName -CompleteAfter ((get-date).AddHours(-1))
        }
    }

Stop-Logging -StopTranscript

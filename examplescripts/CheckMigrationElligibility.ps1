# CheckMigrationElligibility
param ([Parameter(Mandatory=$true)][string] $UserName,  [switch]$migrate, [switch]$AutoComplete)

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

# Connect to required services: Exchange Online + Active Directory
ExolConnect
ADImport

# Initialize logging
$logs = Initialize-Logging -ScriptPath $PSCommandPath

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
        $migrationfilename = "$($logs.OutPath)\$($ntid.PrimarySmtpAddress).csv"
        $migrationfile | export-csv -Path $migrationfilename -NoTypeInformation
        if ($migrate) {
            Write-Host "Mailbox ""$($ntid.PrimarySmtpAddress)"" migration is in progress. Please monitor it in the portal." -ForegroundColor Magenta
            if ($AutoComplete) {
                . $PSScriptRoot\NewMigrationBatch.ps1 -CSVFile $migrationfilename -start -AutoComplete 
                }
            else {
                . $PSScriptRoot\NewMigrationBatch.ps1 -CSVFile $migrationfilename -start
                }
            Remove-Item $migrationfilename
            # Write-Host "Mailbox ""$($ntid.PrimarySmtpAddress)"" migration is in progress. Please monitor it in the portal." -ForegroundColor Magenta
            }
        }
    }


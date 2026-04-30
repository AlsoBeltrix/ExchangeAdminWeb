<#
if (Test-Path "$PSScriptRoot\o365\$env:computername-$env:username.cred_pass"){
    $credpass = Get-Content "$PSScriptRoot\o365\$env:computername-$env:username.cred_pass" | ConvertTo-SecureString
    } else {
        "No password file."
        break
    }

$cred = new-object -typename System.Management.Automation.PSCredential -argumentlist "svc_scriptadmo365@analog.onmicrosoft.com",$credpass

Connect-ExchangeOnline -Credential $cred -Prefix C
#>
$thumb = @(
  Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue
  Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue
) | Where-Object { $_.Subject -eq 'CN=EXO-Automation' -and $_.HasPrivateKey } |
    Sort-Object NotBefore -Descending |
    Select-Object -ExpandProperty Thumbprint -First 1

if (-not $thumb) { throw 'EXO-Automation certificate with private key not found. Import the PFX first.' }

Connect-ExchangeOnline -AppId '129fb786-c574-42d8-b4f6-d1c440357819' -Organization 'analog.onmicrosoft.com' -CertificateThumbprint $thumb -ShowBanner:$false -Prefix C
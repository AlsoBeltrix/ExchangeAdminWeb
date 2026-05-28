#Requires -RunAsAdministrator

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$deployScript = Join-Path $repoRoot "deploy.ps1"

if (-not (Test-Path -LiteralPath $deployScript -PathType Leaf)) {
    throw "deploy.ps1 was not found at expected path: $deployScript"
}

& $deployScript `
    -ParentSite "Default Web Site" `
    -AppAlias "ExchangeAdminWebDev" `
    -AppPoolName "ExchangeAdminWebDev" `
    -PublishPath "D:\inetpub\ExchangeAdminWebDev" `
    -PathBase "/ExchangeAdminWebDev" `
    -LogRoot "E:\WWWOutput" `
    -NonInteractive

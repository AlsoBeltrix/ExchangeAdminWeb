#Requires -Version 7.0
<#
.SYNOPSIS
    Creates the two Entra app registrations ExchangeAdminWeb needs for the
    Risky Users and Intune Devices modules, grants admin consent, and mints a
    client secret for each.

.DESCRIPTION
    Run once, interactively, as a Global Administrator (or Privileged Role
    Administrator + Application Administrator). Prints Tenant ID, Client ID and
    Client Secret per registration. The secret is shown ONCE - copy each set
    straight into a Delinea secret with fields named exactly:
        Tenant ID
        Client ID
        Client Secret
    Then set each module's GraphDelineaSecretId in Module Config.

    Use -PlanOnly to see what would be created without touching the tenant.

.PARAMETER PlanOnly
    Describe the actions without executing them.

.PARAMETER SecretLifetimeYears
    Client secret validity. Default 1.
#>
[CmdletBinding()]
param(
    [switch]$PlanOnly,
    [int]$SecretLifetimeYears = 1
)

$ErrorActionPreference = "Stop"

$GraphAppId = "00000003-0000-0000-c000-000000000000"

$Registrations = @(
    @{
        Name  = "ExchangeAdminWeb-RiskyUsers"
        Roles = @(
            "IdentityRiskyUser.Read.All",
            "IdentityRiskyUser.ReadWrite.All"
        )
    },
    @{
        Name  = "ExchangeAdminWeb-IntuneDevices"
        Roles = @(
            "DeviceManagementManagedDevices.Read.All",
            "DeviceManagementManagedDevices.ReadWrite.All",
            "DeviceManagementManagedDevices.PrivilegedOperations.All",
            "Device.ReadWrite.All"
        )
    }
)

function Write-Step([string]$Message) {
    Write-Host "  $Message" -ForegroundColor Cyan
}

if ($PlanOnly) {
    Write-Host "PLAN ONLY - nothing will be created." -ForegroundColor Yellow
    foreach ($reg in $Registrations) {
        Write-Host ""
        Write-Host "Would create app registration '$($reg.Name)' (single tenant) with a service principal," -ForegroundColor Yellow
        Write-Host "grant admin consent for these Microsoft Graph application permissions," -ForegroundColor Yellow
        Write-Host "and add a $SecretLifetimeYears-year client secret:" -ForegroundColor Yellow
        $reg.Roles | ForEach-Object { Write-Step $_ }
    }
    return
}

if (-not (Get-Module -ListAvailable Microsoft.Graph.Applications)) {
    Write-Host "Installing Microsoft.Graph.Applications for the current user..."
    Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force
}
Import-Module Microsoft.Graph.Applications

Connect-MgGraph -Scopes @(
    "Application.ReadWrite.All",
    "AppRoleAssignment.ReadWrite.All"
) -NoWelcome

$tenantId = (Get-MgContext).TenantId
$graphSp  = Get-MgServicePrincipal -Filter "appId eq '$GraphAppId'"
if (-not $graphSp) { throw "Microsoft Graph service principal not found in tenant $tenantId." }

# Preflight BOTH names before creating anything, so an existing registration
# refuses the whole run and never leaves the tenant half-configured.
$collisions = foreach ($reg in $Registrations) {
    $existing = Get-MgApplication -Filter "displayName eq '$($reg.Name)'"
    if ($existing) { "'$($reg.Name)' (appId $($existing.AppId))" }
}
if ($collisions) {
    throw "Refusing: app registration(s) already exist - $($collisions -join '; '). Nothing was created. Remove or rename them first; this script does not modify existing registrations."
}

# Resolve EVERY role for EVERY registration before creating anything, for the same reason
# the name check above exists. The role lookup used to run inside the creation loop, so an
# unrecognised permission on the second registration threw after the first had been created,
# consented and given a secret - and an unrecognised permission is the likeliest failure
# here, because the Roles list is edited by hand.
$rolesByRegistration = @{}
foreach ($reg in $Registrations) {
    $rolesByRegistration[$reg.Name] = foreach ($roleName in $reg.Roles) {
        $role = $graphSp.AppRoles | Where-Object {
            $_.Value -eq $roleName -and $_.AllowedMemberTypes -contains "Application"
        }
        if (-not $role) {
            throw "Graph application permission '$roleName' (for '$($reg.Name)') not found. Nothing was created."
        }
        $role
    }
}

$results = @()

# A mid-run failure leaves real registrations behind. They are named here so the operator
# knows what to record or remove - without this the tenant is half-configured and the only
# evidence is scrollback.
try {

foreach ($reg in $Registrations) {
    Write-Host ""
    Write-Host "== $($reg.Name) ==" -ForegroundColor Green

    $roles = $rolesByRegistration[$reg.Name]

    Write-Step "Creating application"
    $app = New-MgApplication -DisplayName $reg.Name -SignInAudience "AzureADMyOrg" -RequiredResourceAccess @(
        @{
            ResourceAppId  = $GraphAppId
            ResourceAccess = @($roles | ForEach-Object { @{ Id = $_.Id; Type = "Role" } })
        }
    )

    # Review: $results was only appended after the secret existed, so a failure in the
    # service principal, the consent grant or the secret itself left a real application in
    # the tenant that the catch could not name - it printed "No registrations were created."
    # Record the application the moment it exists; the row is completed below.
    $results += [pscustomobject]@{
        Registration    = $reg.Name
        "Tenant ID"     = $tenantId
        "Client ID"     = $app.AppId
        "Client Secret" = "(never issued - this registration is INCOMPLETE and must be deleted or finished by hand)"
        SecretExpires   = $null
    }

    Write-Step "Creating service principal"
    $sp = New-MgServicePrincipal -AppId $app.AppId

    foreach ($role in $roles) {
        Write-Step "Granting admin consent: $($role.Value)"
        New-MgServicePrincipalAppRoleAssignment `
            -ServicePrincipalId $sp.Id `
            -PrincipalId $sp.Id `
            -ResourceId $graphSp.Id `
            -AppRoleId $role.Id | Out-Null
    }

    Write-Step "Adding client secret ($SecretLifetimeYears year)"
    $secret = Add-MgApplicationPassword -ApplicationId $app.Id -PasswordCredential @{
        DisplayName = "ExchangeAdminWeb"
        EndDateTime = (Get-Date).AddYears($SecretLifetimeYears)
    }

    $record = [pscustomobject]@{
        Registration   = $reg.Name
        "Tenant ID"    = $tenantId
        "Client ID"    = $app.AppId
        "Client Secret" = $secret.SecretText
        SecretExpires  = $secret.EndDateTime
    }
    # Replace the incomplete placeholder added above now that the secret exists.
    $results = @($results | Where-Object { $_."Client ID" -ne $app.AppId })
    $results += $record

    # Printed HERE, as soon as it exists, not only in the summary at the end. The client
    # secret cannot be read back from Entra: if a later registration throws, the summary
    # never runs and this secret is gone while the registration it belongs to still exists.
    # Buffering an unrecoverable value until the end of a loop that can fail is the defect.
    Write-Host ""
    Write-Host "--- $($reg.Name): created. Copy this into its Delinea secret now. ---" -ForegroundColor Yellow
    $record | Format-List
}

Write-Host ""
Write-Host "Copy each row into its Delinea secret now - the Client Secret is not retrievable later." -ForegroundColor Yellow
$results | Format-List

} catch {
    Write-Host ""
    Write-Host "  X  FAILED partway through: $_" -ForegroundColor Red
    if ($results.Count -gt 0) {
        Write-Host ""
        Write-Host "These registrations WERE created and still exist in the tenant. Record their secrets now or delete the registrations - the secrets cannot be retrieved later:" -ForegroundColor Yellow
        $results | Format-List
    } else {
        Write-Host "No registrations were created." -ForegroundColor Yellow
    }
    throw
}

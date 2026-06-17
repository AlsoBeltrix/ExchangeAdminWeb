[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ServerUrl,
    [string]$Username,
    [securestring]$Password,
    [string]$GrantType = "password",
    [int]$SecretId = 31
)

$ErrorActionPreference = "Stop"

function ConvertTo-PlainText {
    # Flatten a SecureString to plaintext only at the moment of use, then free the
    # unmanaged BSTR. The plaintext is never echoed.
    param([securestring]$Secure)
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Get-SafeHttpError {
    # Constitution line 42: never surface raw Delinea auth response bodies. Report the
    # HTTP status code and reason phrase only; never the raw error-response body. When
    # there is no HTTP response (DNS/TLS/connection failure) the exception message
    # carries no response body and is safe to show.
    param($ErrorRecord)
    $response = $ErrorRecord.Exception.Response
    if ($response -and $null -ne $response.StatusCode) {
        return "HTTP $([int]$response.StatusCode) $($response.StatusCode)"
    }
    return $ErrorRecord.Exception.Message
}

if (-not $Username) { $Username = Read-Host "Username" }
if (-not $Password) { $Password = Read-Host "Password" -AsSecureString }

Write-Host "`n--- Delinea REST Test ---" -ForegroundColor Cyan
Write-Host "Server:    $ServerUrl"
Write-Host "Grant:     $GrantType"
Write-Host "Username:  $Username"
Write-Host "SecretId:  $SecretId"
Write-Host ""

# Step 1: Get token
Write-Host "[1] Authenticating..." -NoNewline
try {
    $plainPassword = ConvertTo-PlainText $Password
    if ($GrantType -eq "password") {
        $body = @{
            grant_type = "password"
            username   = $Username
            password   = $plainPassword
        }
    } else {
        $clientId = if ($Username -like "sdk-client-*") { $Username } else { "sdk-client-$Username" }
        $body = @{
            grant_type    = "client_credentials"
            client_id     = $clientId
            client_secret = $plainPassword
        }
    }
    $tokenResponse = Invoke-RestMethod -Uri "$ServerUrl/oauth2/token" -Method POST -Body $body -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop
    Write-Host " OK" -ForegroundColor Green
    Write-Host "   Token expires in $($tokenResponse.expires_in)s"
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "   $(Get-SafeHttpError $_)" -ForegroundColor Red
    exit 1
}
finally {
    $plainPassword = $null
}

# Step 2: Fetch secret
Write-Host "[2] Fetching secret $SecretId..." -NoNewline
try {
    $headers = @{ Authorization = "Bearer $($tokenResponse.access_token)" }
    $secret = Invoke-RestMethod -Uri "$ServerUrl/api/v1/secrets/$SecretId" -Headers $headers -ErrorAction Stop
    Write-Host " OK" -ForegroundColor Green
    Write-Host ""
    Write-Host "   Secret Name: $($secret.name)"
    Write-Host "   Fields:"
    foreach ($item in $secret.items) {
        $val = if ($item.isPassword) { "********" } else { $item.itemValue }
        Write-Host "     $($item.fieldName) = $val"
    }
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "   $(Get-SafeHttpError $_)" -ForegroundColor Red
    exit 1
}

Write-Host "`n--- All OK ---" -ForegroundColor Green

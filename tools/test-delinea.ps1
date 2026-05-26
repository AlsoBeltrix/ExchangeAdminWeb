param(
    [string]$ServerUrl = "https://secretserver.ad.analog.com/secretserver",
    [string]$Username,
    [string]$Password,
    [string]$GrantType = "password",
    [int]$SecretId = 31
)

if (-not $Username) { $Username = Read-Host "Username" }
if (-not $Password) {
    $Password = Read-Host "Password" -AsSecureString |
        ForEach-Object { [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) }
}

Write-Host "`n--- Delinea REST Test ---" -ForegroundColor Cyan
Write-Host "Server:    $ServerUrl"
Write-Host "Grant:     $GrantType"
Write-Host "Username:  $Username"
Write-Host "SecretId:  $SecretId"
Write-Host ""

# Step 1: Get token
Write-Host "[1] Authenticating..." -NoNewline
try {
    if ($GrantType -eq "password") {
        $body = @{
            grant_type = "password"
            username   = $Username
            password   = $Password
        }
    } else {
        $body = @{
            grant_type    = "client_credentials"
            client_id     = $Username
            client_secret = $Password
        }
    }
    $tokenResponse = Invoke-RestMethod -Uri "$ServerUrl/oauth2/token" -Method POST -Body $body -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop
    Write-Host " OK" -ForegroundColor Green
    Write-Host "   Token expires in $($tokenResponse.expires_in)s"
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "   $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) { Write-Host "   Response: $($_.ErrorDetails.Message)" -ForegroundColor Yellow }
    exit 1
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
    Write-Host "   $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) { Write-Host "   Response: $($_.ErrorDetails.Message)" -ForegroundColor Yellow }
    exit 1
}

Write-Host "`n--- All OK ---" -ForegroundColor Green

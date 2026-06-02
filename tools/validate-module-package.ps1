param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$HostPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,

    [switch]$TreatWarningsAsErrors
)

$ErrorActionPreference = "Stop"

$issues = New-Object System.Collections.Generic.List[object]

function Resolve-FullPath {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Path not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $baseFull = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $baseFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseFull += [System.IO.Path]::DirectorySeparatorChar
    }

    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $baseUri = New-Object System.Uri($baseFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace("/", "\")
}

function Add-Issue {
    param(
        [ValidateSet("Error", "Warning")]
        [string]$Severity,
        [string]$Code,
        [string]$Message,
        [string]$Path = "",
        [int]$Line = 0,
        [string]$Text = ""
    )

    $issues.Add([pscustomobject]@{
        Severity = $Severity
        Code = $Code
        Message = $Message
        Path = $Path
        Line = $Line
        Text = $Text.Trim()
    }) | Out-Null
}

function Get-PackageFiles {
    param(
        [string]$Root,
        [string[]]$Extensions
    )

    Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $Extensions -contains $_.Extension.ToLowerInvariant() }
}

function Search-Files {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$Pattern,
        [string]$Severity,
        [string]$Code,
        [string]$Message
    )

    foreach ($file in $Files) {
        $matches = Select-String -LiteralPath $file.FullName -Pattern $Pattern -AllMatches
        foreach ($match in $matches) {
            Add-Issue `
                -Severity $Severity `
                -Code $Code `
                -Message $Message `
                -Path (Get-RelativePath $packageRoot $file.FullName) `
                -Line $match.LineNumber `
                -Text $match.Line
        }
    }
}

function Get-FirstRegexGroup {
    param(
        [string]$Text,
        [string]$Pattern
    )

    $match = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return $null
}

$packageRoot = Resolve-FullPath $PackagePath
$hostRoot = Resolve-FullPath $HostPath

$requiredDirs = @("src", "integration", "tests", "docs")
foreach ($dir in $requiredDirs) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $dir))) {
        Add-Issue "Error" "PKG001" "Required package directory is missing: $dir"
    }
}

$moduleCatalogSnippet = Join-Path $packageRoot "integration\ModuleCatalog.snippet.cs"
$programSnippet = Join-Path $packageRoot "integration\Program.snippet.cs"

if (-not (Test-Path -LiteralPath $moduleCatalogSnippet)) {
    Add-Issue "Error" "PKG002" "Missing integration snippet: integration\ModuleCatalog.snippet.cs"
}

if (-not (Test-Path -LiteralPath $programSnippet)) {
    Add-Issue "Error" "PKG003" "Missing integration snippet: integration\Program.snippet.cs"
}

$rootProgram = Join-Path $packageRoot "Program.cs"
if (Test-Path -LiteralPath $rootProgram) {
    Add-Issue "Error" "PKG004" "Package must not include a standalone production Program.cs." "Program.cs"
}

$csprojFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter "*.csproj"
foreach ($file in $csprojFiles) {
    Add-Issue "Error" "PKG005" "Package must not include a standalone project file." (Get-RelativePath $packageRoot $file.FullName)
}

$buildDirs = Get-ChildItem -LiteralPath $packageRoot -Recurse -Directory |
    Where-Object { $_.Name -in @("bin", "obj") }
foreach ($dir in $buildDirs) {
    Add-Issue "Error" "PKG006" "Package must not include build output directories." (Get-RelativePath $packageRoot $dir.FullName)
}

$allowedTopLevel = @("src", "integration", "tests", "docs", "README.md", "AdminModuleDeveloperGuide.md")
$topLevelItems = Get-ChildItem -LiteralPath $packageRoot -Force
foreach ($item in $topLevelItems) {
    if ($allowedTopLevel -notcontains $item.Name) {
        Add-Issue "Warning" "PKG007" "Unexpected top-level package item. Keep deliverables limited to src, integration, tests, and docs." $item.Name
    }
}

$srcRoot = Join-Path $packageRoot "src"
$srcFiles = @()
if (Test-Path -LiteralPath $srcRoot) {
    $srcFiles = @(Get-PackageFiles $srcRoot @(".cs", ".razor"))
}

$allTextFiles = @(Get-PackageFiles $packageRoot @(".cs", ".razor", ".md", ".json", ".ps1", ".txt"))

Search-Files $srcFiles "\b(class|record|interface)\s+(GraphTokenClient|AuditService|OperationTraceService|ModuleConfigService|ModuleCredentialService|ProtectedPrincipalService|ClientInfoService|DelineaService)\b" "Error" "HOST001" "Production module source must not declare host service types."
Search-Files $srcFiles "\bIDelineaService\b" "Error" "HOST002" "The real host exposes DelineaService directly, not IDelineaService."
Search-Files $srcFiles "\b(Fake|Mock)[A-Za-z0-9_]*\b" "Error" "HOST003" "Production module source must not reference fake/mock host services."
Search-Files $allTextFiles "RequireAssertion\s*\([^)]*=>\s*true\s*\)" "Error" "AUTH001" "Package must not define always-allow authorization policies."
Search-Files $srcFiles "127\.0\.0\.1" "Error" "AUDIT001" "Production module source must not hardcode audit/client IP addresses."
Search-Files $srcFiles "\b(scope|operationScope)\.Step\s*\(" "Error" "TRACE001" "Operation trace steps must be written through OperationTraceService.Step(...), not OperationScope."
Search-Files $srcFiles "\b(ex|exception)\.StackTrace\b|Exception\.StackTrace|StackTrace" "Error" "SAFE001" "UI-bound production source must not expose raw stack traces."
Search-Files $srcFiles "catch\s*\{\s*\}" "Warning" "SAFE002" "Empty catch block found. Log or intentionally handle the failure."
Search-Files $srcFiles "ServiceNow" "Warning" "TICKET001" "ServiceNow wording found. Use generic Ticket Number unless ServiceNow validation is explicitly required."

$graphLiteralPatterns = @(
    "\b(GetAsync|PostAsync|PostNoContentAsync|PatchAsync|DeleteAsync)\(\s*`"(?!/)",
    "\b(GetAsync|PostAsync|PostNoContentAsync|PatchAsync|DeleteAsync)\(\s*\$`"(?!/)"
)
foreach ($pattern in $graphLiteralPatterns) {
    Search-Files $srcFiles $pattern "Error" "GRAPH001" "Literal GraphTokenClient endpoints must start with '/'."
}

$descriptorText = ""
$moduleId = $null
$route = $null
$mainPolicy = $null
$iconCss = $null

if (Test-Path -LiteralPath $moduleCatalogSnippet) {
    $descriptorText = Get-Content -LiteralPath $moduleCatalogSnippet -Raw
    $moduleId = Get-FirstRegexGroup $descriptorText 'Id\s*=\s*"([^"]+)"'
    $route = Get-FirstRegexGroup $descriptorText 'Route\s*=\s*"([^"]+)"'
    $iconCss = Get-FirstRegexGroup $descriptorText 'IconCss\s*=\s*"([^"]+)"'
    $mainPolicy = Get-FirstRegexGroup $descriptorText 'MainPermission\s*=\s*new\s*\(\s*"[^"]+"\s*,\s*"([^"]+)"'

    if (-not $moduleId) {
        Add-Issue "Error" "CAT001" "ModuleCatalog snippet does not declare Id."
    }

    if (-not $route) {
        Add-Issue "Error" "CAT002" "ModuleCatalog snippet does not declare Route."
    }
    elseif ($route.StartsWith("/")) {
        Add-Issue "Error" "CAT003" "Descriptor Route must not start with '/'." "integration\ModuleCatalog.snippet.cs"
    }

    if ($descriptorText -notmatch 'EnabledByDefault\s*=\s*false') {
        Add-Issue "Warning" "CAT004" "New optional modules should normally use EnabledByDefault = false." "integration\ModuleCatalog.snippet.cs"
    }

    if ($descriptorText -match 'IsSystemModule\s*=\s*true') {
        Add-Issue "Error" "CAT005" "Optional contributed modules must not set IsSystemModule = true." "integration\ModuleCatalog.snippet.cs"
    }

    if ($descriptorText -notmatch 'MainPermission\s*=\s*new\s*\([^)]*FailClosed\s*:\s*true') {
        Add-Issue "Error" "CAT006" "Privileged modules must set FailClosed: true on MainPermission." "integration\ModuleCatalog.snippet.cs"
    }

    if (-not $iconCss) {
        Add-Issue "Error" "CAT007" "ModuleCatalog snippet does not declare IconCss."
    }
    else {
        $iconTokens = $iconCss -split "\s+" | Where-Object { $_ -and $_ -ne "bi" }
        $navTokens = @($iconTokens | Where-Object { $_ -like "*-nav-menu" })
        if ($navTokens.Count -eq 0) {
            Add-Issue "Error" "CAT008" "IconCss should use an existing nav-menu icon class, e.g. 'bi bi-geo-alt-fill-nav-menu'." "integration\ModuleCatalog.snippet.cs"
        }

        $hostIconCssPath = Join-Path $hostRoot "Components\Layout\NavMenu.razor.css"
        if (Test-Path -LiteralPath $hostIconCssPath) {
            $hostIconCss = Get-Content -LiteralPath $hostIconCssPath -Raw
            foreach ($token in $navTokens) {
                if ($hostIconCss -notmatch [regex]::Escape("." + $token)) {
                    Add-Issue "Error" "CAT009" "IconCss references a nav-menu class that does not exist in host CSS: $token" "integration\ModuleCatalog.snippet.cs"
                }
            }
        }
    }
}

if (Test-Path -LiteralPath $programSnippet) {
    $programText = Get-Content -LiteralPath $programSnippet -Raw
    if ($programText -notmatch 'AddScoped\s*<\s*[A-Za-z0-9_]+Service\s*>\s*\(') {
        Add-Issue "Warning" "DI001" "Program snippet should register the module service, normally with AddScoped<TService>()." "integration\Program.snippet.cs"
    }

    if ($programText -match 'AddSingleton\s*<\s*[A-Za-z0-9_]+Service\s*>') {
        Add-Issue "Warning" "DI002" "Module services that use UI-circuit state, PowerShell, or backend clients should normally be scoped, not singleton." "integration\Program.snippet.cs"
    }
}

if ($route) {
    $pageFiles = @()
    if (Test-Path -LiteralPath (Join-Path $srcRoot "Components\Pages")) {
        $pageFiles = @(Get-ChildItem -LiteralPath (Join-Path $srcRoot "Components\Pages") -Recurse -File -Filter "*.razor")
    }

    $routePattern = '@page\s+"\/' + [regex]::Escape($route) + '"'
    $matchingPages = @()
    foreach ($page in $pageFiles) {
        $pageText = Get-Content -LiteralPath $page.FullName -Raw
        if ($pageText -match $routePattern) {
            $matchingPages += $page
        }
    }

    if ($matchingPages.Count -eq 0) {
        Add-Issue "Error" "PAGE001" "No Razor page in src declares @page `"\/$route`"."
    }
    elseif ($matchingPages.Count -gt 1) {
        Add-Issue "Error" "PAGE002" "Multiple Razor pages declare route /$route."
    }
    else {
        $page = $matchingPages[0]
        $pageRel = Get-RelativePath $packageRoot $page.FullName
        $pageText = Get-Content -LiteralPath $page.FullName -Raw

        if ($mainPolicy -and $pageText -notmatch '\[Authorize\(Policy\s*=\s*"' + [regex]::Escape($mainPolicy) + '"\)\]') {
            Add-Issue "Error" "PAGE003" "Razor page Authorize policy does not match descriptor MainPermission policy: $mainPolicy" $pageRel
        }

        if ($pageText -notmatch '@rendermode') {
            Add-Issue "Error" "PAGE004" "Razor page is missing @rendermode InteractiveServer." $pageRel
        }

        if ($pageText -notmatch '@inject\s+AuthenticationStateProvider\s+AuthStateProvider') {
            Add-Issue "Error" "PAGE005" "Razor page should inject AuthenticationStateProvider AuthStateProvider." $pageRel
        }

        if ($pageText -notmatch '@inject\s+IAuthorizationService\s+AuthorizationService') {
            Add-Issue "Error" "PAGE006" "Razor page should inject IAuthorizationService AuthorizationService." $pageRel
        }

        if ($pageText -notmatch '@inject\s+ClientInfoService\s+ClientInfo') {
            Add-Issue "Error" "PAGE007" "Razor page should inject ClientInfoService ClientInfo for audit IP attribution." $pageRel
        }

        if ($pageText -match 'string\.IsNullOrWhiteSpace\([^)]*ticket' -and $pageText -notmatch '@bind:event\s*=\s*"oninput"') {
            Add-Issue "Warning" "PAGE008" "Page appears to use ticket-controlled buttons but no @bind:event=`"oninput`" was found." $pageRel
        }
    }
}

$testFakeFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter "*.cs" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '(Fake|Mock)' })
foreach ($fake in $testFakeFiles) {
    $rel = Get-RelativePath $packageRoot $fake.FullName
    if ($rel -notlike "tests\*" -and $rel -notlike "docs\*") {
        Add-Issue "Warning" "TEST001" "Fake/mock code should live under tests/, not under production or host-shaped folders." $rel
    }
}

$errorCount = @($issues | Where-Object { $_.Severity -eq "Error" }).Count
$warningCount = @($issues | Where-Object { $_.Severity -eq "Warning" }).Count

Write-Host ""
Write-Host "ExchangeAdminWeb module package validation"
Write-Host "  Package : $packageRoot"
Write-Host "  Host    : $hostRoot"
Write-Host ""

foreach ($issue in ($issues | Sort-Object Severity, Code, Path, Line)) {
    $location = if ($issue.Path) {
        if ($issue.Line -gt 0) { "$($issue.Path):$($issue.Line)" } else { $issue.Path }
    } else {
        ""
    }

    $prefix = if ($location) { "$($issue.Severity) $($issue.Code) [$location]" } else { "$($issue.Severity) $($issue.Code)" }
    Write-Host "$prefix - $($issue.Message)"
    if ($issue.Text) {
        Write-Host "    $($issue.Text)"
    }
}

if ($issues.Count -eq 0) {
    Write-Host "No issues found."
}

Write-Host ""
Write-Host "Summary: $errorCount error(s), $warningCount warning(s)"

if ($errorCount -gt 0 -or ($TreatWarningsAsErrors -and $warningCount -gt 0)) {
    exit 1
}

exit 0

#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Execution tests for tools/validate-module-package.ps1 page-level checks.
Unlike DeployInvariants.Tests.ps1 (static AST parsing), these build a minimal
module package fixture in a temp directory and run the validator end to end,
then assert on the emitted issue codes.

Scope today: PAGE009 - every module Razor page must display its descriptor
Version via the <ModuleVersion /> component. This guards the "canonical,
enforced rule" claim in AdminModuleSpec.md / AdminModuleDeveloperGuide.md.
#>

BeforeAll {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $Validator = Join-Path $RepoRoot 'tools\validate-module-package.ps1'

    # Build a minimal, route-matching module package under $Root. The page is
    # written with or without the <ModuleVersion /> component depending on
    # $IncludeModuleVersion. The rest of the package only needs to be complete
    # enough that the validator reaches the single-page block (PAGE003-PAGE009);
    # the tests assert on the PAGE009 token alone, so unrelated issues do not
    # affect the result.
    function New-ModulePackageFixture {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
            Justification = 'Test helper builds a throwaway fixture under the temp dir; ShouldProcess is noise here')]
        param(
            [string]$Root,
            [bool]$IncludeModuleVersion
        )

        New-Item -ItemType Directory -Path $Root -Force | Out-Null
        foreach ($dir in @('src\Components\Pages', 'integration', 'tests', 'docs')) {
            New-Item -ItemType Directory -Path (Join-Path $Root $dir) -Force | Out-Null
        }

        $catalogSnippet = @'
new AdminModuleDescriptor
{
    Id = "MyModule",
    Route = "my-module",
    IconCss = "bi bi-geo-alt-fill-nav-menu",
    Category = "Other",
    SortOrder = 800,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    MainPermission = new("Access", "MyModule", FailClosed: true)
}
'@
        Set-Content -LiteralPath (Join-Path $Root 'integration\ModuleCatalog.snippet.cs') -Value $catalogSnippet

        Set-Content -LiteralPath (Join-Path $Root 'integration\Program.snippet.cs') `
            -Value 'builder.Services.AddScoped<MyModuleService>();'

        $moduleVersionLine = if ($IncludeModuleVersion) { '    <ModuleVersion />' } else { '    <!-- version display intentionally omitted -->' }
        $pageText = @"
@page "/my-module"
@rendermode InteractiveServer
@attribute [Authorize(Policy = "MyModule")]
@inject AuthenticationStateProvider AuthStateProvider
@inject IAuthorizationService AuthorizationService
@inject ClientInfoService ClientInfo

<h1>My Module</h1>
$moduleVersionLine
"@
        Set-Content -LiteralPath (Join-Path $Root 'src\Components\Pages\MyModule.razor') -Value $pageText

        return $Root
    }

    function Invoke-Validator {
        param([string]$PackagePath)
        # Capture all host output as text; the validator writes issue lines via Write-Host.
        $output = & $Validator -PackagePath $PackagePath -HostPath $RepoRoot *>&1 | Out-String
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = $output
        }
    }
}

Describe 'validate-module-package.ps1 PAGE009 (ModuleVersion component required)' {

    It 'parses without syntax errors' {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($Validator, [ref]$tokens, [ref]$errors) | Out-Null
        $errors | Should -BeNullOrEmpty
    }

    It 'raises PAGE009 when the page lacks the ModuleVersion component' {
        $pkg = Join-Path ([System.IO.Path]::GetTempPath()) ("eaw-mv-missing-" + [Guid]::NewGuid().ToString('N'))
        try {
            New-ModulePackageFixture -Root $pkg -IncludeModuleVersion $false | Out-Null
            $result = Invoke-Validator -PackagePath $pkg
            $result.Output | Should -Match 'PAGE009'
            $result.ExitCode | Should -Be 1
        }
        finally {
            Remove-Item -LiteralPath $pkg -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'does not raise PAGE009 when the page includes the ModuleVersion component' {
        $pkg = Join-Path ([System.IO.Path]::GetTempPath()) ("eaw-mv-present-" + [Guid]::NewGuid().ToString('N'))
        try {
            New-ModulePackageFixture -Root $pkg -IncludeModuleVersion $true | Out-Null
            $result = Invoke-Validator -PackagePath $pkg
            $result.Output | Should -Not -Match 'PAGE009'
            $result.ExitCode | Should -Be 0
        }
        finally {
            Remove-Item -LiteralPath $pkg -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

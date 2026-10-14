[CmdletBinding()]
param(
    [switch]$SkipUiScreenshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path -Path $projectRoot -ChildPath 'MicuQuotaWidget.sln'
$coreTests = Join-Path -Path $projectRoot -ChildPath 'tests\MicuQuotaWidget.Core.Tests\MicuQuotaWidget.Core.Tests.csproj'
$uiTests = Join-Path -Path $projectRoot -ChildPath 'tests\MicuQuotaWidget.UiTests\MicuQuotaWidget.UiTests.csproj'
$appProject = Join-Path -Path $projectRoot -ChildPath 'src\MicuQuotaWidget\MicuQuotaWidget.csproj'
$publishDirectory = Join-Path -Path $projectRoot -ChildPath 'artifacts\publish-smoke'

foreach ($path in @($solutionPath, $coreTests, $uiTests, $appProject)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required project file not found: $path"
    }
}

Push-Location $projectRoot

try {
    if (Test-Path -LiteralPath (Join-Path -Path $projectRoot -ChildPath '.git')) {
        Invoke-NativeCommand -FilePath 'git' -Arguments @('diff', '--check')
    }

    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('restore', $solutionPath)
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('format', $solutionPath, '--verify-no-changes', '--no-restore')
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('build', $solutionPath, '--configuration', 'Release', '--no-restore')
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('test', $coreTests, '--configuration', 'Release', '--no-restore')
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('test', $uiTests, '--configuration', 'Release', '--no-restore')

    if (-not $SkipUiScreenshot) {
        & (Join-Path -Path $PSScriptRoot -ChildPath 'Test-Ui.ps1') -NoRestore

        if ($LASTEXITCODE -ne 0) {
            throw "UI screenshot script failed with exit code $LASTEXITCODE"
        }
    }

    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @(
        'publish',
        $appProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $publishDirectory,
        '--no-restore')
}
finally {
    Pop-Location
}

Write-Output '[OK] Quality gate completed.'

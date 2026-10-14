[CmdletBinding()]
param(
    [switch]$NoRestore
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
$uiProject = Join-Path -Path $projectRoot -ChildPath 'tests\MicuQuotaWidget.UiTests\MicuQuotaWidget.UiTests.csproj'
$artifactDirectory = Join-Path -Path $projectRoot -ChildPath 'artifacts'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$capturePath = Join-Path -Path $artifactDirectory -ChildPath "ui-smoke-$timestamp.png"
$settingsCapturePath = Join-Path -Path $artifactDirectory -ChildPath "ui-settings-$timestamp.png"

if (-not (Test-Path -LiteralPath $uiProject)) {
    throw "UI test project not found: $uiProject"
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$env:MICU_WIDGET_CAPTURE_PATH = $capturePath
$env:MICU_SETTINGS_CAPTURE_PATH = $settingsCapturePath

try {
    $arguments = @('test', $uiProject, '--configuration', 'Release')

    if ($NoRestore) {
        $arguments += '--no-restore'
    }

    Invoke-NativeCommand -FilePath 'dotnet' -Arguments $arguments
}
finally {
    Remove-Item Env:MICU_WIDGET_CAPTURE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:MICU_SETTINGS_CAPTURE_PATH -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $capturePath)) {
    throw "UI test completed without producing the expected screenshot: $capturePath"
}

if (-not (Test-Path -LiteralPath $settingsCapturePath)) {
    throw "UI test completed without producing the expected settings screenshot: $settingsCapturePath"
}

Write-Output "[OK] UI screenshot: $capturePath"
Write-Output "[OK] Settings screenshot: $settingsCapturePath"

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$deliveryPath = Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-Delivery.ps1'
$workflowPath = Join-Path -Path $projectRoot -ChildPath '.github\workflows\release.yml'
$ignorePath = Join-Path -Path $projectRoot -ChildPath '.gitignore'

foreach ($requiredPath in @($deliveryPath, $workflowPath, $ignorePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release-security file is missing: $requiredPath"
    }
}

$deliveryContent = Get-Content -LiteralPath $deliveryPath -Raw -Encoding UTF8
$workflowContent = Get-Content -LiteralPath $workflowPath -Raw -Encoding UTF8
$ignoreContent = Get-Content -LiteralPath $ignorePath -Raw -Encoding UTF8

# These markers keep the public-release safety controls from being removed silently.
foreach ($marker in @('Assert-NoSensitiveStagedContent', 'Assert-PublicGitHistory', 'Get-SafeGitHubOrigin', '--no-gpg-sign', '--no-sign')) {
    if ($deliveryContent.IndexOf($marker, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Release delivery script is missing the required safety control: $marker"
    }
}

if ($deliveryContent.IndexOf('$parsedReleaseList = $releaseListJson | ConvertFrom-Json', [System.StringComparison]::Ordinal) -lt 0 -or
    $deliveryContent.IndexOf('if ([bool]$parsedReleaseList)', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'Release delivery script does not safely handle an empty GitHub Release list.'
}

# Windows PowerShell represents an empty JSON array as an empty object array.
$emptyReleaseList = '[]' | ConvertFrom-Json

if (@($emptyReleaseList).Count -ne 0) {
    throw 'Release preflight must treat an empty GitHub Release list as empty.'
}

foreach ($ignoreRule in @('.env.*', 'local.settings.json', 'secrets.json', 'credentials.json', '*.pfx', '*.p12', '*.pem', '*.key', '*.snk')) {
    if ($ignoreContent.IndexOf($ignoreRule, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Git ignore rules are missing the required secret safeguard: $ignoreRule"
    }
}

if ($workflowContent -notmatch '(?m)^\s*RELEASE_TAG:\s*\$\{\{\s*github\.ref_name\s*\}\}\s*$') {
    throw 'Release workflow does not pass the tag through a dedicated environment variable.'
}

$refNameExpressionCount = [System.Text.RegularExpressions.Regex]::Matches($workflowContent, '\$\{\{\s*github\.ref_name\s*\}\}').Count

if ($refNameExpressionCount -ne 1) {
    throw 'Release workflow contains an unexpected GitHub ref-name interpolation.'
}

if ($workflowContent -notmatch 'actions/checkout@[0-9a-f]{40}' -or $workflowContent -notmatch 'actions/setup-dotnet@[0-9a-f]{40}') {
    throw 'Release workflow must pin third-party actions to immutable commit SHAs.'
}

if ($workflowContent -match 'uses:\s+actions/(checkout|setup-dotnet)@v\d') {
    throw 'Release workflow still uses a mutable major-version action tag.'
}

if ($workflowContent -notmatch '(?m)^\s*persist-credentials:\s*false\s*$') {
    throw 'Release workflow must disable checkout credential persistence.'
}

if ($workflowContent.IndexOf('$env:RELEASE_TAG', [System.StringComparison]::Ordinal) -lt 0) {
    throw 'Release workflow does not read the validated release tag from the environment.'
}

Write-Output '[OK] Release security contract checks completed.'

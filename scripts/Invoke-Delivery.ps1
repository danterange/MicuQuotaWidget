[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Paths,

    [string]$Message = 'feat: update MicuQuotaWidget',

    [switch]$Commit,

    [switch]$Push,

    [switch]$Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publicGitAuthorName = 'MicuQuotaWidget contributors'
$publicGitAuthorEmail = 'noreply@users.noreply.github.com'

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

function Get-ValidatedRelativePaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string[]]$CandidatePaths
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd([char[]]@('\', '/'))
    $validPaths = New-Object System.Collections.Generic.List[string]

    foreach ($candidatePath in $CandidatePaths) {
        $isUnsafePath = [string]::IsNullOrWhiteSpace($candidatePath) -or
                        ([System.IO.Path]::IsPathRooted($candidatePath)) -or
                        ($candidatePath.StartsWith(':(')) -or
                        ($candidatePath -match '[*?\[]')

        if ($isUnsafePath) {
            throw "Unsafe Git pathspec: $candidatePath"
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path -Path $resolvedRoot -ChildPath $candidatePath))

        if (-not $fullPath.StartsWith("$resolvedRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Path is outside the project root: $candidatePath"
        }

        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Path does not exist and cannot be staged: $candidatePath"
        }

        $relativePath = $fullPath.Substring($resolvedRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        [void]$validPaths.Add($relativePath)
    }

    return @($validPaths | Select-Object -Unique)
}

function Assert-NoStagedChanges {
    $stagedPaths = @(& git -c core.quotePath=false diff --cached --name-only)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect staged changes: exit code $LASTEXITCODE"
    }

    if (@($stagedPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'The index already contains changes. Review or commit them before running the delivery script.'
    }
}

function Assert-OnlyExpectedPathsAreStaged {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$AllowedPaths
    )

    $stagedPaths = @(& git -c core.quotePath=false diff --cached --name-only)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect staged paths: exit code $LASTEXITCODE"
    }

    foreach ($stagedPath in $stagedPaths) {
        $allowed = $false

        foreach ($allowedPath in $AllowedPaths) {
            if ($stagedPath -eq $allowedPath -or $stagedPath.StartsWith("$allowedPath/", [System.StringComparison]::Ordinal)) {
                $allowed = $true
                break
            }
        }

        if (-not $allowed) {
            throw "Unexpected path entered the index: $stagedPath"
        }
    }
}

# Reads origin without printing it and accepts only credential-free GitHub URLs.
function Get-SafeGitHubOrigin {
    $originUrl = (& git remote get-url origin).Trim()

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($originUrl)) {
        throw 'Push requires a configured origin remote.'
    }

    $originMatch = [System.Text.RegularExpressions.Regex]::Match(
        $originUrl,
        '^(?:https://github\.com/|git@github\.com:)(?<owner>[A-Za-z0-9][A-Za-z0-9_.-]*)/(?<repository>[A-Za-z0-9][A-Za-z0-9_.-]*?)(?:\.git)?$')

    if (-not $originMatch.Success) {
        throw 'Origin must be a credential-free GitHub HTTPS or SSH URL.'
    }

    return [pscustomobject]@{
        NameWithOwner = "$($originMatch.Groups['owner'].Value)/$($originMatch.Groups['repository'].Value)"
    }
}

# Rejects public history whose author or committer metadata is not the project noreply identity.
function Assert-PublicGitHistory {
    $identityRecords = @(& git log --format='%an%x1f%ae%x1f%cn%x1f%ce' HEAD)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect public commit identities: exit code $LASTEXITCODE"
    }

    $unsafeCommitCount = 0

    foreach ($identityRecord in $identityRecords) {
        $fields = $identityRecord.Split([char[]]@([char]0x1f))

        if ($fields.Count -ne 4 -or
            $fields[0] -ne $publicGitAuthorName -or
            $fields[1] -ne $publicGitAuthorEmail -or
            $fields[2] -ne $publicGitAuthorName -or
            $fields[3] -ne $publicGitAuthorEmail) {
            $unsafeCommitCount++
        }
    }

    if ($unsafeCommitCount -gt 0) {
        throw "Public history identity guard rejected $unsafeCommitCount commit(s) without the project noreply identity."
    }
}

# Scans staged additions and modifications without echoing matched content or credentials.
function Assert-NoSensitiveStagedContent {
    $stagedPaths = @(& git -c core.quotePath=false diff --cached --diff-filter=ACMR --name-only)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect staged content: exit code $LASTEXITCODE"
    }

    $sensitivePathPattern = '(?i)(^|/)(\.env(?:\.|$)|.*(?:secret|credential|password|token).*(?:\.json|\.config|\.xml|\.ini|\.txt)$|.*\.(?:pfx|p12|pem|key|snk)$)'
    $sensitivePatterns = [ordered]@{
        'PEM private key' = '-----BEGIN(?: [A-Z]+)* PRIVATE KEY-----'
        'GitHub token' = '\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
        'OpenAI-style API key' = '\bsk-[A-Za-z0-9_-]{20,}\b'
        'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
        'Bearer credential' = '(?i)\bAuthorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._~+/-]{20,}'
        'credential literal' = '(?im)(?:["'']?(?:api[-_]?key|access[-_]?token|password|client[-_]?secret|secret)[A-Za-z0-9_-]*["'']?\s*[:=]\s*["''][^"''\r\n]{12,}["''])'
        'non-whitelisted email address' = '(?i)\b[A-Z0-9._%+-]+@(?!users\.noreply\.github\.com\b|example\.com\b|invalid\b)[A-Z0-9.-]+\.[A-Z]{2,}\b'
        'local Windows user path' = '(?i)[A-Z]:\\Users\\'
    }
    $violations = New-Object System.Collections.Generic.List[string]

    foreach ($stagedPath in $stagedPaths) {
        if ($stagedPath -match $sensitivePathPattern) {
            $violations.Add("$stagedPath [sensitive file name]")
            continue
        }

        $stagedContentLines = @(& git show ":$stagedPath")
        $showExitCode = $LASTEXITCODE

        if ($showExitCode -ne 0) {
            throw "Unable to inspect staged file ${stagedPath}: exit code $showExitCode"
        }

        $stagedContent = $stagedContentLines -join "`n"

        foreach ($patternName in $sensitivePatterns.Keys) {
            $match = [System.Text.RegularExpressions.Regex]::Match($stagedContent, $sensitivePatterns[$patternName])

            if ($match.Success) {
                $lineNumber = 1 + [System.Text.RegularExpressions.Regex]::Matches($stagedContent.Substring(0, $match.Index), "`n").Count
                $violations.Add("${stagedPath}:$lineNumber [$patternName]")
            }
        }
    }

    if ($violations.Count -gt 0) {
        throw "Sensitive staged content guard rejected $($violations.Count) finding(s): $($violations -join '; ')"
    }
}

# Creates an annotated tag whose tagger metadata cannot inherit a personal Git identity.
function New-PublicAnnotatedTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TagName
    )

    Invoke-NativeCommand -FilePath 'git' -Arguments @(
        '-c', "user.name=$publicGitAuthorName",
        '-c', "user.email=$publicGitAuthorEmail",
        'tag', '--no-sign', '-a', $TagName, '-m', "Release $TagName")

    $taggerRecord = (& git for-each-ref --format='%(taggername)%x1f%(taggeremail)' "refs/tags/$TagName").Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect release tag identity: exit code $LASTEXITCODE"
    }

    $taggerFields = $taggerRecord.Split([char[]]@([char]0x1f))

    if ($taggerFields.Count -ne 2 -or
        $taggerFields[0] -ne $publicGitAuthorName -or
        $taggerFields[1] -ne "<$publicGitAuthorEmail>") {
        throw 'Release tag identity guard rejected a non-noreply tagger.'
    }
}

function Commit-ExactPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$RelativePaths,

        [Parameter(Mandatory = $true)]
        [string]$CommitMessage
    )

    Assert-NoStagedChanges
    $addArguments = @('add', '--') + $RelativePaths
    Invoke-NativeCommand -FilePath 'git' -Arguments $addArguments
    Assert-OnlyExpectedPathsAreStaged -AllowedPaths $RelativePaths
    Invoke-NativeCommand -FilePath 'git' -Arguments @('diff', '--cached', '--check')
    Assert-NoSensitiveStagedContent

    & git diff --cached --quiet

    if ($LASTEXITCODE -eq 0) {
        return $false
    }

    if ($LASTEXITCODE -ne 1) {
        throw "Unable to inspect staged diff: exit code $LASTEXITCODE"
    }

    Invoke-NativeCommand -FilePath 'git' -Arguments @(
        '-c', "user.name=$publicGitAuthorName",
        '-c', "user.email=$publicGitAuthorEmail",
        'commit', '--no-gpg-sign', '-m', $CommitMessage)
    return $true
}

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Replacement,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $regex = New-Object System.Text.RegularExpressions.Regex($Pattern)

    if ($regex.Matches($Content).Count -ne 1) {
        throw "Expected exactly one $Description marker."
    }

    return $regex.Replace($Content, $Replacement, 1)
}

function Write-Utf8BomFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $temporaryPath = "$LiteralPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $encoding = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($temporaryPath, $Content, $encoding)
    Move-Item -LiteralPath $temporaryPath -Destination $LiteralPath -Force
}

function Get-NextPatchVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PropsPath
    )

    $propsContent = Get-Content -LiteralPath $PropsPath -Raw -Encoding UTF8
    $match = [System.Text.RegularExpressions.Regex]::Match($propsContent, '<Version>([^<]+)</Version>')

    if (-not $match.Success) {
        throw 'Directory.Build.props does not contain a Version element.'
    }

    $version = [Version]$match.Groups[1].Value
    return "$($version.Major).$($version.Minor).$($version.Build + 1)"
}

function Update-ReleaseVersionFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $propsPath = Join-Path -Path $RootPath -ChildPath 'Directory.Build.props'
    $propsContent = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $propsContent = Replace-ExactlyOnce -Content $propsContent -Pattern '<Version>[^<]+</Version>' -Replacement "<Version>$Version</Version>" -Description 'Version'
    $propsContent = Replace-ExactlyOnce -Content $propsContent -Pattern '<AssemblyVersion>[^<]+</AssemblyVersion>' -Replacement "<AssemblyVersion>$Version.0</AssemblyVersion>" -Description 'AssemblyVersion'
    $propsContent = Replace-ExactlyOnce -Content $propsContent -Pattern '<FileVersion>[^<]+</FileVersion>' -Replacement "<FileVersion>$Version.0</FileVersion>" -Description 'FileVersion'
    $propsContent = Replace-ExactlyOnce -Content $propsContent -Pattern '<InformationalVersion>[^<]+</InformationalVersion>' -Replacement "<InformationalVersion>$Version</InformationalVersion>" -Description 'InformationalVersion'
    Write-Utf8BomFile -LiteralPath $propsPath -Content $propsContent

    $readmePath = Join-Path -Path $RootPath -ChildPath 'README.md'
    $readmeContent = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
    $readmeContent = Replace-ExactlyOnce -Content $readmeContent -Pattern '<!-- VERSION: [^>]+ -->' -Replacement "<!-- VERSION: $Version -->" -Description 'README version'

    if ($readmeContent -notmatch '## 中文' -or $readmeContent -notmatch '## English' -or $readmeContent -notmatch 'docs/images/') {
        throw 'README must retain Chinese, English, and image documentation before release.'
    }

    Write-Utf8BomFile -LiteralPath $readmePath -Content $readmeContent

    $releaseDirectory = Join-Path -Path $RootPath -ChildPath 'docs\releases'
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $releasePath = Join-Path -Path $releaseDirectory -ChildPath "v$Version.md"
    $releaseContent = @(
        "# v$Version 发布说明",
        '',
        '## 变更',
        '',
        '- 本版本包含已通过质量门禁和代码审查的 MicuQuotaWidget 变更。',
        '',
        '## 验证',
        '',
        '- Release build、unit test、mock HTTP integration test、WPF UI test 和 publish smoke check 已通过。',
        '',
        '## 兼容性',
        '',
        '- Windows 10/11 x64；需要 .NET 8 Desktop Runtime（或使用后续 self-contained 包）。',
        '',
        '## 升级',
        '',
        '- 安装新版本前关闭正在运行的小组件；升级后保留当前 Windows 用户的 Credential Manager 配置。',
        '- 若查询方式或余额字段变更，先阅读 `docs/米醋余额适配器.md`。',
        '',
        '## SHA-256',
        '',
        '- 以 GitHub Release 附件 `SHA256SUMS.txt` 为准。',
        '',
        '## 回滚',
        '',
        '- 使用上一个已验证的 GitHub Release；禁止覆盖已发布 tag 或附件。'
    ) -join "`r`n"
    Write-Utf8BomFile -LiteralPath $releasePath -Content $releaseContent

    $changelogPath = Join-Path -Path $RootPath -ChildPath 'CHANGELOG.md'
    $changelogContent = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
    $headingRegex = New-Object System.Text.RegularExpressions.Regex('(?m)^## \[')
    $firstHeading = $headingRegex.Match($changelogContent)

    if (-not $firstHeading.Success) {
        throw 'CHANGELOG.md has no version heading to insert before.'
    }

    $entry = "## [$Version] - $(Get-Date -Format 'yyyy-MM-dd')`r`n`r`n### Changed / 变更`r`n`r`n- 已创建 GitHub Release 流程对应的版本记录。`r`n`r`n"
    $changelogContent = $changelogContent.Insert($firstHeading.Index, $entry)
    Write-Utf8BomFile -LiteralPath $changelogPath -Content $changelogContent

    return @('Directory.Build.props', 'README.md', 'CHANGELOG.md', "docs/releases/v$Version.md")
}

function Assert-ReleasePreflight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $statusLines = @(& git status --porcelain=v1)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read Git status: exit code $LASTEXITCODE"
    }

    if (@($statusLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Release preflight requires a clean working tree after the product commit.'
    }

    $branch = (& git branch --show-current).Trim()

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
        throw 'Release preflight refuses a detached or unknown Git branch.'
    }

    $origin = Get-SafeGitHubOrigin
    Assert-PublicGitHistory

    Invoke-NativeCommand -FilePath 'git' -Arguments @('fetch', 'origin', '--tags', '--prune')
    $authStatus = & gh auth status 2>$null

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to verify GitHub CLI authentication: exit code $LASTEXITCODE"
    }

    $repoJson = & gh repo view $origin.NameWithOwner --json nameWithOwner

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the GitHub repository associated with origin: exit code $LASTEXITCODE"
    }

    $repo = $repoJson | ConvertFrom-Json

    if ($repo.nameWithOwner -ne $origin.NameWithOwner) {
        throw 'GitHub CLI repository does not match the credential-free origin remote.'
    }

    $tagName = "v$Version"
    $previousErrorActionPreference = $ErrorActionPreference

    try {
        $ErrorActionPreference = 'Continue'
        $existingTag = & git rev-parse -q --verify "refs/tags/$tagName" 2>$null
        $tagLookupExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($tagLookupExitCode -eq 0) {
        throw "Release tag already exists: $tagName"
    }

    if ($tagLookupExitCode -ne 1) {
        throw "Unable to inspect release tag ${tagName}: exit code $tagLookupExitCode"
    }

    # A successful list query distinguishes a truly absent release from auth or API failures.
    $releaseListJson = & gh release list --repo $origin.NameWithOwner --limit 100 --json tagName

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to list GitHub Releases: exit code $LASTEXITCODE"
    }

    # ConvertFrom-Json emits an empty Object[] for an empty JSON array in Windows PowerShell.
    # Normalize false-like empty values to an actual empty collection before strict-mode access.
    $parsedReleaseList = $releaseListJson | ConvertFrom-Json
    $existingReleases = @()

    if ([bool]$parsedReleaseList) {
        $existingReleases = @($parsedReleaseList)
    }

    if (@($existingReleases | Where-Object { $_.tagName -eq $tagName }).Count -gt 0) {
        throw "GitHub Release already exists: $tagName"
    }

    return [pscustomobject]@{
        Branch = $branch
        TagName = $tagName
    }
}

function Wait-ForReleaseWorkflow {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommitSha
    )

    $deadline = (Get-Date).AddMinutes(15)

    while ((Get-Date) -lt $deadline) {
        $runsJson = & gh run list --workflow release.yml --commit $CommitSha --limit 10 --json databaseId,status,conclusion,url

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to query GitHub Actions runs: exit code $LASTEXITCODE"
        }

        $runs = @($runsJson | ConvertFrom-Json)
        $run = $runs | Select-Object -First 1

        if ($null -ne $run) {
            if ($run.status -eq 'completed') {
                if ($run.conclusion -ne 'success') {
                    throw "Release workflow failed: $($run.url)"
                }

                return $run
            }
        }

        Start-Sleep -Seconds 10
    }

    throw 'Timed out waiting for the GitHub Actions release workflow.'
}

function Verify-ReleaseAssets {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$TagName,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $releaseJson = & gh release view $TagName --json isDraft,tagName,url,assets

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect GitHub Release ${TagName}: exit code $LASTEXITCODE"
    }

    $release = $releaseJson | ConvertFrom-Json

    if ($release.isDraft -or $release.tagName -ne $TagName) {
        throw "GitHub Release $TagName is not a matching published release."
    }

    $zipName = "MicuQuotaWidget-v$Version-windows-x64.zip"
    $manifestName = 'SHA256SUMS.txt'
    $assetNames = @($release.assets | ForEach-Object { $_.name })

    foreach ($requiredAsset in @($zipName, $manifestName)) {
        if ($assetNames -notcontains $requiredAsset) {
            throw "GitHub Release $TagName is missing required asset: $requiredAsset"
        }
    }

    $verificationDirectory = Join-Path -Path $RootPath -ChildPath ("artifacts\release-verification\$TagName")
    New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
    Invoke-NativeCommand -FilePath 'gh' -Arguments @('release', 'download', $TagName, '--pattern', $zipName, '--pattern', $manifestName, '--dir', $verificationDirectory, '--clobber')

    $zipPath = Join-Path -Path $verificationDirectory -ChildPath $zipName
    $manifestPath = Join-Path -Path $verificationDirectory -ChildPath $manifestName
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestLine = Get-Content -LiteralPath $manifestPath | Where-Object { $_ -match "\*?$([System.Text.RegularExpressions.Regex]::Escape($zipName))$" } | Select-Object -First 1

    $hashMatch = [System.Text.RegularExpressions.Regex]::Match($manifestLine, '^(?<hash>[a-fA-F0-9]{64})\s+')

    if ([string]::IsNullOrWhiteSpace($manifestLine) -or -not $hashMatch.Success) {
        throw "SHA256SUMS.txt has no valid entry for $zipName"
    }

    if ($actualHash -ne $hashMatch.Groups['hash'].Value.ToLowerInvariant()) {
        throw "Downloaded release ZIP hash does not match SHA256SUMS.txt for $zipName"
    }

    return $release.url
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$qualityGate = Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-QualityGate.ps1'
$propsPath = Join-Path -Path $projectRoot -ChildPath 'Directory.Build.props'
$releaseWorkflowPath = Join-Path -Path $projectRoot -ChildPath '.github\workflows\release.yml'

Push-Location $projectRoot

try {
    Invoke-NativeCommand -FilePath 'git' -Arguments @('rev-parse', '--is-inside-work-tree')
    $validatedPaths = Get-ValidatedRelativePaths -RootPath $projectRoot -CandidatePaths $Paths

    if ($Push -and -not $Commit) {
        throw 'Push requires a local commit created by this delivery invocation.'
    }

    if ($Release) {
        $Commit = $true
        $Push = $true
    }

    & $qualityGate

    if ($LASTEXITCODE -ne 0) {
        throw "Quality gate failed with exit code $LASTEXITCODE"
    }

    if ($Commit) {
        [void](Commit-ExactPaths -RelativePaths $validatedPaths -CommitMessage $Message)
    }

    if (-not $Release) {
        if ($Push) {
            [void](Get-SafeGitHubOrigin)
            Assert-PublicGitHistory
            Invoke-NativeCommand -FilePath 'git' -Arguments @('push', 'origin', 'HEAD')
        }

        return
    }

    if (-not (Test-Path -LiteralPath $releaseWorkflowPath)) {
        throw "Release workflow is missing: $releaseWorkflowPath"
    }

    $nextVersion = Get-NextPatchVersion -PropsPath $propsPath
    $releasePlan = Assert-ReleasePreflight -Version $nextVersion
    $versionPaths = Update-ReleaseVersionFiles -RootPath $projectRoot -Version $nextVersion

    & $qualityGate

    if ($LASTEXITCODE -ne 0) {
        throw "Quality gate failed after version update with exit code $LASTEXITCODE"
    }

    [void](Commit-ExactPaths -RelativePaths $versionPaths -CommitMessage "chore(release): v$nextVersion")
    Assert-PublicGitHistory
    Invoke-NativeCommand -FilePath 'git' -Arguments @('push', 'origin', 'HEAD')
    New-PublicAnnotatedTag -TagName $releasePlan.TagName
    Invoke-NativeCommand -FilePath 'git' -Arguments @('push', 'origin', $releasePlan.TagName)
    $commitSha = (& git rev-parse 'HEAD').Trim()
    $workflowRun = Wait-ForReleaseWorkflow -CommitSha $commitSha
    $releaseUrl = Verify-ReleaseAssets -RootPath $projectRoot -TagName $releasePlan.TagName -Version $nextVersion

    Write-Output "[OK] Release workflow: $($workflowRun.url)"
    Write-Output "[OK] GitHub Release: $releaseUrl"
}
finally {
    Pop-Location
}

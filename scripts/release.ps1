[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$Publish,
    [string]$Repository = 'secondshift-dv/neuterradise'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$DistRoot = Join-Path $RepositoryRoot 'dist'
$BuildScript = Join-Path $PSScriptRoot 'build.ps1'

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RemoteTagCommit {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $direct = @(git ls-remote --refs origin "refs/tags/$Tag")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect remote tag '$Tag'."
    }
    if ($direct.Count -eq 0) {
        return $null
    }

    $peeled = @(git ls-remote origin "refs/tags/$Tag^{}")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve remote tag '$Tag'."
    }

    $line = if ($peeled.Count -gt 0) { $peeled[0] } else { $direct[0] }
    return ($line -split '\s+')[0].Trim()
}

function Invoke-GhChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $RepositoryRoot
try {
    foreach ($command in @('git', 'gh')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "$command is required to create or publish a GitHub Release."
        }
    }

    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
        throw 'GitHub Release creation requires a clean working tree.'
    }

    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw 'Could not resolve source HEAD.'
    }

    git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not refresh origin/main before release creation.'
    }
    $originMain = (git rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $originMain) {
        throw "Release source must exactly match current origin/main. HEAD=$head origin/main=$originMain"
    }

    [xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
    $identity = $buildProps.Project.PropertyGroup |
        Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } |
        Select-Object -First 1
    if ($null -eq $identity) {
        throw 'Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier.'
    }

    $productVersion = [string]$identity.ProductVersion
    $runtimeIdentifier = [string]$identity.ProductRuntimeIdentifier
    $tag = "v$productVersion"
    $zipName = "NeuTerradise-v$productVersion-$runtimeIdentifier.zip"

    if ($env:GITHUB_REF_TYPE -eq 'tag' -and
        -not [string]::Equals($env:GITHUB_REF_NAME, $tag, [StringComparison]::Ordinal)) {
        throw "Git tag '$($env:GITHUB_REF_NAME)' does not match product version tag '$tag'."
    }

    if (-not $NoBuild) {
        & $BuildScript
        if (-not $?) {
            throw 'Canonical build failed before release creation.'
        }
    }

    $zipPath = Join-Path $DistRoot $zipName
    $manifestPath = Join-Path $DistRoot 'update.json'
    $provenancePath = Join-Path $DistRoot 'build-provenance.json'
    foreach ($required in @($zipPath, $manifestPath, $provenancePath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Verified release input is missing: $required"
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.productId -ne 'neuterradise' -or
        $manifest.productVersion -ne $productVersion -or
        $manifest.runtimeIdentifier -ne $runtimeIdentifier) {
        throw 'update.json identity does not match Directory.Build.props.'
    }

    $zipInfo = Get-Item -LiteralPath $zipPath
    $zipHash = Get-Sha256Lower -Path $zipPath
    $manifestHash = Get-Sha256Lower -Path $manifestPath
    if ([long]$manifest.payloadByteLength -ne $zipInfo.Length -or
        -not [string]::Equals([string]$manifest.payloadSha256, $zipHash, [StringComparison]::Ordinal)) {
        throw 'update.json does not match the final release ZIP.'
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.sourceHead -ne $head -or
        $provenance.productVersion -ne $productVersion -or
        $provenance.runtimeIdentifier -ne $runtimeIdentifier -or
        $provenance.zipFileName -ne $zipName -or
        [long]$provenance.zipByteLength -ne $zipInfo.Length -or
        -not [string]::Equals([string]$provenance.zipSha256, $zipHash, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$provenance.updateManifestSha256, $manifestHash, [StringComparison]::Ordinal)) {
        throw 'dist build provenance does not match the current source HEAD and release payload.'
    }

    gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated.'
    }

    $remoteTagCommit = Get-RemoteTagCommit -Tag $tag
    if ($null -ne $remoteTagCommit -and
        -not [string]::Equals($remoteTagCommit, $head, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing remote tag '$tag' points to $remoteTagCommit, not current HEAD $head."
    }

    $releaseViewOutput = @(
        & gh release view $tag --repo $Repository --json isDraft,targetCommitish,tagName 2>&1
    )
    $releaseExists = $LASTEXITCODE -eq 0

    if ($releaseExists) {
        $releaseInfo = ($releaseViewOutput -join [Environment]::NewLine) | ConvertFrom-Json
        if (-not $releaseInfo.isDraft) {
            throw "GitHub Release '$tag' is already published and is immutable through this script."
        }

        Invoke-GhChecked -Arguments @(
            'release', 'edit', $tag,
            '--repo', $Repository,
            '--target', $head,
            '--draft'
        )
        Invoke-GhChecked -Arguments @(
            'release', 'upload', $tag,
            '--repo', $Repository,
            '--clobber',
            $zipPath,
            $manifestPath
        )
    }
    else {
        $releaseError = $releaseViewOutput -join [Environment]::NewLine
        if (-not [string]::IsNullOrWhiteSpace($releaseError) -and
            $releaseError -notmatch '(?i)(release not found|not found|HTTP 404)') {
            throw "Could not determine whether release '$tag' exists: $releaseError"
        }

        Invoke-GhChecked -Arguments @(
            'release', 'create', $tag,
            '--repo', $Repository,
            '--target', $head,
            '--title', "Neu Terradise $tag",
            '--generate-notes',
            '--draft',
            $zipPath,
            $manifestPath
        )
    }

    if ($Publish) {
        # Publishing is deliberately a second explicit action. The draft is refreshed from the
        # verified current dist first, then promoted without changing payload identity.
        Invoke-GhChecked -Arguments @(
            'release', 'edit', $tag,
            '--repo', $Repository,
            '--draft=false'
        )

        $publishedTagCommit = Get-RemoteTagCommit -Tag $tag
        if ($null -eq $publishedTagCommit -or
            -not [string]::Equals($publishedTagCommit, $head, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published tag '$tag' does not resolve to current HEAD $head."
        }

        Write-Host 'STATUS=RELEASED'
    }
    else {
        Write-Host 'STATUS=DRAFT_READY'
    }

    Write-Host "TAG=$tag"
    Write-Host "SOURCE_HEAD=$head"
    Write-Host "ZIP=$zipPath"
    Write-Host "UPDATE_MANIFEST=$manifestPath"
    Write-Host "BUILD_PROVENANCE=$provenancePath"
}
finally {
    Pop-Location
}

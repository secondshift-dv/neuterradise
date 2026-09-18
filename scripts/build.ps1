[CmdletBinding()]
param(
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'NeuTerradise.sln'
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$PackageScript = Join-Path $PSScriptRoot 'package-win-x64.ps1'
$DistRoot = Join-Path $RepositoryRoot 'dist'
$StageRoot = Join-Path ([IO.Path]::GetTempPath()) ('neuterradise-package-' + [Guid]::NewGuid().ToString('N'))
$OfflineSource = $null
$PreviousOfflineEnvironment = @{}

function Invoke-DotNetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

Push-Location $RepositoryRoot
try {
    if (-not (Test-Path -LiteralPath $SolutionPath -PathType Leaf)) { throw "Solution was not found: $SolutionPath" }
    if (-not (Test-Path -LiteralPath $PackageScript -PathType Leaf)) { throw "Packaging authority was not found: $PackageScript" }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to establish source identity.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'A local .NET 10 SDK is required. Automatic SDK installation is not part of the build script.' }

    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty.Count -gt 0) { throw 'Working tree is not clean. Commit, stash, or revert local changes before producing canonical output.' }

    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) { throw 'Could not resolve source HEAD.' }

    if ($Offline) {
        foreach ($name in @(
            'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
            'DOTNET_CLI_TELEMETRY_OPTOUT',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
            'DOTNET_SDK_VULNERABILITY_CHECK_DISABLE',
            'NUGET_CERT_REVOCATION_MODE')) {
            $PreviousOfflineEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }

        $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_SDK_VULNERABILITY_CHECK_DISABLE = '1'
        $env:NUGET_CERT_REVOCATION_MODE = 'offline'
    }

    $sdks = @(dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^10\.' })) {
        throw 'A .NET 10 SDK is required. Automatic SDK installation is not part of the build script.'
    }

    [xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
    $identity = $buildProps.Project.PropertyGroup | Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } | Select-Object -First 1
    if ($null -eq $identity) { throw 'Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier.' }

    $productVersion = [string]$identity.ProductVersion
    $runtimeIdentifier = [string]$identity.ProductRuntimeIdentifier
    $zipName = "NeuTerradise-v$productVersion-$runtimeIdentifier.zip"

    Write-Host "SOURCE_HEAD=$head"
    Write-Host "PRODUCT_VERSION=$productVersion"
    Write-Host "RID=$runtimeIdentifier"
    Write-Host "NETWORK_MODE=$(if ($Offline) { 'OFFLINE' } else { 'CACHE_FIRST_MISSING_ONLY' })"

    $restoreArgs = @(
        'restore',
        $SolutionPath,
        '--runtime', $runtimeIdentifier,
        '--disable-parallel'
    )
    if ($Offline) {
        $OfflineSource = Join-Path ([IO.Path]::GetTempPath()) ('neuterradise-offline-nuget-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $OfflineSource -Force | Out-Null

        # A deliberately empty source prevents NuGet from contacting configured feeds. The global
        # packages folder remains available, so cached exact packages are reused and cache misses fail.
        $restoreArgs += @(
            '--source', $OfflineSource,
            '-p:NuGetAudit=false'
        )
    }
    Invoke-DotNetChecked -Arguments $restoreArgs

    & $PackageScript -RepositoryRoot $RepositoryRoot -OutputDirectory $StageRoot -Offline:$Offline
    if (-not $?) { throw 'Packaging authority failed.' }

    $verifiedBuild = Join-Path $StageRoot 'install-root'
    $verifiedZip = Join-Path $StageRoot $zipName
    $verifiedUpdateManifest = Join-Path $StageRoot 'update.json'
    foreach ($required in @(
        $verifiedBuild,
        $verifiedZip,
        $verifiedUpdateManifest,
        (Join-Path $verifiedBuild 'NeuTerradise.exe'),
        (Join-Path $verifiedBuild 'NeuTerradise.Updater.exe'),
        (Join-Path $verifiedBuild 'workers\NeuTerradise.Profiling.Worker.exe'),
        (Join-Path $verifiedBuild 'release-manifest.json'),
        (Join-Path $verifiedBuild 'deployment\artifacts.json'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Verified build output is incomplete: $required" }
    }

    if (Test-Path -LiteralPath $DistRoot) { Remove-Item -LiteralPath $DistRoot -Recurse -Force }
    $runtimeOutput = Join-Path $DistRoot 'NeuTerradise'
    New-Item -ItemType Directory -Path $runtimeOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $verifiedBuild '*') -Destination $runtimeOutput -Recurse -Force
    Copy-Item -LiteralPath $verifiedZip -Destination (Join-Path $DistRoot $zipName) -Force
    Copy-Item -LiteralPath $verifiedUpdateManifest -Destination (Join-Path $DistRoot 'update.json') -Force

    $distZip = Join-Path $DistRoot $zipName
    $distManifest = Join-Path $DistRoot 'update.json'
    $provenancePath = Join-Path $DistRoot 'build-provenance.json'
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceHead = $head
        productVersion = $productVersion
        runtimeIdentifier = $runtimeIdentifier
        zipFileName = $zipName
        zipByteLength = [long](Get-Item -LiteralPath $distZip).Length
        zipSha256 = (Get-FileHash -LiteralPath $distZip -Algorithm SHA256).Hash.ToLowerInvariant()
        updateManifestSha256 = (Get-FileHash -LiteralPath $distManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $provenanceJson = $provenance | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        $provenancePath,
        $provenanceJson + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Write-Host 'STATUS=DONE'
    Write-Host "APP=$(Join-Path $runtimeOutput 'NeuTerradise.exe')"
    Write-Host "ZIP=$(Join-Path $DistRoot $zipName)"
    Write-Host "UPDATE_MANIFEST=$(Join-Path $DistRoot 'update.json')"
    Write-Host "BUILD_PROVENANCE=$(Join-Path $DistRoot 'build-provenance.json')"
    Write-Host 'VAULT_TOUCHED=NO'
}
finally {
    Pop-Location
    foreach ($entry in $PreviousOfflineEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if ($null -ne $OfflineSource -and (Test-Path -LiteralPath $OfflineSource)) { Remove-Item -LiteralPath $OfflineSource -Recurse -Force }
    if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
}

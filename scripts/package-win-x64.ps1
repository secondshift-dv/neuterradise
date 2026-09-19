[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = $(
        if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
            Join-Path ([IO.Path]::GetTempPath()) 'neuterradise-package'
        }
        else {
            Join-Path $env:RUNNER_TEMP 'neuterradise-package'
        }
    ),

    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ProductId = 'neuterradise'
$BuildPropsPath = Join-Path $PSScriptRoot '..\Directory.Build.props'
if (-not (Test-Path -LiteralPath $BuildPropsPath)) { throw "Directory.Build.props is required for product release identity." }
$BuildProps = [xml](Get-Content -LiteralPath $BuildPropsPath -Raw)
$BuildPropertyGroup = $BuildProps.Project.PropertyGroup | Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } | Select-Object -First 1
if ($null -eq $BuildPropertyGroup -or [string]::IsNullOrWhiteSpace($BuildPropertyGroup.ProductVersion) -or [string]::IsNullOrWhiteSpace($BuildPropertyGroup.ProductRuntimeIdentifier)) { throw "Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier." }
$ProductVersion = [string]$BuildPropertyGroup.ProductVersion
$RuntimeIdentifier = [string]$BuildPropertyGroup.ProductRuntimeIdentifier
$ZipName = "NeuTerradise-v$ProductVersion-$RuntimeIdentifier.zip"

$ReleaseContractSourcePath = Join-Path $RepositoryRoot 'release-contract.json'
if (-not (Test-Path -LiteralPath $ReleaseContractSourcePath -PathType Leaf)) { throw "release-contract.json is required." }
$ReleaseContract = Get-Content -LiteralPath $ReleaseContractSourcePath -Raw | ConvertFrom-Json
if ($ReleaseContract.schemaVersion -ne 1 -or
    $ReleaseContract.productId -ne $ProductId -or
    $ReleaseContract.runtimeIdentifier -ne $RuntimeIdentifier) {
    throw "release-contract.json identity is invalid."
}
$ModelsRelativeRoot = ([string]$ReleaseContract.modelsRelativeRoot).Replace('\', '/').Trim('/')
$RequiredReleaseMembers = @($ReleaseContract.requiredMembers | ForEach-Object { ([string]$_).Replace('\', '/') })
$RequiredUniqueFileNames = @($ReleaseContract.requiredUniqueFileNames | ForEach-Object { [string]$_ })
$FfmpegMirrorUrl = [string]$ReleaseContract.ffmpegMirrorUrl

$AppExe = 'NeuTerradise.exe'
$WorkerExe = 'NeuTerradise.Profiling.Worker.exe'
$UpdaterExe = 'NeuTerradise.Updater.exe'
$AppRelativePath = $AppExe
$WorkerRelativePath = "workers/$WorkerExe"
$UpdaterRelativePath = $UpdaterExe

$OpenCvZooCommit = '47534e27c9851bb1128ccc0102f1145e27f23f98'
$YuNetFileName = 'face_detection_yunet_2023mar.onnx'
$YuNetSha256 = '8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4'
$YuNetUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$OpenCvZooCommit/models/face_detection_yunet/$YuNetFileName"
$YuNetLicenseUrl = "https://raw.githubusercontent.com/opencv/opencv_zoo/$OpenCvZooCommit/models/face_detection_yunet/LICENSE"

$SFaceFileName = 'face_recognition_sface_2021dec.onnx'
$SFaceSha256 = '0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79'
$SFaceUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$OpenCvZooCommit/models/face_recognition_sface/$SFaceFileName"
$SFaceLicenseUrl = "https://raw.githubusercontent.com/opencv/opencv_zoo/$OpenCvZooCommit/models/face_recognition_sface/LICENSE"

$FfmpegReleaseTag = 'autobuild-2026-09-08-23-15'
$FfmpegArchiveName = 'ffmpeg-n8.1.2-51-g7ba069f4f1-win64-lgpl-8.1.zip'
$FfmpegArchiveSha256 = '9bb4c17bf1e271e7944a61d900716e61c6f6b4ef556544f3907b131cbc0bfd41'
$FfmpegArchiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegReleaseTag/$FfmpegArchiveName"
$FfmpegCommit = '7ba069f4f11d126f52a740156dbab6476a8a865a'
$FfmpegLicenseUrl = "https://raw.githubusercontent.com/FFmpeg/FFmpeg/$FfmpegCommit/COPYING.LGPLv2.1"
$BtbnCommit = 'b237db1fe9ed6ceb374ec6711c9c9a06988d6587'
$BtbnLicenseUrl = "https://raw.githubusercontent.com/BtbN/FFmpeg-Builds/$BtbnCommit/LICENSE"

$OpenCvSharpPackage = 'OpenCvSharp5.runtime.win'
$OpenCvSharpVersion = '5.0.0.20260905'
$OpenCvSharpCommit = '7d1ae7eda1316c6b8f24dd61335702867403cee9'
$OpenCvSharpLicenseUrl = "https://raw.githubusercontent.com/shimat/opencvsharp/$OpenCvSharpCommit/LICENSE"

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = Get-Sha256Lower -Path $Path
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "$Label SHA-256 mismatch. Expected $Expected but got $actual."
    }
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$AllowEqual
    )

    $candidateFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Candidate))
    $rootFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if ([string]::Equals($candidateFull, $rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $AllowEqual.IsPresent
    }

    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-DisposableOutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $candidate = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $root = [IO.Path]::GetPathRoot($candidate)
    if ([string]::IsNullOrWhiteSpace($root) -or
        [string]::Equals($candidate, [IO.Path]::TrimEndingDirectorySeparator($root), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Packaging OutputDirectory cannot be a filesystem root.'
    }

    $repoPackagingRoot = Join-Path $RepoRoot 'dist\.packaging'
    $safe = Test-PathWithin -Candidate $candidate -Root $repoPackagingRoot -AllowEqual

    if (-not $safe -and -not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $safe = Test-PathWithin -Candidate $candidate -Root $env:RUNNER_TEMP
    }

    if (-not $safe) {
        $systemTemp = [IO.Path]::GetTempPath()
        $safe = Test-PathWithin -Candidate $candidate -Root $systemTemp
    }

    if (-not $safe) {
        throw "Packaging OutputDirectory must be a dedicated disposable directory under RUNNER_TEMP, the system temp directory, or '$repoPackagingRoot'. Refusing recursive cleanup of '$candidate'."
    }
}

function Invoke-Download {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile
    )

    $parent = Split-Path -Parent $OutFile
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    # Persistent cache survives repeated local builds. GitHub Actions may override the root
    # through NEUTERRADISE_BUILD_CACHE so the exact same packaging code can use actions/cache.
    if (-not [string]::IsNullOrWhiteSpace($env:NEUTERRADISE_BUILD_CACHE)) {
        $cacheRoot = [IO.Path]::GetFullPath($env:NEUTERRADISE_BUILD_CACHE)
    }
    else {
        $localAppData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)

        if ([string]::IsNullOrWhiteSpace($localAppData)) {
            $localAppData = $env:USERPROFILE
        }

        $cacheRoot = Join-Path $localAppData 'NeuTerradise\BuildCache\downloads'
    }

    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

    $cacheLabel = if ([string]::Equals($Uri, $YuNetUrl, [StringComparison]::Ordinal)) {
        'YuNet-model'
    }
    elseif ([string]::Equals($Uri, $SFaceUrl, [StringComparison]::Ordinal)) {
        'SFace-model'
    }
    elseif ([string]::Equals($Uri, $FfmpegArchiveUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-archive'
    }
    elseif ([string]::Equals($Uri, $FfmpegMirrorUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-project-mirror'
    }
    elseif ([string]::Equals($Uri, $YuNetLicenseUrl, [StringComparison]::Ordinal)) {
        'YuNet-license'
    }
    elseif ([string]::Equals($Uri, $SFaceLicenseUrl, [StringComparison]::Ordinal)) {
        'SFace-license'
    }
    elseif ([string]::Equals($Uri, $FfmpegLicenseUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-license'
    }
    elseif ([string]::Equals($Uri, $BtbnLicenseUrl, [StringComparison]::Ordinal)) {
        'BtbN-license'
    }
    elseif ([string]::Equals($Uri, $OpenCvSharpLicenseUrl, [StringComparison]::Ordinal)) {
        'OpenCvSharp-license'
    }
    else {
        'external-file'
    }

    $expectedSha256 = if ([string]::Equals(
        $Uri,
        $YuNetUrl,
        [StringComparison]::Ordinal)) {

        $YuNetSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $SFaceUrl,
        [StringComparison]::Ordinal)) {

        $SFaceSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $FfmpegArchiveUrl,
        [StringComparison]::Ordinal) -or [string]::Equals(
        $Uri,
        $FfmpegMirrorUrl,
        [StringComparison]::Ordinal)) {

        $FfmpegArchiveSha256
    }
    else {
        $null
    }

    $uriObject = [Uri]$Uri
    $leafName = [IO.Path]::GetFileName($uriObject.AbsolutePath)

    if ([string]::IsNullOrWhiteSpace($leafName)) {
        $leafName = 'download.bin'
    }

    $uriBytes = [Text.Encoding]::UTF8.GetBytes($Uri)
    $uriHashBytes = [Security.Cryptography.SHA256]::HashData($uriBytes)
    $uriHash = [Convert]::ToHexString($uriHashBytes).ToLowerInvariant()

    $cacheFile = Join-Path $cacheRoot "$uriHash-$leafName"
    $cacheValid = Test-Path -LiteralPath $cacheFile -PathType Leaf

    if ($cacheValid) {
        $cachedItem = Get-Item -LiteralPath $cacheFile

        if ($cachedItem.Length -le 0) {
            Write-Host "CACHE_INVALID=$cacheLabel"
            Remove-Item -LiteralPath $cacheFile -Force
            $cacheValid = $false
        }
    }

    if ($cacheValid -and -not [string]::IsNullOrWhiteSpace($expectedSha256)) {
        $cachedSha256 = Get-Sha256Lower -Path $cacheFile

        if (-not [string]::Equals(
            $cachedSha256,
            $expectedSha256,
            [StringComparison]::Ordinal)) {

            Write-Host "CACHE_INVALID=$cacheLabel"
            Remove-Item -LiteralPath $cacheFile -Force
            $cacheValid = $false
        }
    }

    if ($cacheValid) {
        Write-Host "CACHE_HIT=$cacheLabel"
        Copy-Item -LiteralPath $cacheFile -Destination $OutFile -Force
        return
    }

    Write-Host "CACHE_MISS=$cacheLabel"

    if ($Offline) {
        throw "Offline packaging is missing required cached artifact '$cacheLabel'. URI=$Uri"
    }

    $temporaryCacheFile =
        "$cacheFile.partial.$PID.$([Guid]::NewGuid().ToString('N'))"

    try {
        Invoke-WebRequest `
            -Uri $Uri `
            -OutFile $temporaryCacheFile `
            -MaximumRedirection 10 `
            -Headers @{ 'User-Agent' = "NeuTerradise-build/$ProductVersion" }

        if (-not (Test-Path -LiteralPath $temporaryCacheFile -PathType Leaf)) {
            throw "Download did not produce a file for $cacheLabel."
        }

        $downloadedItem = Get-Item -LiteralPath $temporaryCacheFile
        if ($downloadedItem.Length -le 0) {
            throw "Downloaded file is empty for $cacheLabel."
        }

        if (-not [string]::IsNullOrWhiteSpace($expectedSha256)) {
            Assert-Sha256 `
                -Path $temporaryCacheFile `
                -Expected $expectedSha256 `
                -Label $cacheLabel
        }

        Move-Item `
            -LiteralPath $temporaryCacheFile `
            -Destination $cacheFile `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryCacheFile) {
            Remove-Item -LiteralPath $temporaryCacheFile -Force
        }
    }

    Copy-Item -LiteralPath $cacheFile -Destination $OutFile -Force
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

function Write-JsonUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Normalize-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $Path.Replace('\', '/')
}

function Get-ReleaseRole {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([string]::Equals($RelativePath, $AppRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'app' }
    if ([string]::Equals($RelativePath, $WorkerRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'profiling-worker' }
    if ([string]::Equals($RelativePath, $UpdaterRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'updater' }
    if ([string]::Equals($RelativePath, 'deployment/artifacts.json', [StringComparison]::OrdinalIgnoreCase)) { return 'deployment-manifest' }
    if ($RelativePath.StartsWith('tools/', [StringComparison]::OrdinalIgnoreCase)) { return 'tool' }
    if ($RelativePath.StartsWith('workers/models/', [StringComparison]::OrdinalIgnoreCase)) { return 'model' }
    if ($RelativePath.StartsWith('workers/', [StringComparison]::OrdinalIgnoreCase)) { return 'profiling-runtime' }
    if ($RelativePath.StartsWith('LICENSES/', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($RelativePath, 'THIRD-PARTY-NOTICES.txt', [StringComparison]::OrdinalIgnoreCase)) { return 'notice' }
    return 'runtime'
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required deployed file is missing: $RelativePath"
    }
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-DisposableOutputDirectory -Path $OutputDirectory -RepoRoot $RepositoryRoot
$PublishRoot = Join-Path $OutputDirectory 'publish'
$InstallRoot = Join-Path $OutputDirectory 'install-root'
$DownloadRoot = Join-Path $OutputDirectory 'downloads'
$ExtractRoot = Join-Path $OutputDirectory 'extracted'
$VerificationRoot = Join-Path $OutputDirectory 'verification'
$ZipPath = Join-Path $OutputDirectory $ZipName

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishRoot, $InstallRoot, $DownloadRoot, $ExtractRoot -Force | Out-Null

Push-Location $RepositoryRoot
try {
    $commonPublish = @(
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--disable-build-servers',
        '-m:1',
        '-p:BuildInParallel=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:PublishTrimmed=false',
        '-p:DisableUnoResizetizer=true',
        '-p:TargetsTriggeredByCompilation='
    )

    $appOutput = Join-Path $PublishRoot 'app'
    New-Item -ItemType Directory -Path $appOutput -Force | Out-Null
    # The product UI is Uno Platform (Skia Desktop, Win32 host): publish the net10.0-desktop head as a
    # plain self-contained folder. Never MSIX; the updater replaces these files and never owns the Vault.
    $appArgs = @(
        'publish',
        'src/Neuterradise.App/Neuterradise.App.csproj',
        '--framework', 'net10.0-desktop'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=false',
        '-p:WindowsPackageType=None',
        '--output', $appOutput
    )
    Invoke-DotNet -Arguments $appArgs
    Copy-Item -Path (Join-Path $appOutput '*') -Destination $InstallRoot -Recurse -Force

    foreach ($forbidden in @(
        '*.msix',
        '*.appx',
        '*.msixbundle',
        'Uno.WinUI.Runtime.Skia.Wpf*',
        'PresentationFramework.dll',
        'PresentationCore.dll',
        'System.Xaml.dll',
        'System.Printing.dll',
        'ReachFramework.dll')) {
        $hits = @(Get-ChildItem -LiteralPath $InstallRoot -File -Recurse -Filter $forbidden)
        if ($hits.Count -gt 0) {
            throw "Uno desktop publish must not contain $forbidden (found $($hits[0].FullName))."
        }
    }

    foreach ($manifest in @(Get-ChildItem -LiteralPath $InstallRoot -File -Recurse |
        Where-Object { $_.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase) -or
                       $_.Name.EndsWith('.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase) })) {
        $forbiddenReference = Select-String -LiteralPath $manifest.FullName -SimpleMatch -Quiet -Pattern @(
            'Uno.WinUI.Runtime.Skia.Wpf',
            'PresentationFramework',
            'PresentationCore')
        if ($forbiddenReference) {
            throw "Uno desktop dependency manifest contains a WPF host/framework reference: $($manifest.FullName)."
        }
    }

    $builtInPack = Join-Path $InstallRoot 'Assets/Presentation/BuiltIn/assets/home-header.png'
    if (-not (Test-Path -LiteralPath $builtInPack -PathType Leaf)) {
        throw 'Built-in presentation pack art was not published with the application.'
    }

    $workerOutput = Join-Path $PublishRoot 'worker'
    $workerInstallRoot = Join-Path $InstallRoot 'workers'
    New-Item -ItemType Directory -Path $workerOutput, $workerInstallRoot -Force | Out-Null
    $workerArgs = @(
        'publish',
        'src/Neuterradise.Profiling.Worker/Neuterradise.Profiling.Worker.csproj'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=false',
        '--output', $workerOutput
    )
    Invoke-DotNet -Arguments $workerArgs
    Copy-Item -Path (Join-Path $workerOutput '*') -Destination $workerInstallRoot -Recurse -Force

    $updaterOutput = Join-Path $PublishRoot 'updater'
    New-Item -ItemType Directory -Path $updaterOutput -Force | Out-Null
    $updaterArgs = @(
        'publish',
        'src/Neuterradise.Updater/Neuterradise.Updater.csproj'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '--output', $updaterOutput
    )
    Invoke-DotNet -Arguments $updaterArgs

    $publishedUpdater = Join-Path $updaterOutput $UpdaterExe
    if (-not (Test-Path -LiteralPath $publishedUpdater -PathType Leaf)) {
        throw "Single-file updater publish did not produce $UpdaterExe."
    }
    Copy-Item -LiteralPath $publishedUpdater -Destination (Join-Path $InstallRoot $UpdaterExe) -Force
}
finally {
    Pop-Location
}

$WorkerInstallRoot = Join-Path $InstallRoot 'workers'
$YuNetRelativePath = "$ModelsRelativeRoot/yunet/$YuNetFileName"
$SFaceRelativePath = "$ModelsRelativeRoot/sface/$SFaceFileName"
$YuNetPath = Join-Path $InstallRoot $YuNetRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
$SFacePath = Join-Path $InstallRoot $SFaceRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)

Invoke-Download -Uri $YuNetUrl -OutFile $YuNetPath
Assert-Sha256 -Path $YuNetPath -Expected $YuNetSha256 -Label 'YuNet model'
Invoke-Download -Uri $SFaceUrl -OutFile $SFacePath
Assert-Sha256 -Path $SFacePath -Expected $SFaceSha256 -Label 'SFace model'

$LicensesRoot = Join-Path $InstallRoot 'LICENSES'
New-Item -ItemType Directory -Path $LicensesRoot -Force | Out-Null
Invoke-Download -Uri $YuNetLicenseUrl -OutFile (Join-Path $LicensesRoot 'YuNet-MIT.txt')
Invoke-Download -Uri $SFaceLicenseUrl -OutFile (Join-Path $LicensesRoot 'SFace-Apache-2.0.txt')
Invoke-Download -Uri $FfmpegLicenseUrl -OutFile (Join-Path $LicensesRoot 'FFmpeg-LGPL-2.1.txt')
Invoke-Download -Uri $BtbnLicenseUrl -OutFile (Join-Path $LicensesRoot 'BtbN-FFmpeg-Builds-LICENSE.txt')
Invoke-Download -Uri $OpenCvSharpLicenseUrl -OutFile (Join-Path $LicensesRoot 'OpenCvSharp-Apache-2.0.txt')

$FfmpegArchivePath = Join-Path $DownloadRoot $FfmpegArchiveName
$ffmpegDownloadedFromMirror = $false
try {
    Invoke-Download -Uri $FfmpegMirrorUrl -OutFile $FfmpegArchivePath
    Assert-Sha256 -Path $FfmpegArchivePath -Expected $FfmpegArchiveSha256 -Label 'mirrored BtbN FFmpeg archive'
    $ffmpegDownloadedFromMirror = $true
}
catch {
    if ($Offline) { throw }
    Write-Host 'FFMPEG_MIRROR_UNAVAILABLE=YES'
    Invoke-Download -Uri $FfmpegArchiveUrl -OutFile $FfmpegArchivePath
}
Assert-Sha256 -Path $FfmpegArchivePath -Expected $FfmpegArchiveSha256 -Label 'BtbN FFmpeg archive'
Write-Host "FFMPEG_SOURCE=$(if ($ffmpegDownloadedFromMirror) { 'PROJECT_MIRROR' } else { 'UPSTREAM_FALLBACK' })"

$FfmpegExtractRoot = Join-Path $ExtractRoot 'ffmpeg'
Expand-Archive -LiteralPath $FfmpegArchivePath -DestinationPath $FfmpegExtractRoot -Force
$FfmpegCandidates = @(Get-ChildItem -LiteralPath $FfmpegExtractRoot -File -Recurse -Filter 'ffmpeg.exe')
$FfprobeCandidates = @(Get-ChildItem -LiteralPath $FfmpegExtractRoot -File -Recurse -Filter 'ffprobe.exe')
if ($FfmpegCandidates.Count -ne 1 -or $FfprobeCandidates.Count -ne 1) {
    throw "Expected exactly one ffmpeg.exe and one ffprobe.exe in pinned archive; got $($FfmpegCandidates.Count) and $($FfprobeCandidates.Count)."
}

$ToolsRoot = Join-Path $InstallRoot 'tools'
New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
$FfmpegPath = Join-Path $ToolsRoot 'ffmpeg.exe'
$FfprobePath = Join-Path $ToolsRoot 'ffprobe.exe'
Copy-Item -LiteralPath $FfmpegCandidates[0].FullName -Destination $FfmpegPath
Copy-Item -LiteralPath $FfprobeCandidates[0].FullName -Destination $FfprobePath

$ffmpegVersionLine = (& $FfmpegPath -hide_banner -version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $ffmpegVersionLine -notmatch '7ba069f4f1') {
    throw "Pinned ffmpeg executable did not report expected source revision 7ba069f4f1. Reported: $ffmpegVersionLine"
}
$ffprobeVersionLine = (& $FfprobePath -hide_banner -version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $ffprobeVersionLine -notmatch '7ba069f4f1') {
    throw "Pinned ffprobe executable did not report expected source revision 7ba069f4f1. Reported: $ffprobeVersionLine"
}

$FfmpegSourceEntry = Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($FfmpegExtractRoot, $FfmpegCandidates[0].FullName))
$FfprobeSourceEntry = Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($FfmpegExtractRoot, $FfprobeCandidates[0].FullName))

$NuGetPackagesRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}
else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}
$OpenCvPackageRoot = Join-Path $NuGetPackagesRoot "$($OpenCvSharpPackage.ToLowerInvariant())/$OpenCvSharpVersion"
if (-not (Test-Path -LiteralPath $OpenCvPackageRoot -PathType Container)) {
    throw "Restored NuGet package was not found: $OpenCvPackageRoot"
}
$OpenCvPackageNatives = @(Get-ChildItem -LiteralPath $OpenCvPackageRoot -File -Recurse -Filter 'OpenCvSharpExtern.dll')
if ($OpenCvPackageNatives.Count -ne 1) {
    throw "Expected exactly one OpenCvSharpExtern.dll in $OpenCvSharpPackage $OpenCvSharpVersion; got $($OpenCvPackageNatives.Count)."
}
$PublishedOpenCvNatives = @(Get-ChildItem -LiteralPath $WorkerInstallRoot -File -Recurse -Filter 'OpenCvSharpExtern.dll')
if ($PublishedOpenCvNatives.Count -ne 1) {
    throw "Expected exactly one deployed OpenCvSharpExtern.dll under workers/; got $($PublishedOpenCvNatives.Count)."
}
$OpenCvPackageNativeSha = Get-Sha256Lower -Path $OpenCvPackageNatives[0].FullName
$OpenCvPublishedNativeSha = Get-Sha256Lower -Path $PublishedOpenCvNatives[0].FullName
if (-not [string]::Equals($OpenCvPackageNativeSha, $OpenCvPublishedNativeSha, [StringComparison]::Ordinal)) {
    throw 'Published OpenCvSharpExtern.dll does not match the restored pinned NuGet package byte-for-byte.'
}
$OpenCvSourceEntry = Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($OpenCvPackageRoot, $OpenCvPackageNatives[0].FullName))
$OpenCvDeployedRelativePath = Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($InstallRoot, $PublishedOpenCvNatives[0].FullName))

$ThirdPartyNoticePath = Join-Path $InstallRoot 'THIRD-PARTY-NOTICES.txt'
$notice = @"
Neu Terradise v$ProductVersion - Third-Party Runtime Notices

This local runtime-test package contains third-party runtime artifacts pinned by exact source/version and verified before packaging.

1. YuNet face detector model
   Source: OpenCV Zoo commit $OpenCvZooCommit
   Source file: models/face_detection_yunet/$YuNetFileName
   Deployed file: $YuNetRelativePath
   SHA-256: $YuNetSha256
   License: MIT
   License copy: LICENSES/YuNet-MIT.txt

2. SFace face recognition model
   Source: OpenCV Zoo commit $OpenCvZooCommit
   Source file: models/face_recognition_sface/$SFaceFileName
   Deployed file: $SFaceRelativePath
   SHA-256: $SFaceSha256
   License: Apache-2.0
   License copy: LICENSES/SFace-Apache-2.0.txt

3. FFmpeg and ffprobe
   Build archive: BtbN FFmpeg-Builds tag $FfmpegReleaseTag, commit $BtbnCommit
   Archive: $FfmpegArchiveName
   Archive SHA-256: $FfmpegArchiveSha256
   FFmpeg source revision: $FfmpegCommit
   Build family: win64 LGPL static
   FFmpeg license copy: LICENSES/FFmpeg-LGPL-2.1.txt
   BtbN build-system license copy: LICENSES/BtbN-FFmpeg-Builds-LICENSE.txt

4. OpenCvSharp native runtime
   NuGet package: $OpenCvSharpPackage $OpenCvSharpVersion
   OpenCvSharp source tag commit: $OpenCvSharpCommit
   Deployed file: $OpenCvDeployedRelativePath
   License: Apache-2.0
   License copy: LICENSES/OpenCvSharp-Apache-2.0.txt

The deployment/artifacts.json manifest is the machine-readable authority for the runtime artifacts consumed by Neu Terradise. A missing file or SHA-256 mismatch is treated as unavailable/fail-closed; the application must not fall back to PATH, the source tree, or unapproved remote content.
"@
[IO.File]::WriteAllText($ThirdPartyNoticePath, $notice.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$DeploymentDirectory = Join-Path $InstallRoot 'deployment'
New-Item -ItemType Directory -Path $DeploymentDirectory -Force | Out-Null
$ReleaseContractInstallPath = Join-Path $DeploymentDirectory 'release-contract.json'
Copy-Item -LiteralPath $ReleaseContractSourcePath -Destination $ReleaseContractInstallPath -Force
$DeploymentManifestPath = Join-Path $DeploymentDirectory 'artifacts.json'

$deploymentManifest = [ordered]@{
    schemaVersion = 2
    policy = 'InstallRoot-only pinned runtime artifacts; SHA-256 verification is mandatory and mismatches fail closed. The profiling worker is isolated under workers/ and resolves its models from workers/models. No PATH, source-tree, or network fallback is permitted at runtime.'
    noticesRelativePath = 'THIRD-PARTY-NOTICES.txt'
    sources = @(
        [ordered]@{
            sourceId = 'opencv-zoo-yunet-2023mar'
            kind = 'file'
            url = $YuNetUrl
            sha256 = $YuNetSha256
            license = 'MIT'
            licenseUrl = "https://github.com/opencv/opencv_zoo/blob/$OpenCvZooCommit/models/face_detection_yunet/LICENSE"
            provenance = "OpenCV Zoo commit $OpenCvZooCommit, YuNet 2023mar model file."
        },
        [ordered]@{
            sourceId = 'opencv-zoo-sface-2021dec'
            kind = 'file'
            url = $SFaceUrl
            sha256 = $SFaceSha256
            license = 'Apache-2.0'
            licenseUrl = "https://github.com/opencv/opencv_zoo/blob/$OpenCvZooCommit/models/face_recognition_sface/LICENSE"
            provenance = "OpenCV Zoo commit $OpenCvZooCommit, SFace 2021dec model file."
        },
        [ordered]@{
            sourceId = 'btbn-ffmpeg-8.1.2-win64-lgpl'
            kind = 'archive'
            url = $FfmpegArchiveUrl
            sha256 = $FfmpegArchiveSha256
            version = 'n8.1.2-51-g7ba069f4f1'
            license = 'LGPL-2.1-or-later'
            licenseUrl = "https://github.com/FFmpeg/FFmpeg/blob/$FfmpegCommit/COPYING.LGPLv2.1"
            provenance = "BtbN FFmpeg-Builds tag $FfmpegReleaseTag (build commit $BtbnCommit), FFmpeg source commit $FfmpegCommit, win64 LGPL static archive."
        },
        [ordered]@{
            sourceId = 'opencvsharp5-runtime-win'
            kind = 'nuget'
            package = $OpenCvSharpPackage
            version = $OpenCvSharpVersion
            license = 'Apache-2.0'
            licenseUrl = "https://github.com/shimat/opencvsharp/blob/$OpenCvSharpCommit/LICENSE"
            provenance = "NuGet package $OpenCvSharpPackage $OpenCvSharpVersion; corresponding OpenCvSharp tag commit $OpenCvSharpCommit."
        }
    )
    artifacts = @(
        [ordered]@{
            logicalName = 'YuNet face detector'
            kind = 'model'
            modelId = 'yunet'
            version = '2023mar'
            sourceId = 'opencv-zoo-yunet-2023mar'
            sourceEntryPath = "models/face_detection_yunet/$YuNetFileName"
            deployedRelativePath = $YuNetRelativePath
            sha256 = Get-Sha256Lower -Path $YuNetPath
            license = 'MIT'
            provenance = "Exact bytes from OpenCV Zoo commit $OpenCvZooCommit."
            consumers = @('profiling-worker')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Face detection and face profiling are unavailable.'
            mismatchBehavior = 'Fail closed and report the YuNet model capability unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'SFace face recognizer'
            kind = 'model'
            modelId = 'sface'
            version = '2021dec'
            sourceId = 'opencv-zoo-sface-2021dec'
            sourceEntryPath = "models/face_recognition_sface/$SFaceFileName"
            deployedRelativePath = $SFaceRelativePath
            sha256 = Get-Sha256Lower -Path $SFacePath
            license = 'Apache-2.0'
            provenance = "Exact bytes from OpenCV Zoo commit $OpenCvZooCommit."
            consumers = @('profiling-worker')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Face recognition embeddings and related-profile ranking are unavailable.'
            mismatchBehavior = 'Fail closed and report the SFace model capability unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'FFmpeg executable'
            kind = 'tool'
            toolId = 'ffmpeg'
            version = 'n8.1.2-51-g7ba069f4f1'
            sourceId = 'btbn-ffmpeg-8.1.2-win64-lgpl'
            sourceEntryPath = $FfmpegSourceEntry
            deployedRelativePath = 'tools/ffmpeg.exe'
            sha256 = Get-Sha256Lower -Path $FfmpegPath
            license = 'LGPL-2.1-or-later'
            provenance = "Extracted from the pinned BtbN archive after archive SHA-256 verification; FFmpeg source commit $FfmpegCommit."
            consumers = @('app')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Video preview derivation is unavailable.'
            mismatchBehavior = 'Fail closed and do not execute an unverified ffmpeg binary.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'FFprobe executable'
            kind = 'tool'
            toolId = 'ffprobe'
            version = 'n8.1.2-51-g7ba069f4f1'
            sourceId = 'btbn-ffmpeg-8.1.2-win64-lgpl'
            sourceEntryPath = $FfprobeSourceEntry
            deployedRelativePath = 'tools/ffprobe.exe'
            sha256 = Get-Sha256Lower -Path $FfprobePath
            license = 'LGPL-2.1-or-later'
            provenance = "Extracted from the pinned BtbN archive after archive SHA-256 verification; FFmpeg source commit $FfmpegCommit."
            consumers = @('app')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Video technical metadata extraction is unavailable.'
            mismatchBehavior = 'Fail closed and do not execute an unverified ffprobe binary.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'OpenCvSharp native runtime'
            kind = 'native'
            version = $OpenCvSharpVersion
            sourceId = 'opencvsharp5-runtime-win'
            sourceEntryPath = $OpenCvSourceEntry
            deployedRelativePath = $OpenCvDeployedRelativePath
            sha256 = $OpenCvPublishedNativeSha
            license = 'Apache-2.0'
            provenance = "Published byte matches the restored $OpenCvSharpPackage $OpenCvSharpVersion package exactly."
            consumers = @('profiling-worker')
            distribution = 'build'
            featureImpactWhenMissing = 'YuNet/SFace inference cannot load.'
            mismatchBehavior = 'Fail closed; profiling worker native inference capability is unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        }
    )
    declinedArtifacts = @()
}
Write-JsonUtf8NoBom -Value $deploymentManifest -Path $DeploymentManifestPath -Depth 12

$parsedDeployment = Get-Content -LiteralPath $DeploymentManifestPath -Raw | ConvertFrom-Json
if ($parsedDeployment.schemaVersion -ne 2 -or $parsedDeployment.artifacts.Count -ne 5) {
    throw 'Generated deployment/artifacts.json failed structural validation.'
}
foreach ($artifact in $parsedDeployment.artifacts) {
    $artifactPath = Join-Path $InstallRoot ([string]$artifact.deployedRelativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Deployment manifest points to a missing file: $($artifact.deployedRelativePath)"
    }
    Assert-Sha256 -Path $artifactPath -Expected ([string]$artifact.sha256) -Label ([string]$artifact.logicalName)
}

foreach ($requiredRelativePath in $RequiredReleaseMembers) {
    Assert-RequiredFile -Root $InstallRoot -RelativePath $requiredRelativePath
}
foreach ($requiredFileName in $RequiredUniqueFileNames) {
    $matches = @(Get-ChildItem -LiteralPath $InstallRoot -File -Recurse -Filter $requiredFileName)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one required runtime member named '$requiredFileName'; got $($matches.Count)."
    }
}

$ReleaseManifestPath = Join-Path $InstallRoot 'release-manifest.json'
$releaseFiles = @(
    Get-ChildItem -LiteralPath $InstallRoot -File -Recurse |
        Where-Object { -not [string]::Equals($_.FullName, $ReleaseManifestPath, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $relative = Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($InstallRoot, $_.FullName))
            if ($relative.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.StartsWith('Vault/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.EndsWith('.db', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Forbidden payload member detected: $relative"
            }
            [ordered]@{
                relativePath = $relative
                byteLength = $_.Length
                sha256 = Get-Sha256Lower -Path $_.FullName
                role = Get-ReleaseRole -RelativePath $relative
            }
        } |
        Sort-Object { $_.relativePath }
)

$releaseManifest = [ordered]@{
    schemaVersion = 1
    productId = $ProductId
    productVersion = $ProductVersion
    runtimeIdentifier = $RuntimeIdentifier
    files = $releaseFiles
}
Write-JsonUtf8NoBom -Value $releaseManifest -Path $ReleaseManifestPath -Depth 8

$parsedRelease = Get-Content -LiteralPath $ReleaseManifestPath -Raw | ConvertFrom-Json
if ($parsedRelease.schemaVersion -ne 1 -or
    $parsedRelease.productId -ne $ProductId -or
    $parsedRelease.productVersion -ne $ProductVersion -or
    $parsedRelease.runtimeIdentifier -ne $RuntimeIdentifier) {
    throw 'Generated release-manifest.json identity is invalid.'
}

$manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $parsedRelease.files) {
    $relative = [string]$entry.relativePath
    if (-not $manifestPaths.Add($relative)) {
        throw "Duplicate release manifest path: $relative"
    }

    $path = Join-Path $InstallRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release manifest points to a missing file: $relative"
    }

    $info = Get-Item -LiteralPath $path
    if ($info.Length -ne [long]$entry.byteLength) {
        throw "Release manifest byte length mismatch: $relative"
    }
    Assert-Sha256 -Path $path -Expected ([string]$entry.sha256) -Label "release member $relative"
}

foreach ($requiredRelativePath in $RequiredReleaseMembers) {
    if (-not $manifestPaths.Contains($requiredRelativePath)) {
        throw "Release manifest is missing required membership: $requiredRelativePath"
    }
}
foreach ($requiredFileName in $RequiredUniqueFileNames) {
    $count = @($parsedRelease.files | Where-Object {
        [string]::Equals([IO.Path]::GetFileName([string]$_.relativePath), $requiredFileName, [StringComparison]::OrdinalIgnoreCase)
    }).Count
    if ($count -ne 1) {
        throw "Release manifest must contain exactly one required runtime member named '$requiredFileName'."
    }
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $InstallRoot '*') -DestinationPath $ZipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf) -or (Get-Item -LiteralPath $ZipPath).Length -le 0) {
    throw 'ZIP packaging did not produce a non-empty archive.'
}

if (Test-Path -LiteralPath $VerificationRoot) {
    Remove-Item -LiteralPath $VerificationRoot -Recurse -Force
}
Expand-Archive -LiteralPath $ZipPath -DestinationPath $VerificationRoot -Force

$expandedReleasePath = Join-Path $VerificationRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $expandedReleasePath -PathType Leaf)) {
    throw 'ZIP verification failed: release-manifest.json is missing after extraction.'
}
$expandedManifest = Get-Content -LiteralPath $expandedReleasePath -Raw | ConvertFrom-Json

$expectedExpanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$expectedExpanded.Add('release-manifest.json')
foreach ($entry in $expandedManifest.files) {
    [void]$expectedExpanded.Add([string]$entry.relativePath)
    $expandedPath = Join-Path $VerificationRoot ([string]$entry.relativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $expandedPath -PathType Leaf)) {
        throw "ZIP verification failed: missing $($entry.relativePath)"
    }

    $expandedInfo = Get-Item -LiteralPath $expandedPath
    if ($expandedInfo.Length -ne [long]$entry.byteLength) {
        throw "ZIP verification failed: byte length mismatch for $($entry.relativePath)"
    }
    Assert-Sha256 -Path $expandedPath -Expected ([string]$entry.sha256) -Label "ZIP member $($entry.relativePath)"
}

$actualExpanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $VerificationRoot -File -Recurse) {
    [void]$actualExpanded.Add((Normalize-RelativePath -Path ([IO.Path]::GetRelativePath($VerificationRoot, $file.FullName))))
}
if (-not $actualExpanded.SetEquals($expectedExpanded)) {
    $unexpected = @($actualExpanded | Where-Object { -not $expectedExpanded.Contains($_) })
    $missing = @($expectedExpanded | Where-Object { -not $actualExpanded.Contains($_) })
    throw "ZIP membership mismatch. Unexpected=[$($unexpected -join ', ')] Missing=[$($missing -join ', ')]"
}

$zipSha256 = Get-Sha256Lower -Path $ZipPath
$zipBytes = (Get-Item -LiteralPath $ZipPath).Length

# External feed authority for the exact ZIP produced above. This is intentionally generated only
# after final archive verification, so payloadByteLength and payloadSha256 cannot drift from the ZIP.
$UpdateManifestPath = Join-Path $OutputDirectory 'update.json'
$updateManifest = [ordered]@{
    schemaVersion = 1
    productId = $ProductId
    productVersion = $ProductVersion
    runtimeIdentifier = $RuntimeIdentifier
    payloadByteLength = [long]$zipBytes
    payloadSha256 = $zipSha256
    minimumCompatibleVersion = $null
    files = $releaseFiles
}
Write-JsonUtf8NoBom -Value $updateManifest -Path $UpdateManifestPath -Depth 8

$parsedUpdate = Get-Content -LiteralPath $UpdateManifestPath -Raw | ConvertFrom-Json
if ($parsedUpdate.schemaVersion -ne 1 -or
    $parsedUpdate.productId -ne $ProductId -or
    $parsedUpdate.productVersion -ne $ProductVersion -or
    $parsedUpdate.runtimeIdentifier -ne $RuntimeIdentifier -or
    [long]$parsedUpdate.payloadByteLength -ne [long]$zipBytes -or
    -not [string]::Equals([string]$parsedUpdate.payloadSha256, $zipSha256, [StringComparison]::Ordinal) -or
    $parsedUpdate.files.Count -ne $releaseFiles.Count) {
    throw 'Generated update.json failed structural or payload-identity validation.'
}

Write-Host "PACKAGE_READY=$ZipPath"
Write-Host "PACKAGE_SHA256=$zipSha256"
Write-Host "PACKAGE_BYTES=$zipBytes"
Write-Host "UPDATE_MANIFEST=$UpdateManifestPath"
Write-Host "INSTALL_FILE_COUNT=$($releaseFiles.Count + 1)"
Write-Host "APP_PATH=$AppRelativePath"
Write-Host "WORKER_PATH=$WorkerRelativePath"
Write-Host "UPDATER_PATH=$UpdaterRelativePath"
Write-Host "FFMPEG_VERSION=$ffmpegVersionLine"
Write-Host "FFPROBE_VERSION=$ffprobeVersionLine"

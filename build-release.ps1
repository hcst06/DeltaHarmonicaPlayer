[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^win-[a-z0-9]+$')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$SkipNpmRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)

function Get-SafeRepositoryPath {
    param([Parameter(Mandatory)][string]$RelativePath)

    $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    $requiredPrefix = $repoRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作仓库之外的路径：$fullPath"
    }
    return $fullPath
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
    }
}

$projectPath = Get-SafeRepositoryPath 'app\DeltaHarmonicaPlayer\DeltaHarmonicaPlayer.csproj'
$webPath = Get-SafeRepositoryPath 'web\audio-transcriber'
$assetsPath = Get-SafeRepositoryPath 'app\DeltaHarmonicaPlayer\AudioTranscriber'
$publishPath = Get-SafeRepositoryPath 'out\publish'
$stagePath = Get-SafeRepositoryPath 'out\portable-stage'
$outputsPath = Get-SafeRepositoryPath 'outputs'
[xml]$projectDocument = Get-Content -LiteralPath $projectPath -Raw
$releaseVersion = [string]($projectDocument.Project.PropertyGroup.Version | Select-Object -First 1)
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "项目版本号无效：$releaseVersion"
}
$zipPath = Join-Path $outputsPath "三角洲口风琴播放器-v$releaseVersion-便携版.zip"
$checksumPath = Join-Path $outputsPath 'SHA256SUMS.txt'

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$null = Get-Command npm -ErrorAction Stop

Push-Location $webPath
try {
    if (-not $SkipNpmRestore) {
        Invoke-Checked npm 'ci'
    }
    Invoke-Checked npm 'run' 'build'
}
finally {
    Pop-Location
}

foreach ($requiredAsset in @(
    'index.html',
    'app.js',
    'model\model.json',
    'model\group1-shard1of1.bin'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsPath $requiredAsset) -PathType Leaf)) {
        throw "离线扒谱资源生成不完整，缺少：$requiredAsset"
    }
}

Invoke-Checked dotnet 'restore' $projectPath '-r' $RuntimeIdentifier
Invoke-Checked dotnet 'build' $projectPath '-c' $Configuration '-r' $RuntimeIdentifier '--no-restore'

$testAssembly = Get-SafeRepositoryPath "app\DeltaHarmonicaPlayer\bin\$Configuration\net10.0-windows\$RuntimeIdentifier\DeltaHarmonicaPlayer.dll"
Invoke-Checked dotnet 'exec' $testAssembly '--self-test'

foreach ($pathToClean in @($publishPath, $stagePath)) {
    if (Test-Path -LiteralPath $pathToClean) {
        Remove-Item -LiteralPath $pathToClean -Recurse -Force
    }
}

Invoke-Checked dotnet `
    'publish' $projectPath `
    '-c' $Configuration `
    '-r' $RuntimeIdentifier `
    '--self-contained' 'true' `
    '--no-restore' `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:EnableCompressionInSingleFile=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false' `
    '-o' $publishPath

$stageApp = Join-Path $stagePath 'App'
$stageSongs = Join-Path $stagePath 'Songs'
$null = New-Item -ItemType Directory -Path $stageApp, $stageSongs -Force
Get-ChildItem -LiteralPath $publishPath -Force | Copy-Item -Destination $stageApp -Recurse -Force

foreach ($document in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
    $source = Get-SafeRepositoryPath $document
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "发布文档不存在：$document"
    }
    Copy-Item -LiteralPath $source -Destination $stagePath -Force
}

# A self-contained application carries .NET Runtime components. Preserve the
# exact SDK/runtime third-party notice used by the build when it is available.
$dotnetNotice = Join-Path (Split-Path -Parent $dotnetCommand.Source) 'ThirdPartyNotices.txt'
if (Test-Path -LiteralPath $dotnetNotice -PathType Leaf) {
    Copy-Item -LiteralPath $dotnetNotice `
        -Destination (Join-Path $stagePath 'DOTNET-THIRD-PARTY-NOTICES.txt') `
        -Force
}

$songHint = @'
把你有权使用的 .mid 或 .midi 文件放在这里，然后在播放器中点击“刷新”。
也可以使用播放器的“添加 MIDI”“简谱转 MIDI”或“音频转 MIDI”功能。
'@
[IO.File]::WriteAllText(
    (Join-Path $stageSongs '请把MIDI放在这里.txt'),
    $songHint,
    [Text.UTF8Encoding]::new($true))

$null = New-Item -ItemType Directory -Path $outputsPath -Force
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipInfo = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $checksumPath,
    "$zipHash  $($zipInfo.Name)`r`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "便携版已生成：$($zipInfo.FullName)"
Write-Host ("大小：{0:N1} MB" -f ($zipInfo.Length / 1MB))
Write-Host "SHA256：$zipHash"

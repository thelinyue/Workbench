param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '1.2.13',
    [Parameter(Mandatory = $true)]
    [string]$PluginBinaryPath,
    [string]$InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stagingRoot = Join-Path $PSScriptRoot '.staging'
$appPublish = Join-Path $stagingRoot 'app'
$dist = Join-Path $PSScriptRoot 'dist'
$pluginBinary = [System.IO.Path]::GetFullPath($PluginBinaryPath)
if (-not (Test-Path -LiteralPath $pluginBinary -PathType Leaf)) {
    throw "未找到正式发布所需的日志分析插件：$pluginBinary"
}
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $InnoCompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or -not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
    throw '未找到 Inno Setup 6 编译器 ISCC.exe，请先安装 Inno Setup 6，或通过 -InnoCompilerPath 显式传入。'
}

if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
if (Test-Path -LiteralPath $dist) {
    try {
        Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction Stop
    }
    catch {
        $dist = Join-Path $PSScriptRoot ("dist-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        Write-Warning "原 dist 目录正在被占用，改用独立输出目录：$dist"
    }
}
New-Item -ItemType Directory -Force -Path $appPublish, $dist | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$appProject = Join-Path $repoRoot 'src\HephaestusWorkbench.App\HephaestusWorkbench.App.csproj'
$innoScript = Join-Path $PSScriptRoot 'HephaestusWorkbench.iss'
Write-Host '正在还原 win-x64 发布依赖……'
& dotnet restore $appProject -r win-x64 --configfile (Join-Path $repoRoot 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "应用还原失败，退出码：$LASTEXITCODE" }

Write-Host '正在发布 self-contained 主程序……'
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:PluginBinaryPath=$pluginBinary -p:DebugType=None -p:DebugSymbols=false -o $appPublish
if ($LASTEXITCODE -ne 0) { throw "应用发布失败，退出码：$LASTEXITCODE" }

Write-Host '正在生成标准单文件离线安装包……'
& $InnoCompilerPath "/DMyAppVersion=$Version" "/DAppSource=$appPublish" "/DOutputDir=$dist" $innoScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码：$LASTEXITCODE" }
$setupFileName = "HephaestusWorkbench_v$Version.exe"
$setupExecutable = Join-Path $dist $setupFileName
if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) {
    throw "未生成预期的安装包：$setupExecutable"
}

$hashFiles = @($setupFileName)
$hashLines = foreach ($name in $hashFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dist $name)).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "标准单文件离线安装包已生成：$setupExecutable"

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '1.2.0',
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
Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host '正在还原 win-x64 发布依赖……'
& dotnet restore $appProject -r win-x64 --configfile (Join-Path $repoRoot 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "应用还原失败，退出码：$LASTEXITCODE" }

Write-Host '正在发布 self-contained 主程序……'
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:PluginBinaryPath=$pluginBinary -p:DebugType=None -p:DebugSymbols=false -o $appPublish
if ($LASTEXITCODE -ne 0) { throw "应用发布失败，退出码：$LASTEXITCODE" }

Write-Host '正在生成标准单文件离线安装包……'
& $InnoCompilerPath "/DMyAppVersion=$Version" "/DAppSource=$appPublish" "/DOutputDir=$dist" $innoScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码：$LASTEXITCODE" }
$setupFileName = "HephaestusWorkbench_Setup_v$Version.exe"
$setupExecutable = Join-Path $dist $setupFileName
if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) {
    throw "未生成预期的安装包：$setupExecutable"
}

$pluginPackageDirectory = Join-Path $stagingRoot 'plugin-package'
New-Item -ItemType Directory -Force -Path $pluginPackageDirectory | Out-Null
Copy-Item -LiteralPath $pluginBinary -Destination (Join-Path $pluginPackageDirectory 'log_analyzer.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\HephaestusWorkbench.App\PluginSeed\manifest.json') -Destination (Join-Path $pluginPackageDirectory 'manifest.json') -Force
$pluginPackage = Join-Path $dist 'log-analyzer-1.50-win-x64.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($pluginPackageDirectory, $pluginPackage, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hashFiles = @($setupFileName)
$hashLines = foreach ($name in $hashFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dist $name)).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))

$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginPackage).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    (Join-Path $dist 'log-analyzer-1.50-win-x64.zip.sha256'),
    "$pluginHash  log-analyzer-1.50-win-x64.zip`n",
    [System.Text.UTF8Encoding]::new($false))
$catalogDirectory = Join-Path $dist 'marketplace'
New-Item -ItemType Directory -Force -Path $catalogDirectory | Out-Null
$catalog = [ordered]@{
    schemaVersion = 1
    plugins = @(
        [ordered]@{
            id = 'log-analyzer'
            name = '日志分析插件'
            description = '赫菲斯托斯工程工作台官方日志分析插件。'
            version = '1.50'
            type = 'Exe'
            packageUrl = 'https://github.com/thelinyue/Hephaestus-Workbench-Releases/releases/download/plugin-log-analyzer-v1.50/log-analyzer-1.50-win-x64.zip'
            sha256 = $pluginHash
            packageSize = (Get-Item -LiteralPath $pluginPackage).Length
            minimumAppVersion = '1.1.0'
            releaseNotesUrl = 'https://github.com/thelinyue/Hephaestus-Workbench-Releases/releases/tag/plugin-log-analyzer-v1.50'
            author = 'thelinyue'
            license = 'Proprietary binary distribution'
            repository = 'https://github.com/thelinyue/Hephaestus-Workbench-Releases'
            manifest = [ordered]@{
                id = 'log-analyzer'
                name = '日志分析插件'
                version = '1.50'
                type = 'Exe'
                entry = 'log_analyzer.exe'
                runner = 'legacy-log-analyzer'
                reportPath = 'report/report.html'
            }
        }
    )
}
$catalogJson = $catalog | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $catalogDirectory 'catalog.json'), $catalogJson, [System.Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "标准单文件离线安装包已生成：$setupExecutable"

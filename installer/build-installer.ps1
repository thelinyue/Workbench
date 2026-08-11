param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '1.1.0',
    [Parameter(Mandatory = $true)]
    [string]$PluginBinaryPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stagingRoot = Join-Path $PSScriptRoot '.staging'
$appPublish = Join-Path $stagingRoot 'app'
$setupPublish = Join-Path $stagingRoot 'setup'
$dist = Join-Path $PSScriptRoot 'dist'
$payload = Join-Path $PSScriptRoot 'Payload.zip'
$pluginBinary = [System.IO.Path]::GetFullPath($PluginBinaryPath)
if (-not (Test-Path -LiteralPath $pluginBinary -PathType Leaf)) {
    throw "未找到正式发布所需的日志分析插件：$pluginBinary"
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
New-Item -ItemType Directory -Force -Path $appPublish, $setupPublish, $dist | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$appProject = Join-Path $repoRoot 'src\HephaestusWorkbench.App\HephaestusWorkbench.App.csproj'
$setupProject = Join-Path $PSScriptRoot 'HephaestusWorkbench.Setup\HephaestusWorkbench.Setup.csproj'
Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host 'Restoring win-x64 publish assets...'
& dotnet restore $appProject -r win-x64 --configfile (Join-Path $repoRoot 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "Application restore failed, exit code: $LASTEXITCODE" }
& dotnet restore $setupProject -r win-x64 --configfile (Join-Path $repoRoot 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "Installer restore failed, exit code: $LASTEXITCODE" }

Write-Host 'Publishing Hephaestus Workbench application...'
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:PluginBinaryPath=$pluginBinary -p:DebugType=None -p:DebugSymbols=false -o $appPublish
if ($LASTEXITCODE -ne 0) { throw "Application publish failed, exit code: $LASTEXITCODE" }

if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($appPublish, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host 'Publishing installer...'
& dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $setupPublish
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed, exit code: $LASTEXITCODE" }
$setupExecutable = Join-Path $setupPublish 'HephaestusWorkbench-Setup.exe'
if (-not (Test-Path -LiteralPath $setupExecutable)) { throw "Installer executable was not generated: $setupExecutable" }

Copy-Item -LiteralPath $setupExecutable -Destination (Join-Path $dist 'HephaestusWorkbench_Setup.exe') -Force
Copy-Item -LiteralPath $setupExecutable -Destination (Join-Path $dist 'HephaestusWorkbench_Update.exe') -Force
Copy-Item -LiteralPath $setupExecutable -Destination (Join-Path $dist 'HephaestusWorkbench_Uninstall.exe') -Force

$pluginPackageDirectory = Join-Path $stagingRoot 'plugin-package'
New-Item -ItemType Directory -Force -Path $pluginPackageDirectory | Out-Null
Copy-Item -LiteralPath $pluginBinary -Destination (Join-Path $pluginPackageDirectory 'log_analyzer.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\HephaestusWorkbench.App\PluginSeed\manifest.json') -Destination (Join-Path $pluginPackageDirectory 'manifest.json') -Force
$pluginPackage = Join-Path $dist 'log-analyzer-1.50-win-x64.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($pluginPackageDirectory, $pluginPackage, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hashFiles = @(
    'HephaestusWorkbench_Setup.exe',
    'HephaestusWorkbench_Update.exe',
    'HephaestusWorkbench_Uninstall.exe',
    'log-analyzer-1.50-win-x64.zip'
)
$hashLines = foreach ($name in $hashFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dist $name)).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))

$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginPackage).Hash.ToLowerInvariant()
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
        }
    )
}
$catalogJson = $catalog | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $catalogDirectory 'catalog.json'), $catalogJson, [System.Text.UTF8Encoding]::new($false))

$webView2Installer = Join-Path $PSScriptRoot 'dependencies\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$prerequisiteOutput = Join-Path $dist 'Prerequisites'
if (Test-Path -LiteralPath $webView2Installer) {
    New-Item -ItemType Directory -Force -Path $prerequisiteOutput | Out-Null
    Copy-Item -LiteralPath $webView2Installer -Destination $prerequisiteOutput -Force
    Write-Host 'Copied the offline WebView2 installer.'
} else {
    Write-Warning 'Offline WebView2 installer not found; setup will show a manual installation message if needed.'
}

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "Installer package generated: $dist"

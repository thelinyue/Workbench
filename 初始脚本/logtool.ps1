# ============================================================
# 脚本名称：logtool.ps1
# 版本：1.49
# 构建日期：2026-07-24
# 作者：包毅思
# 免责声明：使用本脚本风险自负，请谨慎操作！
# 功能：日志解压、SSH命令生成、去除LVM缓存配置（支持文件/粘贴）、
#       按天数删除、彻底清空、配置管理、隐藏备份
# 修复：确保统计数字正常显示，使用 @() 强制数组计数。
# 警告：清理操作永久删除文件（不进回收站）！
# ============================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "logtool"
$scriptVersion = "1.49"
$buildDate = "2026-07-24"
$configFile = ".\logtool_config.txt"
$script:SevenZipPath = $null

# ---------- 统一错误处理 ----------
function Handle-Error {
    param([string]$Message, [System.Exception]$Exception = $null, [switch]$Exit)
    Write-Host "错误：$Message" -ForegroundColor Red
    if ($Exception) { Write-Host "详细信息：$($Exception.Message)" -ForegroundColor DarkRed }
    if ($Exit) { Read-Host "按 Enter 退出" ; exit 1 }
}

# ---------- 辅助函数 ----------
function Sanitize-Path($path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $path }
    return $path.Trim('"').Trim("'").Trim()
}

function Get-DisplayWidth($str) {
    $width = 0
    foreach ($c in $str.ToCharArray()) {
        if ($c -match '[\u4e00-\u9fa5]') { $width += 2 } else { $width += 1 }
    }
    return $width
}

function Print-Banner {
    $totalWidth = 60
    $line = "═" * $totalWidth
    $topLines = @(
        "日志管理工具 logtool",
        "版本：$scriptVersion (Build $buildDate)",
        "作者：包毅思"
    )
    $bottomLines = @(
        "功能：解压 .tgz/.temp/.zip 日志，SSH命令生成，",
        "      去除 LVM 缓存配置，按天删除，彻底清空，配置管理。",
        "免责声明：使用本脚本风险自负，请谨慎操作！",
        "警告：所有清理操作均永久删除文件（不进回收站）！"
    )

    Write-Host "╔$line╗" -ForegroundColor Cyan
    foreach ($txt in $topLines + $bottomLines) {
        $cur = Get-DisplayWidth $txt
        $padL = [Math]::Floor(($totalWidth - $cur) / 2)
        $padR = $totalWidth - $cur - $padL
        $padded = (' ' * $padL) + $txt + (' ' * $padR)
        Write-Host "║$padded║" -ForegroundColor Cyan
    }
    Write-Host "╚$line╝" -ForegroundColor Cyan
}

function Get-NameWithoutExtensions($fileName) {
    $name = $fileName
    while ($true) {
        $ext = [System.IO.Path]::GetExtension($name)
        if ([string]::IsNullOrEmpty($ext)) { break }
        $name = [System.IO.Path]::GetFileNameWithoutExtension($name)
    }
    return $name
}

function Format-Size($bytes) {
    if ($bytes -gt 1GB) { return "{0:N2} GB" -f ($bytes / 1GB) }
    if ($bytes -gt 1MB) { return "{0:N2} MB" -f ($bytes / 1MB) }
    return "{0:N2} KB" -f ($bytes / 1KB)
}

function Get-LogFileDetails($path) {
    $files = @(Get-ChildItem -Path $path -File | Where-Object { $_.Extension -match "\.(tgz|temp|zip)$" })
    foreach ($dir in (Get-ChildItem -Path $path -Directory)) {
        $files += Get-ChildItem -Path $dir.FullName -File | Where-Object { $_.Extension -match "\.(tgz|temp|zip)$" }
    }
    return $files | ForEach-Object {
        $base = Get-NameWithoutExtensions $_.Name
        [PSCustomObject]@{
            File        = $_
            Path        = $_.FullName
            Directory   = $_.DirectoryName
            FileName    = $_.Name
            ExpectedDir = Join-Path $_.DirectoryName $base
            IsExtracted = (Test-Path (Join-Path $_.DirectoryName $base))
            IsZip       = ($_.Extension -eq ".zip")
        }
    }
}

function Confirm-Action($prompt) {
    do { $r = Read-Host "$prompt (Y/N)" } while ($r -notmatch '^[YyNn]$')
    return ($r -eq 'Y' -or $r -eq 'y')
}

function Return-MenuOrExit {
    $key = Read-Host "`n按 Enter 返回菜单，或输入 Q 退出"
    if ($key -eq "Q" -or $key -eq "q") { exit }
}

# ---------- 配置读写 ----------
function Read-Config {
    if (-not (Test-Path $configFile)) { return $null }
    $lines = Get-Content $configFile
    return @{
        TargetPath = if ($lines.Count -ge 1) { Sanitize-Path $lines[0] } else { "" }
        LogExe     = if ($lines.Count -ge 2) { Sanitize-Path $lines[1] } else { "" }
        SevenZipPath = if ($lines.Count -ge 3) { Sanitize-Path $lines[2] } else { "" }
    }
}

function Write-Config($target, $logExe, $sevenZipPath) {
    try {
        @(Sanitize-Path $target, Sanitize-Path $logExe, Sanitize-Path $sevenZipPath) |
            Out-File -FilePath $configFile -Encoding Default -ErrorAction Stop
    } catch {
        Handle-Error "写入配置文件失败：$($_.Exception.Message)" -Exception $_ -Exit
    }
}

function Set-Config {
    Clear-Host
    Write-Host "========== 配置设置 ==========" -ForegroundColor Cyan
    $cur = Read-Config
    $defTarget = if ($cur -and $cur.TargetPath) { $cur.TargetPath } else { "D:\Downloads" }
    $defLog = if ($cur -and $cur.LogExe) { $cur.LogExe } else { ".\log.exe" }
    $def7z = if ($cur -and $cur.SevenZipPath) { $cur.SevenZipPath } else { "" }

    Write-Host "请输入日志下载目录（直接回车保留当前值）"
    Write-Host "当前值: $defTarget" -ForegroundColor Yellow
    $t = Read-Host "路径"
    if ([string]::IsNullOrWhiteSpace($t)) { $t = $defTarget } else { $t = Sanitize-Path $t }

    Write-Host "`n请输入 log.exe 路径（直接回车保留当前值）"
    Write-Host "当前值: $defLog" -ForegroundColor Yellow
    $l = Read-Host "路径"
    if ([string]::IsNullOrWhiteSpace($l)) { $l = $defLog } else { $l = Sanitize-Path $l }

    Write-Host "`n请输入 7z.exe 路径（直接回车保留当前值）"
    Write-Host "当前值: $(if ($def7z) { $def7z } else { '(未设置)' })" -ForegroundColor Yellow
    $z = Read-Host "路径"
    if ([string]::IsNullOrWhiteSpace($z)) { $z = $def7z } else { $z = Sanitize-Path $z }

    Write-Config $t $l $z
    Write-Host "`n配置已保存。" -ForegroundColor Green
    Read-Host "按 Enter 继续"
}

# ---------- 获取 7z 路径（带缓存） ----------
function Get-7zPath {
    if ($script:SevenZipPath -and (Test-Path $script:SevenZipPath)) { return $script:SevenZipPath }
    $config = Read-Config
    if ($config -and $config.SevenZipPath -and (Test-Path $config.SevenZipPath)) {
        $script:SevenZipPath = $config.SevenZipPath
        return $script:SevenZipPath
    }
    $7zCmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($7zCmd) {
        $script:SevenZipPath = $7zCmd.Source
        Write-Config $config.TargetPath $config.LogExe $script:SevenZipPath
        return $script:SevenZipPath
    }
    $commonPaths = @("C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe")
    foreach ($p in $commonPaths) {
        if (Test-Path $p) {
            $script:SevenZipPath = $p
            Write-Config $config.TargetPath $config.LogExe $script:SevenZipPath
            return $script:SevenZipPath
        }
    }

    Write-Host "未找到 7z.exe，请输入完整路径（直接回车跳过 zip 解压）：" -ForegroundColor Yellow
    $userPath = Sanitize-Path (Read-Host "路径")
    if ($userPath -and (Test-Path $userPath)) {
        $script:SevenZipPath = $userPath
        Write-Config $config.TargetPath $config.LogExe $script:SevenZipPath
        return $script:SevenZipPath
    }
    Write-Config $config.TargetPath $config.LogExe ""
    $script:SevenZipPath = $null
    return $null
}

# ---------- 通用解压执行 ----------
function Invoke-ExtractGeneric {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$Command,
        [string]$ArgumentTemplate,
        [bool]$UseTarget = $true
    )
    $failed = @()
    $oldEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try {
        $total = $Files.Count
        $i = 0
        foreach ($f in $Files) {
            $i++
            Write-Host "正在解压 ($i/$total)：$($f.FullName)" -ForegroundColor Gray
            if ($UseTarget) {
                $targetDir = Get-NameWithoutExtensions $f.Name
                $targetPath = Join-Path $f.DirectoryName $targetDir
                $arg = $ArgumentTemplate -replace '{path}', "`"$($f.FullName)`"" -replace '{target}', "`"$targetPath`""
            } else {
                $arg = $ArgumentTemplate -replace '{path}', "`"$($f.FullName)`""
            }
            Invoke-Expression "& '$Command' $arg" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  成功" -ForegroundColor Green
            } else {
                Write-Host "  ❌ 失败（退出码：$LASTEXITCODE）" -ForegroundColor Red
                $failed += $f
            }
        }
    } finally {
        [Console]::OutputEncoding = $oldEncoding
    }
    return $failed
}

function Invoke-ExtractTgz($files, $logExe) {
    return Invoke-ExtractGeneric -Files $files -Command $logExe -ArgumentTemplate '-d {path}' -UseTarget $false
}

function Invoke-ExtractZip($files) {
    $7zPath = Get-7zPath
    if (-not $7zPath) {
        Write-Host "错误：未配置 7z.exe，跳过 zip 解压。" -ForegroundColor Red
        return $files
    }
    return Invoke-ExtractGeneric -Files $files -Command $7zPath -ArgumentTemplate 'x {path} -o{target} -y' -UseTarget $true
}

# ---------- 清理功能 ----------
function Invoke-Cleanup($items, $msg, $excludePaths = @()) {
    $items = $items | Where-Object { $_.Path -notin $excludePaths }
    if ($items.Count -eq 0) { Write-Host "没有可清理的项目。" -ForegroundColor Green; return }
    Write-Host "`n$msg" -ForegroundColor Cyan
    foreach ($it in $items) {
        Write-Host "  压缩包: $($it.FileName)" -ForegroundColor Gray
        Write-Host "  目录: $($it.ExpectedDir)" -ForegroundColor Gray
    }
    if (Confirm-Action "确认永久删除") {
        $cnt = 0
        foreach ($it in $items) {
            if (Test-Path $it.ExpectedDir) { Remove-Item -Path $it.ExpectedDir -Recurse -Force -ErrorAction SilentlyContinue; $cnt++ }
            if (Test-Path $it.Path) { Remove-Item -Path $it.Path -Force -ErrorAction SilentlyContinue; $cnt++ }
        }
        Write-Host "清理完成，处理了 $cnt 个项目。" -ForegroundColor Green
    }
}

# ---------- 提取花括号块 ----------
function Get-BraceBlock($text, $startPattern) {
    $start = [regex]::Match($text, $startPattern).Index
    if ($start -lt 0) { return $null }
    $open = $text.IndexOf('{', $start)
    if ($open -lt 0) { return $null }
    $depth = 0
    $pos = $open
    $len = $text.Length
    while ($pos -lt $len) {
        $ch = $text[$pos]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') { $depth-- }
        if ($depth -eq 0) {
            return $text.Substring($open, $pos - $open + 1)
        }
        $pos++
    }
    return $null
}

# ---------- 提取 VG 名称 ----------
function Extract-VGName($content) {
    if ($content -match '(?ms)^(\w+)\s*\{') {
        return $matches[1]
    }
    return $null
}

# ---------- 去除 LVM 缓存配置 ----------
function Remove-LVMCache {
    param([string]$InputFile, [string]$InputContent, [string]$OutputFile)
    $content = if ($InputContent) { $InputContent } else {
        if (-not (Test-Path $InputFile)) { Write-Host "错误：输入文件不存在。" -ForegroundColor Red; return $null }
        Get-Content $InputFile -Raw
    }

    $corigBlock = Get-BraceBlock $content '(?ms)volume1_corig\s*\{'
    if (-not $corigBlock) { Write-Host "错误：未找到 volume1_corig。" -ForegroundColor Red; return $null }
    $corigSegment = Get-BraceBlock $corigBlock '(?ms)segment1\s*\{'
    if (-not $corigSegment) { Write-Host "错误：未找到 volume1_corig 的 segment1。" -ForegroundColor Red; return $null }
    $newSegment = "segment1 " + $corigSegment

    $vol1Block = Get-BraceBlock $content '(?ms)volume1\s*\{'
    if (-not $vol1Block) { Write-Host "错误：未找到 volume1。" -ForegroundColor Red; return $null }
    $newVol1Block = [regex]::Replace($vol1Block, '(?ms)segment1\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}', $newSegment)
    $content = $content.Replace($vol1Block, $newVol1Block)

    # 删除缓存相关块
    $content = [regex]::Replace($content, '(?ms)volume1_lvmcache_cvol\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}', '')
    $content = [regex]::Replace($content, '(?ms)volume1_corig\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}', '')
    $content = [regex]::Replace($content, '(?ms)pv1\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}', '')
    $content = $content -replace '(?m)^\s*$', ''

    # 验证 physical_volumes 非空
    $pvBlock = Get-BraceBlock $content '(?ms)physical_volumes\s*\{'
    if ($pvBlock) {
        $pvInner = $pvBlock -replace '(?ms)^\s*physical_volumes\s*\{\s*|\s*\}\s*$', ''
        if ([string]::IsNullOrWhiteSpace($pvInner)) {
            Write-Host "错误：去除缓存后 physical_volumes 为空。" -ForegroundColor Red
            return $null
        }
    } else {
        Write-Host "错误：未找到 physical_volumes。" -ForegroundColor Red
        return $null
    }

    if ($OutputFile) {
        try {
            $outDir = Split-Path $OutputFile -Parent
            if (-not (Test-Path $outDir)) { New-Item -Path $outDir -ItemType Directory -Force | Out-Null }
            $content | Out-File -FilePath $OutputFile -Encoding Default
            Write-Host "去除缓存成功，输出：$(Resolve-Path $OutputFile)" -ForegroundColor Green
            return $true
        } catch {
            Handle-Error "写入输出文件失败：$($_.Exception.Message)" -Exception $_
            return $null
        }
    }
    return $content
}

# ---------- 加载配置 ----------
$config = Read-Config
if (-not $config) {
    Write-Host "首次运行，请进行初始配置：" -ForegroundColor Cyan
    Set-Config
    $config = Read-Config
}

$global:targetPath = if ($config -and $config.TargetPath) { $config.TargetPath } else { "" }
$global:logExe = if ($config -and $config.LogExe) { $config.LogExe } else { "" }

# 验证路径
if (-not $global:targetPath -or -not (Test-Path $global:targetPath)) {
    Write-Host "日志目录配置无效，请重新配置。" -ForegroundColor Yellow
    Set-Config
    $config = Read-Config
    $global:targetPath = $config.TargetPath
    $global:logExe = $config.LogExe
}
if (-not (Test-Path $global:targetPath)) { New-Item -Path $global:targetPath -ItemType Directory -Force | Out-Null }

if (-not $global:logExe -or -not (Test-Path $global:logExe)) {
    Write-Host "log.exe 路径无效，请重新配置。" -ForegroundColor Yellow
    Set-Config
    $config = Read-Config
    $global:logExe = $config.LogExe
}

# ---------- 主循环 ----------
while ($true) {
    Clear-Host
    Print-Banner
    Write-Host ""
    Write-Host "========== 当前配置 ==========" -ForegroundColor Magenta
    Write-Host "  日志目录：$global:targetPath"
    Write-Host "  解压工具：$global:logExe"
    Write-Host "  7z 路径：$($config.SevenZipPath -replace '^$','(未设置)')"
    Write-Host "================================" -ForegroundColor Magenta
    Write-Host ""
    Write-Host "========== 功能菜单 ==========" -ForegroundColor Cyan
    Write-Host " 1 - 解压日志文件（.tgz / .temp / .zip）"
    Write-Host " 2 - SSH 命令生成"
    Write-Host " 3 - 去除 LVM 缓存配置（ugos系统）"
    Write-Host " 4 - 按天数（1-7天）删除日志压缩包及解压文件夹"
    Write-Host " 5 - 删除所有文件和文件夹（彻底清空）"
    Write-Host " 6 - 修改配置"
    Write-Host " 0 - 退出脚本"
    Write-Host "=================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "⚠️  警告：清理操作永久删除，不会进入回收站！" -ForegroundColor Red
    Write-Host "   请确认目标目录仅存放日志文件。" -ForegroundColor Yellow

    $choice = Read-Host "`n请输入数字"

    if ($choice -eq "666") {
        Clear-Host
        Write-Host "========== 隐藏工具 ==========" -ForegroundColor Cyan
        Write-Host " 1 - 备份当前脚本"
        Write-Host " 0 - 取消"
        $subChoice = Read-Host "`n请输入数字"
        if ($subChoice -eq "1") {
            $scriptPath = $MyInvocation.MyCommand.Path
            $scriptDir = if ($scriptPath) { Split-Path $scriptPath } else { (Get-Location).Path }
            $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
            $versionTag = $scriptVersion -replace '\.', '_'
            $backupPath = Join-Path $scriptDir "logtool_${versionTag}_${timestamp}.ps1"
            if (Test-Path $backupPath) { Write-Host "文件已存在，是否覆盖？" -ForegroundColor Yellow; if (-not (Confirm-Action "覆盖")) { continue } }
            if ($scriptPath) { Copy-Item -Path $scriptPath -Destination $backupPath -Force; Write-Host "备份成功：$backupPath" -ForegroundColor Green } else { Write-Host "错误：无法获取脚本路径。" -ForegroundColor Red }
            Read-Host "按 Enter 返回"
        }
        continue
    }

    try {
        switch ($choice) {
            "0" { exit }

            "2" {  # SSH
                Clear-Host
                Write-Host "========== SSH 命令生成 ==========" -ForegroundColor Cyan
                $json = Read-Host '请输入 JSON（如 {"port":44827,"ip":"43.248.128.27"}）'
                try { $obj = $json | ConvertFrom-Json; if (-not $obj.port -or -not $obj.ip) { throw } } catch { Write-Host "JSON 格式无效" -ForegroundColor Red; Read-Host "按 Enter 返回"; continue }
                $ip = $obj.ip; $port = $obj.port
                while ($true) {
                    $user = Read-Host "请输入用户名（输入 q 取消）"
                    if ($user -eq "q" -or $user -eq "Q") { Write-Host "已取消。"; continue 2 }
                    if (-not [string]::IsNullOrWhiteSpace($user)) { break }
                    Write-Host "用户名不能为空。" -ForegroundColor Red
                }
                Write-Host "`n生成的命令：ssh $user@$ip -p $port" -ForegroundColor Green
                Read-Host "按 Enter 返回"
                continue
            }

            "3" {  # 去除 LVM 缓存
                Clear-Host
                Write-Host "========== 去除 LVM 缓存配置 ==========" -ForegroundColor Cyan
                Write-Host "请选择输入方式："
                Write-Host " 1 - 从文件读取"
                Write-Host " 2 - 直接粘贴 vg 信息"
                Write-Host " 0 - 返回"
                $inputChoice = Read-Host "`n请输入数字"
                if ($inputChoice -eq "0") { continue }

                if ($inputChoice -eq "1") {
                    $inputFile = $null
                    $vgName = $null
                    while ($true) {
                        Write-Host "`n请输入包含缓存配置的 LVM 配置文件路径（输入 0 取消）：" -ForegroundColor Yellow
                        $inputFile = Read-Host "路径"
                        if ($inputFile -eq "0") { Write-Host "操作取消。"; break }
                        if ([string]::IsNullOrWhiteSpace($inputFile)) { Write-Host "路径不能为空，请重新输入。" -ForegroundColor Red; continue }
                        $inputFile = Sanitize-Path $inputFile
                        if (-not (Test-Path $inputFile)) {
                            Write-Host "文件不存在，请重新输入。" -ForegroundColor Red
                            continue
                        }
                        try {
                            $fileContent = Get-Content $inputFile -Raw
                            $vgName = Extract-VGName $fileContent
                            if (-not $vgName) {
                                $vgName = [System.IO.Path]::GetFileNameWithoutExtension($inputFile)
                                Write-Host "警告：无法从文件提取 VG 名称，将使用文件名作为基础。" -ForegroundColor Yellow
                            }
                            break
                        } catch {
                            Write-Host "读取文件失败，请检查文件是否可读。" -ForegroundColor Red
                            continue
                        }
                    }
                    if (-not $inputFile) { continue }

                    $defaultOutput = Join-Path (Split-Path $inputFile) "${vgName}_nocache.txt"
                    Write-Host "`n输出文件路径（直接回车将使用默认路径）：" -ForegroundColor Yellow
                    Write-Host "默认路径：$defaultOutput" -ForegroundColor Gray
                    $outputFile = Read-Host "路径"
                    if ([string]::IsNullOrWhiteSpace($outputFile)) {
                        $outputFile = $defaultOutput
                        Write-Host "使用默认路径：$outputFile" -ForegroundColor Green
                    } else {
                        $outputFile = Sanitize-Path $outputFile
                    }

                    $result = Remove-LVMCache -InputFile $inputFile -OutputFile $outputFile
                    if ($result -eq $true) { Write-Host "操作完成。" -ForegroundColor Green } else { Write-Host "操作失败。" -ForegroundColor Red }
                    Read-Host "按 Enter 返回"
                    continue
                }

                if ($inputChoice -eq "2") {
                    while ($true) {
                        Write-Host "`n请粘贴 vg 配置内容（输入完成后，输入单独一行的 'END' 结束；输入 0 取消）：" -ForegroundColor Yellow
                        $lines = @()
                        $cancel = $false
                        while ($true) {
                            $line = Read-Host
                            if ($line -eq "0") { $cancel = $true; break }
                            if ($line -eq "END") { break }
                            $lines += $line
                        }
                        if ($cancel) { Write-Host "操作取消。"; break }
                        $inputContent = $lines -join "`n"
                        if ([string]::IsNullOrWhiteSpace($inputContent)) {
                            Write-Host "未输入任何内容，请重新粘贴。" -ForegroundColor Red
                            continue
                        }
                        Write-Host "`n正在处理..." -ForegroundColor Cyan
                        $result = Remove-LVMCache -InputContent $inputContent
                        if ($result) {
                            Write-Host "处理成功。" -ForegroundColor Green
                            Write-Host "`n是否导出为 txt 文件？" -ForegroundColor Yellow
                            if (Confirm-Action "导出") {
                                $defaultFile = "vg_nocache.txt"
                                $counter = 1
                                while (Test-Path $defaultFile) {
                                    $defaultFile = "vg_nocache_$counter.txt"
                                    $counter++
                                }
                                $outFile = Read-Host "输出文件路径（直接回车使用 $defaultFile）"
                                if (-not $outFile) { $outFile = $defaultFile } else { $outFile = Sanitize-Path $outFile }
                                try {
                                    $outDir = Split-Path $outFile -Parent
                                    if ($outDir -and -not (Test-Path $outDir)) { New-Item -Path $outDir -ItemType Directory -Force | Out-Null }
                                    $result | Out-File -FilePath $outFile -Encoding Default
                                    Write-Host "已导出至：$(Resolve-Path $outFile)" -ForegroundColor Green
                                    Write-Host "`n去除缓存后的 vg 配置如下：" -ForegroundColor Green
                                    Write-Host "--------------------------------------------------" -ForegroundColor Cyan
                                    Write-Host $result
                                    Write-Host "--------------------------------------------------" -ForegroundColor Cyan
                                } catch {
                                    Handle-Error "导出失败：$($_.Exception.Message)" -Exception $_
                                }
                            } else {
                                Write-Host "未导出，结果如下：" -ForegroundColor Yellow
                                Write-Host "--------------------------------------------------" -ForegroundColor Cyan
                                Write-Host $result
                                Write-Host "--------------------------------------------------" -ForegroundColor Cyan
                            }
                            break
                        } else {
                            Write-Host "处理失败，请检查内容格式，重新粘贴。" -ForegroundColor Red
                        }
                    }
                    Read-Host "按 Enter 返回"
                    continue
                }

                Write-Host "无效选择。" -ForegroundColor Red
                Read-Host "按 Enter 返回"
                continue
            }

            "4" {  # 按天数删除
                if (-not (Test-Path $global:targetPath)) { Write-Host "错误：目录不存在" -ForegroundColor Red; Return-MenuOrExit; continue }
                $daysInput = Read-Host "`n请输入天数（1-7）"
                if ($daysInput -notmatch "^\d+$" -or [int]$daysInput -lt 1 -or [int]$daysInput -gt 7) { Write-Host "错误：请输入 1~7 的数字。" -ForegroundColor Red; Read-Host "按 Enter 返回"; continue }
                $days = [int]$daysInput
                $cutoff = (Get-Date).AddDays(-$days)

                $details = Get-LogFileDetails $global:targetPath
                $oldItems = @($details | Where-Object { $_.File.LastWriteTime -lt $cutoff })
                if ($oldItems.Count -eq 0) { Write-Host "`n没有 $days 天前的日志压缩包。" -ForegroundColor Green; Return-MenuOrExit; continue }

                $totalSize = 0
                foreach ($it in $oldItems) {
                    $totalSize += (Get-Item $it.Path).Length
                    if (Test-Path $it.ExpectedDir) { $totalSize += (Get-ChildItem -Path $it.ExpectedDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum }
                }
                Write-Host "`n⚠️ 将删除 $days 天前的日志压缩包及解压目录（共 $($oldItems.Count) 个，约 $(Format-Size $totalSize)）" -ForegroundColor Red
                Write-Host "`n详情：" -ForegroundColor Cyan
                foreach ($it in $oldItems) {
                    $rel = if ($it.Directory -eq $global:targetPath) { $it.FileName } else { "$(Split-Path $it.Directory -Leaf)\$($it.FileName)" }
                    Write-Host "  压缩包: $rel" -ForegroundColor Gray
                    if (Test-Path $it.ExpectedDir) { Write-Host "  目录: $($it.ExpectedDir)" -ForegroundColor Gray }
                }
                if (Confirm-Action "确认永久删除") {
                    $cnt = 0
                    foreach ($it in $oldItems) {
                        if (Test-Path $it.ExpectedDir) { Remove-Item -Path $it.ExpectedDir -Recurse -Force -ErrorAction SilentlyContinue; $cnt++ }
                        if (Test-Path $it.Path) { Remove-Item -Path $it.Path -Force -ErrorAction SilentlyContinue; $cnt++ }
                    }
                    Write-Host "`n成功删除 $cnt 个项目。" -ForegroundColor Green
                }
                Return-MenuOrExit
                continue
            }

            "5" {  # 彻底清空
                if (-not (Test-Path $global:targetPath)) { Write-Host "错误：目录不存在" -ForegroundColor Red; Return-MenuOrExit; continue }
                Write-Host "`n⚠️ 此操作将删除 '$global:targetPath' 下的所有内容！" -ForegroundColor Red
                Write-Host "   请确保该目录仅存放日志文件。" -ForegroundColor Yellow
                if (-not (Confirm-Action "确认继续")) { Write-Host "取消操作。" -ForegroundColor Yellow; Return-MenuOrExit; continue }
                $all = Get-ChildItem -Path $global:targetPath -Force
                if ($all.Count -eq 0) { Write-Host "目录已是空的。" -ForegroundColor Green; Return-MenuOrExit; continue }
                $size = ($all | Where-Object { -not $_.PSIsContainer } | Measure-Object -Property Length -Sum).Sum
                Write-Host "找到 $($all.Count) 个项目，大小约 $(Format-Size $size)" -ForegroundColor Yellow
                if (Confirm-Action "确认彻底删除") {
                    Get-ChildItem -Path $global:targetPath -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Host "`n已清空目录。" -ForegroundColor Green
                }
                Return-MenuOrExit
                continue
            }

            "6" {  # 修改配置
                Set-Config
                $config = Read-Config
                if ($config) { $global:targetPath = $config.TargetPath; $global:logExe = $config.LogExe }
                continue
            }

            "1" {  # 解压
                Write-Host "`n提示：按 Ctrl+C 可中断当前操作" -ForegroundColor Yellow
                $oldTreat = [Console]::TreatControlCAsInput
                [Console]::TreatControlCAsInput = $false
                try {
                    if (-not (Test-Path $global:logExe)) { Write-Host "错误：未找到 $global:logExe" -ForegroundColor Red; Return-MenuOrExit; continue }
                    if (-not (Test-Path $global:targetPath)) { Write-Host "错误：目录不存在" -ForegroundColor Red; Return-MenuOrExit; continue }

                    $details = Get-LogFileDetails $global:targetPath
                    # 强制数组化，确保 Count 属性始终存在
                    $details = @($details)
                    $total = $details.Count
                    if ($total -eq 0) {
                        Write-Host "`n没有找到 .tgz、.temp 或 .zip 文件。" -ForegroundColor Green
                        Return-MenuOrExit; continue
                    }

                    $extractedCount = ($details | Where-Object { $_.IsExtracted }).Count
                    $unExtractedCount = $total - $extractedCount

                    Write-Host "`n找到 $total 个日志文件（含根目录及一级子目录）" -ForegroundColor Cyan
                    Write-Host "其中已解压: $extractedCount，未解压: $unExtractedCount" -ForegroundColor Cyan
                    foreach ($it in $details) {
                        $status = if ($it.IsExtracted) { "[已解压]" } else { "[未解压]" }
                        $color = if ($it.IsExtracted) { "Green" } else { "Yellow" }
                        $rel = if ($it.Directory -eq $global:targetPath) { $it.FileName } else { "$(Split-Path $it.Directory -Leaf)\$($it.FileName)" }
                        Write-Host "  $status $rel" -ForegroundColor $color
                    }

                    $toProcess = @()
                    if ($unExtractedCount -eq 0) {
                        Write-Host "`n所有文件已解压。" -ForegroundColor Green
                        if (Confirm-Action "强制重新解压所有文件（将覆盖现有解压目录）") { $toProcess = $details.File }
                    } else {
                        Write-Host "`n建议只解压未解压的文件（$unExtractedCount 个）。" -ForegroundColor Cyan
                        if (Confirm-Action "只解压未解压") {
                            $toProcess = $details | Where-Object { -not $_.IsExtracted } | ForEach-Object { $_.File }
                        } elseif (Confirm-Action "强制重新解压所有文件（将覆盖现有解压目录）") {
                            $toProcess = $details.File
                        }
                    }

                    if ($toProcess.Count -gt 0) {
                        Write-Host "`n即将解压 $($toProcess.Count) 个文件：" -ForegroundColor Cyan
                        foreach ($f in $toProcess) { Write-Host "  $($f.Name)" }
                        Write-Host "`n开始解压..." -ForegroundColor Cyan

                        $zipFiles = $toProcess | Where-Object { $_.Extension -eq ".zip" }
                        $tgzFiles = $toProcess | Where-Object { $_.Extension -match "\.(tgz|temp)$" }

                        $failed = @()
                        if ($tgzFiles) { $failed += Invoke-ExtractTgz $tgzFiles $global:logExe }
                        if ($zipFiles) { $failed += Invoke-ExtractZip $zipFiles }

                        $failed = $failed | Where-Object { $_ -is [System.IO.FileInfo] }
                        $successFiles = $toProcess | Where-Object { $_.FullName -notin $failed.FullName }
                        $successPaths = $successFiles.FullName

                        Write-Host "`n解压结果：成功 $($successFiles.Count) 个，失败 $($failed.Count) 个。" -ForegroundColor Cyan

                        if ($successFiles) {
                            $reportList = @()
                            foreach ($f in $successFiles) {
                                if ($f.Extension -match "\.(tgz|temp)$") {
                                    $base = Get-NameWithoutExtensions $f.Name
                                    $expectedDir = Join-Path $f.DirectoryName $base
                                    if (Test-Path (Join-Path $expectedDir "report\report.html")) {
                                        $reportList += [PSCustomObject]@{ ExpectedDir = $expectedDir }
                                    }
                                }
                            }
                            if ($reportList) {
                                Write-Host "`n找到以下报告文件（仅针对本次解压的 .tgz/.temp 文件）：" -ForegroundColor Cyan
                                foreach ($item in $reportList) {
                                    Write-Host "  $(Join-Path $item.ExpectedDir "report\report.html")" -ForegroundColor Gray
                                }
                                if (Confirm-Action "是否打开这些报告文件？") {
                                    foreach ($item in $reportList) {
                                        $p = Join-Path $item.ExpectedDir "report\report.html"
                                        try { Start-Process $p; Write-Host "已打开：$p" -ForegroundColor Green } catch { Write-Host "打开失败：$p" -ForegroundColor Red }
                                    }
                                }
                            } else {
                                $hasTgz = $successFiles.Where({$_.Extension -match "\.(tgz|temp)$"}).Count -gt 0
                                if ($hasTgz) { Write-Host "未找到报告文件（report.html 不存在）。" -ForegroundColor Yellow }
                                else { Write-Host "本次解压不包含 .tgz/.temp 文件，无需检测报告。" -ForegroundColor Yellow }
                            }
                        }

                        if ($failed) {
                            Write-Host "`n以下文件解压失败：" -ForegroundColor Red
                            foreach ($f in $failed) { Write-Host "  - $($f.Name)" -ForegroundColor Red }
                            if (Confirm-Action "删除失败的压缩包及可能的不完整解压目录") {
                                $failItems = $details | Where-Object { $_.Path -in $failed.FullName }
                                Invoke-Cleanup $failItems "即将删除失败的压缩包及其解压目录："
                            }
                        }
                    }

                    # 全局清理
                    Write-Host "`n---------- 清理旧解压文件 ----------" -ForegroundColor Magenta
                    if (Confirm-Action "是否清理已解压的旧压缩包及目录？（本次解压的除外）") {
                        $extractedItems = $details | Where-Object { $_.IsExtracted }
                        Invoke-Cleanup $extractedItems "找到以下已解压项（将保留本次解压文件）：" -excludePaths $successPaths
                    }
                } finally {
                    [Console]::TreatControlCAsInput = $oldTreat
                }
                Return-MenuOrExit
                continue
            }

            default { Write-Host "无效输入。" -ForegroundColor Red; Read-Host "按 Enter 继续" }
        }
    } catch {
        if ($_.Exception.Message -match "被用户中断") {
            Write-Host "`n操作已被用户中断。" -ForegroundColor Yellow
        } else {
            Handle-Error "发生意外错误：$($_.Exception.Message)" -Exception $_
        }
        Read-Host "按 Enter 继续"
        continue
    }
}
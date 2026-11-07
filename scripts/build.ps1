<#
GreenSite 一键构建流水线（Windows PowerShell 5.1）。
步骤：前置断言 -> 版本号 -> LICENSE.rtf -> build -> publish -> 组装 -> 自检 -> 体检 -> ISCC -> 复检。
所有路径基于 $PSScriptRoot\..，不依赖当前工作目录；脚本内不含任何凭据。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scriptsDir = $PSScriptRoot
$versionIni = Join-Path $root '.version.ini'
$publishDir = Join-Path $root 'outputs\GreenSite'
$distDir = Join-Path $root 'dist'
$issFile = Join-Path $root 'src\setup.iss'
$slnFile = Join-Path $root 'GreenSite.slnx'
$appProj = Join-Path $root 'src\GreenSite\GreenSite.csproj'

# ISCC 默认位于仓库上一级目录的 tools 下，可用 GREENSITE_ISCC 覆盖
$toolsRoot = Split-Path $root -Parent
$iscc = if ($env:GREENSITE_ISCC) { $env:GREENSITE_ISCC } else { Join-Path $toolsRoot 'tools\innosetup\ISCC.exe' }
$psExe = Join-Path $PSHOME 'powershell.exe'

function Step([string]$Message) { Write-Host "[build] $Message" }

function Assert-Native([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What 失败（退出码 $LASTEXITCODE）" }
}

function Invoke-Check([string]$Name, [string]$Script) {
    # 子脚本用 exit 0/1 报告结果，放到独立 powershell 进程执行：
    # 既避免子脚本的 exit 影响本脚本后续步骤，又可用 $LASTEXITCODE 明确判定。
    & $psExe -NoProfile -ExecutionPolicy Bypass -File $Script
    if ($LASTEXITCODE -ne 0) { throw "$Name 失败（退出码 $LASTEXITCODE）" }
}

try {
    # 0. 归档上次构建的安装包与校验文件（移动而非删除，保留交付历史）：
    #    只读体检会把 dist 根目录里最新的安装包当作本轮产物，残留旧包导致 mtime 误判并中止发布。
    $staleSetups = @(Get-ChildItem -LiteralPath $distDir -Filter 'GreenSite-Setup-*' -File -ErrorAction SilentlyContinue)
    if ($staleSetups.Count -gt 0) {
        $archiveDir = Join-Path $distDir 'archive'
        New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
        Step "归档 $($staleSetups.Count) 个旧安装包/校验文件 -> dist\archive"
        foreach ($stale in $staleSetups) {
            Move-Item -LiteralPath $stale.FullName -Destination (Join-Path $archiveDir $stale.Name) -Force
        }
    }

    # 1. 前置断言：dotnet 10 + ISCC.exe
    Step '前置断言：dotnet 10 / ISCC'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet，请安装 .NET 10 SDK（含 WindowsDesktop）'
    }
    $dotnetOut = & dotnet --version
    Assert-Native 'dotnet --version'
    $dotnetVersion = ([string]($dotnetOut | Where-Object { $_ -match '^\s*10\.' } | Select-Object -First 1)).Trim()
    if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
        throw "dotnet --version 必须以 10 开头，实际输出：$($dotnetOut -join ' | ')"
    }
    Step "dotnet 版本：$dotnetVersion"
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) {
        throw "未找到 ISCC.exe：$iscc（可设置 GREENSITE_ISCC 覆盖）"
    }
    Step "ISCC：$iscc"

    # 2. 生成版本号并解析 .version.ini（纯文本逐行解析，不用 ConvertFrom-Json）
    Step '生成版本号 .version.ini'
    & (Join-Path $scriptsDir 'gen_version.ps1')
    $values = @{}
    foreach ($line in (Get-Content -LiteralPath $versionIni -Encoding UTF8)) {
        if ($line.Trim() -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $values[$Matches[1]] = $Matches[2]
        }
    }
    $full = $values['full']
    $num = $values['num']
    $platform = $values['platform']
    $channel = $values['channel']
    $semver = $values['semver']
    if ([string]::IsNullOrWhiteSpace($full) -or [string]::IsNullOrWhiteSpace($num)) {
        throw ".version.ini 缺少 full / num：$versionIni"
    }
    Step "版本：full=$full  num=$num"

    # 3. 生成安装器用的 RTF 许可证
    Step '生成 LICENSE.rtf'
    & (Join-Path $scriptsDir 'gen_license_rtf.ps1')

    # 4. 编译整个解决方案
    Step 'dotnet build GreenSite.slnx -c Release'
    & dotnet build $slnFile -c Release --nologo -v minimal
    Assert-Native 'dotnet build'

    # 5. 框架依赖发布，注入程序集版本与 PE 版本资源
    Step 'dotnet publish (win-x64, framework-dependent)'
    $publishArgs = @(
        'publish', $appProj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-o', $publishDir,
        "-p:Version=$num",
        "-p:FileVersion=$num",
        "-p:AssemblyVersion=$num",
        "-p:InformationalVersion=$full"
    )
    & dotnet @publishArgs
    Assert-Native 'dotnet publish'

    # 6. 组装 outputs/GreenSite/
    Step '组装 outputs/GreenSite'
    if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
        throw "发布目录不存在：$publishDir"
    }
    foreach ($f in @('LICENSE', 'NOTICE', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $root $f) -Destination $publishDir -Force
    }
    $tplSrc = Join-Path $root 'THIRD_PARTY_LICENSES'
    $tplDest = Join-Path $publishDir 'THIRD_PARTY_LICENSES'
    New-Item -ItemType Directory -Path $tplDest -Force | Out-Null
    Copy-Item -Path (Join-Path $tplSrc '*') -Destination $tplDest -Recurse -Force

    $dataDest = Join-Path $publishDir 'data'
    New-Item -ItemType Directory -Path $dataDest -Force | Out-Null
    Copy-Item -Path (Join-Path $root 'src\GreenSite\data\*') -Destination $dataDest -Recurse -Force

    $assetsDest = Join-Path $publishDir 'assets'
    New-Item -ItemType Directory -Path $assetsDest -Force | Out-Null
    foreach ($f in @('logo.svg', 'logo.png', 'app.ico')) {
        Copy-Item -LiteralPath (Join-Path $root "src\GreenSite\assets\$f") -Destination (Join-Path $assetsDest $f) -Force
    }

    # UTF-8 无 BOM（PS 5.1 的 Out-File/重定向会写 UTF-16LE，必须显式指定）
    $outIni = "[version]`nfull=$full`nnum=$num`nplatform=$platform`nchannel=$channel`nsemver=$semver`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $publishDir 'version.ini'), $outIni, $utf8NoBom)

    # 7. 自检（失败即中止，不发布）
    Step '运行自检 selftest.ps1'
    Invoke-Check 'selftest.ps1' (Join-Path $scriptsDir 'selftest.ps1')

    # 8. 打包前只读体检
    Step '运行只读体检 verify_product.ps1（打包前）'
    Invoke-Check 'verify_product.ps1' (Join-Path $scriptsDir 'verify_product.ps1')

    # 9. ISCC 编译安装包（PowerShell 直接调用，不需要 MSYS_NO_PATHCONV）
    Step 'ISCC 编译 src/setup.iss'
    Push-Location $root
    try {
        & $iscc '/Q' '/Odist' $issFile
        $isccCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($isccCode -ne 0) { throw "ISCC 编译失败（退出码 $isccCode）" }

    # 10. 校验安装包存在，再复检一次
    $setupName = "GreenSite-Setup-$full.exe"
    $setupExe = Join-Path $distDir $setupName
    if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
        # 少数环境把 /Odist 解析为相对脚本目录（src\dist），兜底回捞一次
        $fallback = Join-Path $root "src\dist\$setupName"
        if (Test-Path -LiteralPath $fallback -PathType Leaf) {
            Copy-Item -LiteralPath $fallback -Destination $setupExe -Force
        }
    }
    if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
        throw "未生成安装包：$setupExe"
    }
    Step '运行只读体检 verify_product.ps1（打包后）'
    Invoke-Check 'verify_product.ps1' (Join-Path $scriptsDir 'verify_product.ps1')

    $item = Get-Item -LiteralPath $setupExe
    Step "产物：$($item.FullName)"
    Step "大小：$($item.Length) 字节（$([math]::Round($item.Length / 1MB, 2)) MB）"
    Step '构建完成'
}
catch {
    Write-Host "[build] 失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
exit 0

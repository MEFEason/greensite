<#
GreenSite 产物只读体检：只检查，绝不修改 / 删除 / 创建任何文件。
退出码：全部通过 0；任一 [FAIL] 则 1。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appDir = Join-Path $root 'outputs\GreenSite'
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$scriptsDir = $PSScriptRoot

$failures = 0

function Report([bool]$Ok, [string]$Name, [string]$Reason) {
    if ($Ok) {
        Write-Host "[OK]   $Name" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $Name — $Reason" -ForegroundColor Red
        $script:failures++
    }
}

$exe = Join-Path $appDir 'GreenSite.exe'
$exeItem = Get-Item -LiteralPath $exe -ErrorAction SilentlyContinue

# 1. 主程序
Report ($null -ne $exeItem) 'outputs/GreenSite/GreenSite.exe 存在' "未找到 $exe"

# 2. runtimeconfig 引用 WindowsDesktop 框架
$rtCfg = Join-Path $appDir 'GreenSite.runtimeconfig.json'
$rtOk = $false
if (Test-Path -LiteralPath $rtCfg -PathType Leaf) {
    $rtOk = (Get-Content -LiteralPath $rtCfg -Raw -Encoding UTF8) -match 'Microsoft\.WindowsDesktop\.App'
}
Report $rtOk 'GreenSite.runtimeconfig.json 含 Microsoft.WindowsDesktop.App' '文件缺失或未引用 WindowsDesktop 框架'

# 3. 图标与默认清单
Report (Test-Path -LiteralPath (Join-Path $appDir 'assets\app.ico') -PathType Leaf) `
    'assets/app.ico 存在' 'outputs/GreenSite/assets/app.ico 缺失'
foreach ($f in @('default_blocklist.txt', 'default_allowlist.txt')) {
    Report (Test-Path -LiteralPath (Join-Path $appDir "data\$f") -PathType Leaf) `
        "data/$f 存在" "outputs/GreenSite/data/$f 缺失"
}

# 4. version.ini 且 full 非空
$vIni = Join-Path $appDir 'version.ini'
$vOk = $false
if (Test-Path -LiteralPath $vIni -PathType Leaf) {
    $vOk = (Get-Content -LiteralPath $vIni -Raw -Encoding UTF8) -match '(?m)^full=\s*\S+'
}
Report $vOk 'version.ini 存在且 full 非空' '文件缺失或 full 为空'

# 5. PE 版本资源（FileVersion / ProductVersion）
if ($null -ne $exeItem) {
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $peOk = (-not [string]::IsNullOrWhiteSpace($vi.FileVersion)) -and
            (-not [string]::IsNullOrWhiteSpace($vi.ProductVersion))
    Report $peOk 'PE 版本资源 FileVersion/ProductVersion 非空' `
        "FileVersion='$($vi.FileVersion)' ProductVersion='$($vi.ProductVersion)'"
} else {
    Report $false 'PE 版本资源 FileVersion/ProductVersion 非空' 'GreenSite.exe 不存在，无法读取'
}

# 6. 所有 scripts/*.ps1 必须以 UTF-8 BOM 开头；LICENSE.rtf 存在
$psFiles = @(Get-ChildItem -LiteralPath $scriptsDir -Filter '*.ps1' -File)
$noBom = @()
foreach ($p in $psFiles) {
    $bytes = [System.IO.File]::ReadAllBytes($p.FullName)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        $noBom += $p.Name
    }
}
Report ($noBom.Count -eq 0) "scripts/*.ps1 全部 UTF-8 BOM（共 $($psFiles.Count) 个）" `
    "缺少 BOM：$($noBom -join ', ')"
Report (Test-Path -LiteralPath (Join-Path $root 'LICENSE.rtf') -PathType Leaf) `
    '根目录 LICENSE.rtf 存在' 'LICENSE.rtf 缺失'

# 7. 发布目录内不得夹带私钥 / 证书 / 加密配置
$forbidden = @()
if (Test-Path -LiteralPath $appDir -PathType Container) {
    $forbidden = @(Get-ChildItem -LiteralPath $appDir -Recurse -File | Where-Object {
        $_.Extension -in @('.pfx', '.key') -or $_.Name -eq 'config.bin'
    } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
}
Report ($forbidden.Count -eq 0) 'outputs/GreenSite 内无 *.pfx / *.key / config.bin' `
    "发现敏感文件：$($forbidden -join ', ')"

# 8. 产物 mtime 不早于 src/**/*.{cs,xaml} 的最大 mtime（允许 2 秒误差）
$srcFiles = @(Get-ChildItem -LiteralPath $srcDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.cs', '.xaml') })
if ($null -ne $exeItem -and $srcFiles.Count -gt 0) {
    $maxSrc = $srcFiles | ForEach-Object { $_.LastWriteTime } | Sort-Object | Select-Object -Last 1
    $mOk = $exeItem.LastWriteTime -ge $maxSrc.AddSeconds(-2)
    Report $mOk '产物 mtime 不早于 src 源文件最大 mtime（±2s）' `
        "产物=$($exeItem.LastWriteTime) 源码=$maxSrc"
} else {
    Report ($null -ne $exeItem) '产物 mtime 检查' '无 .cs/.xaml 源文件或主程序缺失'
}

# 9. 安装包存在时，其 mtime 应晚于主程序（±2s 容差，规避同秒时间戳）
$setups = @()
if (Test-Path -LiteralPath $distDir -PathType Container) {
    $setups = @(Get-ChildItem -LiteralPath $distDir -Filter 'GreenSite-Setup-*.exe' -File)
}
if ($setups.Count -eq 0) {
    Write-Host '[SKIP] dist/GreenSite-Setup-*.exe 不存在（打包前体检时正常）'
} elseif ($null -ne $exeItem) {
    $setup = $setups | Sort-Object LastWriteTime | Select-Object -Last 1
    $sOk = $setup.LastWriteTime -gt $exeItem.LastWriteTime.AddSeconds(-2)
    Report $sOk 'dist 安装包 mtime 晚于 GreenSite.exe（±2s）' `
        "安装包=$($setup.LastWriteTime) 主程序=$($exeItem.LastWriteTime)"
} else {
    Report $false 'dist 安装包 mtime 晚于 GreenSite.exe（±2s）' '主程序缺失，无法比较'
}

# 10. 卸载门禁静态检查：安装器必须在 InitializeUninstall 里验证管理密码，
#     失败分支须 MsgBox 提示并终止，防止未来误删导致无密码即可卸载。
$iss = Join-Path $srcDir 'setup.iss'
$issOk = $false
if (Test-Path -LiteralPath $iss -PathType Leaf) {
    $issText = Get-Content -LiteralPath $iss -Raw -Encoding UTF8
    $issOk = ($issText -match 'function InitializeUninstall') -and
             ($issText -match '--verify-uninstall') -and
             ($issText -match 'ResultCode <> 0') -and
             ($issText -match 'MsgBox\(') -and
             ($issText -match 'Result := False')
}
Report $issOk 'setup.iss 含卸载管理密码门禁（失败即终止 + MsgBox）' `
    '缺少 InitializeUninstall / 验证调用 / 失败分支'

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[verify] 共 $($script:failures) 项未通过" -ForegroundColor Red
    exit 1
}
Write-Host '[verify] 全部检查通过' -ForegroundColor Green
exit 0

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
    Where-Object { $_.Extension -in @('.cs', '.xaml') -and $_.FullName -notmatch '\\(obj|bin)\\' })
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

# 11. 卸载验证窗口布局：固定 Height 会在高 DPI / 文案换行时裁掉底部按钮，
#     必须自适应高度且按钮固定在 DockPanel 底部（v1.0.6 修复）。
$guardXaml = Join-Path $srcDir 'GreenSite\Views\UninstallGuardWindow.xaml'
$guardOk = $false
if (Test-Path -LiteralPath $guardXaml -PathType Leaf) {
    $guardText = Get-Content -LiteralPath $guardXaml -Raw -Encoding UTF8
    $guardOk = ($guardText -match 'SizeToContent="Height"') -and
               ($guardText -match 'DockPanel.Dock="Bottom"') -and
               ($guardText -match 'x:Name="OkButton"') -and
               ($guardText -match 'x:Name="CancelButton"')
}
Report $guardOk '卸载验证窗口按钮完整可见（自适应高度 + 底部固定）' `
    '缺少 SizeToContent / DockPanel 底部按钮，可能重现按钮裁切'

# 12. 主界面状态栏快捷开关：左侧状态栏与状态总览卡片都必须有按钮并接事件。
$mainXaml = Join-Path $srcDir 'GreenSite\MainWindow.xaml'
$dashXaml = Join-Path $srcDir 'GreenSite\Views\DashboardPage.xaml'
$toggleOk = $false
if ((Test-Path -LiteralPath $mainXaml -PathType Leaf) -and
    (Test-Path -LiteralPath $dashXaml -PathType Leaf)) {
    $mainText = Get-Content -LiteralPath $mainXaml -Raw -Encoding UTF8
    $dashText = Get-Content -LiteralPath $dashXaml -Raw -Encoding UTF8
    $toggleOk = ($mainText -match 'x:Name="ProtectionToggleButton"') -and
                ($mainText -match 'Click="ProtectionToggle_Click"') -and
                ($dashText -match 'x:Name="ProtectionToggleButton"') -and
                ($dashText -match 'Click="ProtectionToggle_Click"')
}
Report $toggleOk '主界面状态栏含防护快捷开关（状态栏 + 状态总览卡片）' `
    '缺少 ProtectionToggleButton 或其事件接线'

# 13. v1.1.0：守护程序产物与安装器配置选项静态检查。
$guardExe = Join-Path $root 'outputs\GreenSite\Guard.exe'
Report (Test-Path -LiteralPath $guardExe -PathType Leaf) `
    'Guard.exe 守护进程/服务已随产物交付' 'outputs/GreenSite/Guard.exe 缺失'
$issTextV11 = if (Test-Path -LiteralPath $iss -PathType Leaf) {
    Get-Content -LiteralPath $iss -Raw -Encoding UTF8
} else { '' }
$issV11Ok = ($issTextV11 -match '覆盖已有用户配置') -and
            ($issTextV11 -match '是否同时删除本机用户配置') -and
            ($issTextV11 -match 'GreenSiteGuard')
Report $issV11Ok 'setup.iss 含安装覆盖/卸载删除配置选项与守护服务' `
    '缺少配置选项或守护服务安装逻辑'
$coreDllV11 = Join-Path $root 'outputs\GreenSite\GreenSite.Core.dll'
$appDllV11 = Join-Path $root 'outputs\GreenSite\GreenSite.dll'
if ((Test-Path -LiteralPath $coreDllV11 -PathType Leaf) -and
    (Test-Path -LiteralPath $appDllV11 -PathType Leaf)) {
    $coreBytes = [System.IO.File]::ReadAllBytes($coreDllV11)
    $appBytes = [System.IO.File]::ReadAllBytes($appDllV11)
    $coreText = [System.Text.Encoding]::ASCII.GetString($coreBytes)
    $appText = [System.Text.Encoding]::ASCII.GetString($appBytes)
    $typesOk = ($coreText -match 'AdvancedOptions') -and
               ($coreText -match 'EncryptedBackupStore') -and
               ($coreText -match 'ShieldScheduleEvaluator') -and
               ($coreText -match 'WatchdogService') -and
               ($appText -match 'OpenGitHub_Click')
    Report $typesOk 'v1.1.0 新类型与 GitHub 入口已编入产物' `
        '产物 DLL 缺少 v1.1.0 新类型/事件'
} else {
    Report $false 'v1.1.0 产物 DLL 存在' '无法检查产物 DLL'
}

# 14. v1.1.2：LocalSystem 服务单一高权限守护（只读静态检查，不启动进程 / 不读真实注册表）。
#     .NET 元数据名（常量/方法/P-Invoke）走 #Strings 堆可用 ASCII 解码；
#     字符串常量走 #US 堆为 UTF-16LE，必须用 Encoding.Unicode 才能命中。
function Test-BinaryHasText([byte[]]$Bytes, [string]$Needle) {
    if ($null -eq $Bytes) { return $false }
    $asciiText = [System.Text.Encoding]::ASCII.GetString($Bytes)
    $unicodeText = [System.Text.Encoding]::Unicode.GetString($Bytes)
    return ($asciiText.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) -or
           ($unicodeText.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0)
}

$coreDllV112 = Join-Path $root 'outputs\GreenSite\GreenSite.Core.dll'
$coreBytesV112 = if (Test-Path -LiteralPath $coreDllV112 -PathType Leaf) {
    [System.IO.File]::ReadAllBytes($coreDllV112)
} else { $null }
$coreV112Ok = (Test-BinaryHasText $coreBytesV112 'ServiceUserDataParam') -and
              (Test-BinaryHasText $coreBytesV112 'DisableFlagPath')
Report $coreV112Ok 'v1.1.2 Core.dll 含服务参数常量与守护关闭标记路径方法' `
    'ASCII/Unicode 解码均缺少 ServiceUserDataParam 或 DisableFlagPath'

# 注意：Guard.exe 只是 apphost，IL 与 P/Invoke 名在 Guard.dll；优先检查 DLL，缺失时回退 exe。
$guardDllV112 = Join-Path $root 'outputs\GreenSite\Guard.dll'
$guardExeV112 = Join-Path $root 'outputs\GreenSite\Guard.exe'
$guardBytesV112 = if (Test-Path -LiteralPath $guardDllV112 -PathType Leaf) {
    [System.IO.File]::ReadAllBytes($guardDllV112)
} elseif (Test-Path -LiteralPath $guardExeV112 -PathType Leaf) {
    [System.IO.File]::ReadAllBytes($guardExeV112)
} else { $null }
$guardV112Ok = (Test-BinaryHasText $guardBytesV112 'WTSQueryUserToken') -and
               (Test-BinaryHasText $guardBytesV112 'CreateProcessAsUser')
Report $guardV112Ok 'v1.1.2 Guard.dll 含跨会话拉起 API（WTSQueryUserToken/CreateProcessAsUser）' `
    'ASCII/Unicode 解码均缺少 WTSQueryUserToken 或 CreateProcessAsUser'

# 托盘源码：Guard/Program.cs 文本不得出现退出守护菜单（普通用户不能结束高权限守护）。
$guardSrcV112 = Join-Path $srcDir 'GreenSite.Guard\Program.cs'
$guardSrcTextV112 = if (Test-Path -LiteralPath $guardSrcV112 -PathType Leaf) {
    Get-Content -LiteralPath $guardSrcV112 -Raw -Encoding UTF8
} else { '' }
$guardTrayOkV112 = ($guardSrcTextV112.Length -gt 0) -and
                   ($guardSrcTextV112.IndexOf('ToolStripMenuItem("退出守护"',
                        [System.StringComparison]::Ordinal) -lt 0) -and
                   ($guardSrcTextV112.IndexOf('退出管理员守护',
                        [System.StringComparison]::Ordinal) -lt 0)
Report $guardTrayOkV112 'v1.1.2 Guard 托盘源码无退出守护菜单' `
    'Guard/Program.cs 缺失或仍含退出守护 / 退出管理员守护菜单'

# 设置页：Watchdog_Changed 方法体必须走密码门禁，且整体不含 v1.1.1 提权开关。
$settingsCsV112 = Join-Path $srcDir 'GreenSite\Views\SettingsPage.xaml.cs'
$settingsTextV112 = if (Test-Path -LiteralPath $settingsCsV112 -PathType Leaf) {
    Get-Content -LiteralPath $settingsCsV112 -Raw -Encoding UTF8
} else { '' }
$watchdogSectionV112 = ''
$wdStartV112 = $settingsTextV112.IndexOf('private void Watchdog_Changed', [System.StringComparison]::Ordinal)
if ($wdStartV112 -ge 0) {
    $wdOpenV112 = $settingsTextV112.IndexOf([char]'{', $wdStartV112)
    if ($wdOpenV112 -ge 0) {
        $wdDepthV112 = 0
        for ($i = $wdOpenV112; $i -lt $settingsTextV112.Length; $i++) {
            if ($settingsTextV112[$i] -eq [char]'{') { $wdDepthV112++ }
            elseif ($settingsTextV112[$i] -eq [char]'}') {
                $wdDepthV112--
                if ($wdDepthV112 -eq 0) {
                    $watchdogSectionV112 = $settingsTextV112.Substring($wdStartV112, $i - $wdStartV112 + 1)
                    break
                }
            }
        }
    }
}
$settingsV112Ok = ($watchdogSectionV112.IndexOf('RequirePassword',
                       [System.StringComparison]::Ordinal) -ge 0) -and
                  ($settingsTextV112.IndexOf('ElevatedWatchdogCheck',
                       [System.StringComparison]::Ordinal) -lt 0)
Report $settingsV112Ok 'v1.1.2 设置页守护开关加密码门禁且无提权开关' `
    'Watchdog_Changed 缺少 RequirePassword，或源码仍含 ElevatedWatchdogCheck'

# 安装器：服务需从注册表读取真实用户数据目录。
$issTextV112 = if (Test-Path -LiteralPath $iss -PathType Leaf) {
    Get-Content -LiteralPath $iss -Raw -Encoding UTF8
} else { '' }
$issV112Ok = ($issTextV112.IndexOf('UserDataDir', [System.StringComparison]::Ordinal) -ge 0) -and
             ($issTextV112.IndexOf('RegWriteStringValue', [System.StringComparison]::Ordinal) -ge 0)
Report $issV112Ok 'v1.1.2 setup.iss 写入 UserDataDir 供服务读取' `
    '缺少 UserDataDir 或 RegWriteStringValue'

# 15. Guard.exe 必须是 GUI 子系统：OutputType=Exe 会生成 Console 子系统，
#     启动守护时伴随一个 cmd 窗口（v1.1.1 beta 实测问题），改为 WinExe 后 PE Subsystem=2。
$guardPe = Join-Path $appDir 'Guard.exe'
$guardSubsystem = $null
if (Test-Path -LiteralPath $guardPe -PathType Leaf) {
    $peBytes = [System.IO.File]::ReadAllBytes($guardPe)
    if ($peBytes.Length -gt 0x40) {
        $peOffset = [System.BitConverter]::ToInt32($peBytes, 0x3C)
        if ($peOffset -gt 0 -and ($peOffset + 0x50) -lt $peBytes.Length) {
            $guardSubsystem = [System.BitConverter]::ToUInt16($peBytes, $peOffset + 24 + 68)
        }
    }
}
Report ($guardSubsystem -eq 2) 'Guard.exe 为 GUI 子系统（启动不弹控制台窗口）' `
    "PE Subsystem=$guardSubsystem（2=GUI；3=Console 会弹 cmd 窗口）"

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[verify] 共 $($script:failures) 项未通过" -ForegroundColor Red
    exit 1
}
Write-Host '[verify] 全部检查通过' -ForegroundColor Green
exit 0

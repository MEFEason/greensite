<#
GreenSite 版本号唯一来源。规范与 Translator 完全一致：

  full = {platform}-{channel}-v{semver}-{YYYY}.{MMDD}.{HH}.{mm}
  例   windows-beta-v0.9.1-2026.1107.09.20
       windows-release-v1.0.0-2026.1107.09.20

  - platform 全小写；GreenSite 目前仅 windows
  - channel 白名单 7 种：snapshot/alpha/beta/pre/release/stable/current
  - 语义版本递进：正式版大迭代改动全部数字（v1.0.0、v2.0.0）；
    测试版只改后面的数字且前段恒为 0（0.9.1 → 0.9.8 → 0.10.56）；
    上一级变动时下级归零
  - 4 位年；不加 build 字样；不带时间戳括号；关于页只显示 full 本身
  - num 为 PE 版本资源专用的四段纯数字：主.次.修订.MMDD
    （同日重复构建靠 full 区分）
  - 时间只取一次，避免编译耗时导致 about / 安装目录 / PE 资源对不上

产物：仓库根目录 .version.ini（不入库，供 build.ps1 与 src/setup.iss 读取）
      [version] full= / platform= / channel= / semver= / num=

用法：
  $env:GREENSITE_SEMVER='0.9.1'; $env:GREENSITE_CHANNEL='beta'; .\scripts\gen_version.ps1
  $env:GREENSITE_SEMVER='1.0.0'; $env:GREENSITE_CHANNEL='release'; .\scripts\gen_version.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\.version.ini')
)

$ErrorActionPreference = 'Stop'

# 时间只取一次：可用 GREENSITE_BUILD_TIME 复现构建（格式 yyyy-MM-dd HH:mm:ss）
if ($env:GREENSITE_BUILD_TIME) {
    $now = [datetime]::ParseExact($env:GREENSITE_BUILD_TIME, 'yyyy-MM-dd HH:mm:ss',
        [System.Globalization.CultureInfo]::InvariantCulture)
} else {
    $now = Get-Date
}

$platform = if ($env:GREENSITE_PLATFORM) { $env:GREENSITE_PLATFORM.ToLowerInvariant() } else { 'windows' }
$channel = if ($env:GREENSITE_CHANNEL) { $env:GREENSITE_CHANNEL.ToLowerInvariant() } else { 'snapshot' }
$semver = if ($env:GREENSITE_SEMVER) { $env:GREENSITE_SEMVER } else { '0.0.0' }

$platformWhitelist = @('windows', 'android')
$channelWhitelist = @('snapshot', 'alpha', 'beta', 'pre', 'release', 'stable', 'current')
$releaseChannels = @('release', 'stable', 'current')

if ($platform -notin $platformWhitelist) { throw "端标识非法: $platform（仅允许 $($platformWhitelist -join '/')）" }
if ($channel -notin $channelWhitelist) { throw "渠道非法: $channel（仅允许 $($channelWhitelist -join '/')）" }
if ($semver -notmatch '^\d+\.\d+\.\d+$') { throw "语义版本非法: $semver（应为 主.次.修订，如 0.9.1）" }
if ($platform -ne 'windows') { Write-Warning "GreenSite 目前仅发布 windows 端，得到 $platform" }
if ($channel -notin $releaseChannels -and $semver -notmatch '^0\.') {
    Write-Warning "测试版语义版本前段应为 0（用户规定的递进规则），当前为 $semver"
}

$vTime = $now.ToString('yyyy.MMdd.HH.mm', [System.Globalization.CultureInfo]::InvariantCulture)
$full = "$platform-$channel-v$semver-$vTime"
$num = "$semver." + $now.ToString('MMdd', [System.Globalization.CultureInfo]::InvariantCulture)

# 显式 UTF-8 无 BOM：Inno 的 ReadIni 与 LoadStringsFromFile 按 UTF-8 读取
$ini = "[version]`nfull=$full`nplatform=$platform`nchannel=$channel`nsemver=$semver`nnum=$num`n"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $ini, $utf8NoBom)

Write-Host "版本: $full"
Write-Host "PE  : $num"
Write-Host "写入: $OutputPath"

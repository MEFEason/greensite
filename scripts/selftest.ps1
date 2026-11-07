<#
GreenSite 自检入口：运行零 NuGet 测试框架的命令行自检项目。
build.ps1 已完成 Release 编译，默认使用 --no-build；若因未编译失败则自动回退。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$proj = Join-Path $root 'src\GreenSite.SelfTest\GreenSite.SelfTest.csproj'

Write-Host "[selftest] 项目：$proj"

$runArgs = @('run', '--project', $proj, '-c', 'Release', '--no-build')
Write-Host "[selftest] dotnet $($runArgs -join ' ')"
& dotnet @runArgs
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host "[selftest] --no-build 运行失败（退出码 $code），回退为自动构建后重试。" -ForegroundColor Yellow
    $runArgs = @('run', '--project', $proj, '-c', 'Release')
    Write-Host "[selftest] dotnet $($runArgs -join ' ')"
    & dotnet @runArgs
    $code = $LASTEXITCODE
}

if ($code -ne 0) {
    Write-Host "[selftest] 自检失败（退出码 $code）" -ForegroundColor Red
    exit 1
}

Write-Host '[selftest] 自检通过'
exit 0

<#
把根目录 LICENSE（英文纯文本）转换为 Inno Setup 可显示的 RTF。
输出：根目录 LICENSE.rtf，UTF-8 无 BOM；相同输入重复运行结果一致。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $root 'LICENSE'
$dst = Join-Path $root 'LICENSE.rtf'

$raw = Get-Content -LiteralPath $src -Raw -Encoding UTF8

# 先统一换行，再转义 RTF 特殊字符，最后把换行转成 \par + 换行
$text = $raw.Replace("`r`n", "`n").Replace("`r", "`n")
$text = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$lines = $text -split "`n"
$body = ($lines | ForEach-Object { $_ + '\par' }) -join "`n"

$rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs20' + "`n" + $body + "`n}"

# PS 5.1 的 Out-File / 重定向会写 UTF-16LE，这里显式写 UTF-8 无 BOM
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($dst, $rtf, $utf8NoBom)

Write-Host "[license] 已生成：$dst"

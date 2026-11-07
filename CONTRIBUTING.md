# 贡献指南

## 提交前检查

- `dotnet build -c Release` 0 error
- `scripts/selftest.ps1` 全绿
- `scripts/verify_product.ps1`（只读体检）通过
- 未提交任何凭据：`*.pfx`、`*.key`、`config.bin`、`config.bak`、token、密码
- 未提交构建产物：`bin/`、`obj/`、`outputs/`、`dist/`
- 新增 `.ps1` 为 UTF-8 BOM；脚本写文件用 `UTF8Encoding($false)`
- 不修改用户 PATH、不在安装尾声手工写注册表键

## 代码约定

- 目标框架 `net10.0-windows`；Core 不引用 WPF，零 NuGet PackageReference
- WPF 与 WinForms 同时启用，`Application` / `MessageBox` 等类型用完全限定名
- 规则匹配：域名后缀逐层父域；**白名单优先于限制列表**
- 有效规则 = 组开关 ∧ 单条开关
- 日志只记 时间 / Host / 命中组 / 命中规则 / 是否白名单豁免，不记 URL 参数
- 密码只经 `SecureString`，用完清零；绝不写日志、配置或异常

## 提交信息

采用 `type(scope): 简述`，例如：

```
feat(rules): 支持分组开关与单条开关
fix(pac): 黑名单分支禁止 DIRECT 兜底
```

`type`：`feat` / `fix` / `docs` / `build` / `test` / `chore`。

## CLA

提交 PR 即视为同意：你的贡献按本项目当前许可证（FSL-1.1-ALv2）
授权，并为 2 年后的 Apache-2.0 转换与后续商业分支保留重新许可的可能。
请勿在 PR 中包含你无权开源的代码或素材。

## 安全漏洞

按 [SECURITY.md](SECURITY.md) 私下报告，勿开公开 Issue。

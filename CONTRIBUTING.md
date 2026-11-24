# 贡献指南

## 提交前检查

- `dotnet build -c Release` **0 error**；或跑完整流水线 `scripts/build.ps1`
- `scripts/selftest.ps1` **15 组用例 204 条全绿**
- `scripts/verify_product.ps1`（只读体检）通过
- 未提交任何凭据：`*.pfx`、`*.key`、`*.p12`、`*.pem`、`*.snk`、`config.bin`、`config.bak`、token、密码
- 未提交构建产物与运行期数据：`bin/`、`obj/`、`outputs/`、`dist/`、`.version.ini`、`*.log`
- 新增 `.ps1` 为 UTF-8 BOM；脚本写文件用 `UTF8Encoding($false)` 显式指定编码
- 不修改用户 PATH、不在安装尾声手工写注册表键
- 新增文件自保护 / 守护进程 / 加密备份逻辑时，必须同步补充自检断言（覆盖正常路径、边界与失败路径），并更新对应文档与手动验证清单
- 不得引入内核驱动、进程注入或隐藏启动等对抗性手段；自保护是用户级 ACL + 完整性校验，须如实标注管理员可绕过的边界，避免杀软误报
- 安装器涉及删除用户配置的选项必须先做路径安全校验（确认位于用户数据目录内、防路径穿越 / 符号链接误删），且默认保留用户配置与日志目录，不得静默删除

## 代码约定

- 目标框架 `net10.0-windows`；`GreenSite.Core` 不引用 WPF，**零 NuGet PackageReference**
- WPF 与 WinForms 同时启用，`Application` / `MessageBox` 等类型一律用完全限定名
- 规则匹配：域名后缀逐层父域；**白名单优先于限制列表**（PAC 中白名单先判断）
- **有效规则 = 组开关 ∧ 单条开关**（两级 AND）
- PAC 限制列表分支只走 `PROXY 127.0.0.1:<port>`，**禁止 `DIRECT` 兜底**；规则变更递增 `?v=<seq>`
- 日志脱敏：只记 时间 / Host / 命中组 / 命中规则 / `allow|block`，**不记 URL 与查询参数**
- 密码只经 `SecureString`，用完清零；绝不写入日志、配置或异常
- 配置校验顺序：header → DPAPI → 密码哈希 → HMAC；「密码错」与「配置被篡改」统一报错
- 配置异常**不提供无密码重置**，避免变成绕过口

## 提交信息

采用 `type(scope): 简述`，例如：

```
feat(rules): 支持分组开关与单条开关
fix(pac): 黑名单分支禁止 DIRECT 兜底
docs(readme): 补充本机 HTTP PAC 端点说明
```

`type`：`feat` / `fix` / `docs` / `build` / `test` / `chore`。

## CLA

提交 PR 即视为同意：你的贡献按本项目当前许可证（FSL-1.1-ALv2）
授权，并为 2 年后的 Apache-2.0 转换与后续商业分支保留重新许可的可能。
请勿在 PR 中包含你无权开源的代码或素材。

## 安全漏洞

按 [SECURITY.md](SECURITY.md) 私下报告，勿开公开 Issue，勿在报告中附带管理密码或 `config.bin`。

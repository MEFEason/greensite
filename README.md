# GreenSite — 网站访问控制程序

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-10-blue)
![License](https://img.shields.io/badge/license-FSL--1.1--ALv2-blue)
![Future License](https://img.shields.io/badge/converts%20to-Apache--2.0%20after%202%20years-green)

**单进程、用户级、零 UAC 的 Windows 网站访问控制工具**。用于限制抖音、斗鱼等短视频/直播网站访问；规则按分组管理，**白名单优先于限制列表**；管理密码保护启动、规则修改、关闭保护与退出。

- 拦截时弹托盘通知并写入拦截日志（不解密 HTTPS、不装根证书）
- 密码 PBKDF2 哈希；规则与配置 DPAPI 加密 + 完整性校验
- 单进程、无服务、无看门狗、空闲近乎零 CPU
- © 2026 MEFEason. All rights reserved.

---

## 功能特性

| 模块 | 说明 |
|------|------|
| 保护状态 | 主界面与托盘显示「保护中 / 已关闭 / 已锁定」，含今日拦截数与有效规则数 |
| 限制列表 | 域名 / 网址规则，按分组管理，支持增删改查、搜索、跨组移动 |
| 白名单 | **优先于限制列表**，命中即放行，绝不被拦截 |
| 分组开关 | 每组一个总开关 + 每条域名一个独立开关；有效规则 = 组开关 ∧ 单条开关 |
| 默认清单 | 抖音系 / TikTok / 快手系 / 西瓜·火山 / 直播平台共 41 条；B 站放行 17 条 |
| 密码门禁 | 首次运行设密码；启动、改规则、关保护、退出均需验证 |
| 拦截提示 | 托盘气泡：「已拦截 <域名> · 命中 <分组>/<规则>」；日志逐条记录 |
| 托盘菜单 | 打开主界面 / 保护状态 / 今日拦截数 / 分组快捷开关 / 关闭保护 / 退出 |
| 主题 | 浅色 / 深色，圆角卡片 + 左侧分组导航（设计参考 ClassIsland） |

## 定位说明（使用前必读）

1. 这是**高摩擦自约束工具，不是不可破的墙**。防绕过靠统一密码门禁、篡改回滚与开机自启；本机管理员仍可改 HKCU、删 PAC、结束进程。
2. Firefox 使用独立代理设置，默认不受保护；需在其网络设置里手动指定本程序的 PAC 地址。
3. QUIC / HTTP3（UDP 443）不走 HTTP 代理，PAC 无法分流。
4. PAC 有缓存，规则变更后可能需要重启浏览器才立即生效。
5. HTTPS 下浏览器只向 PAC 传主机名，网址/路径规则退化为域名匹配；完整 URL 匹配仅对明文 HTTP 生效。
6. 不修改 hosts、不安装根证书、不解密 TLS；只写 HKCU 与当前用户目录，**无需管理员权限**。
7. 单进程、无 Windows 服务、无看门狗、空闲零 CPU。
8. 按既定口径，**退出程序即停止拦截**（需密码验证），并恢复你原先的代理设置；防绕过依赖密码门禁与开机自启。
9. 卸载会解除保护并恢复原代理设置；卸载器会清理 PAC 残留。
10. 异常退出（强杀进程）可能残留 PAC 设置，见「手工清理」。
11. **忘记管理密码没有重置入口**：配置异常时保持拦截并锁定界面，唯一恢复路径是卸载重装。

### 手工清理残留 PAC

若程序异常退出后上网异常，打开 `设置 → 网络和 Internet → 代理 → 使用设置脚本`，关闭「使用设置脚本」或清空脚本地址即可；亦可在注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 删除 `AutoConfigURL` 值（程序会备份并恢复原值，仅在异常退出时可能残留）。

## 系统要求

- Windows 10 / 11，64 位
- .NET 10 桌面运行时（`Microsoft.WindowsDesktop.App 10`）
- 无需管理员权限、无需浏览器扩展

## 安装与使用

1. 运行 `GreenSite-Setup-<版本>.exe`（安装时需要 UAC；也可直接用构建产物目录绿色运行）。
2. 首次启动设置管理密码（≥8 位），保护自动开启。
3. 访问 `douyin.com` → 被拦并弹托盘提示；访问 `bilibili.com` → 正常（白名单优先）。
4. 在「限制列表 / 白名单」按组管理规则，用组开关或单条开关临时放行。
5. 关闭保护或退出程序需输入密码；退出后拦截停止。

## 仓库结构

```
├── src/
│   ├── GreenSite.Core/        # 模型/分组/规则引擎/PAC/代理/Guard/拦截端口/安全/日志（无 WPF 依赖）
│   ├── GreenSite/             # WPF 界面、托盘、主题、默认清单、图标
│   └── GreenSite.SelfTest/    # 手写断言自检（零 NuGet 测试框架）
├── scripts/                   # 版本号、图标、构建、自检、体检、发布脚本
├── src/setup.iss              # Inno Setup 安装器
├── LICENSE                    # FSL-1.1-ALv2 全文
├── NOTICE / THIRD_PARTY_LICENSES/
├── SECURITY.md / CONTRIBUTING.md / DEVELOPMENT.md
└── README.md
```

## 从源码构建

依赖：.NET 10 SDK（含 WindowsDesktop）、Inno Setup 6、PowerShell 5.1。

```powershell
$env:GREENSITE_SEMVER = '0.9.1'
$env:GREENSITE_CHANNEL = 'beta'
./scripts/build.ps1
```

产物：`outputs/GreenSite/`（可运行目录）、`dist/GreenSite-Setup-<版本>.exe`。构建细节见 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 版本号规范

与 Translator 项目完全一致，**唯一来源**是 `scripts/gen_version.ps1` 生成的根目录 `.version.ini`：

```
full = {platform}-{channel}-v{semver}-{YYYY}.{MMDD}.{HH}.{mm}
例   windows-beta-v0.9.1-2026.1107.09.20      （测试版）
     windows-release-v1.0.0-2026.1107.09.20   （正式版）
```

| 规则 | 说明 |
|------|------|
| 端标识 | 全小写，写在渠道**前面**；GreenSite 固定 `windows`；不加 `build` 字样 |
| 渠道 | 7 种白名单：`snapshot` / `alpha` / `beta` / `pre` / `release` / `stable` / `current` |
| 语义版本 | 正式版大迭代改动全部数字（`v1.0.0`、`v2.0.0`）；测试版只改后面的数字且前段恒为 0（`0.9.1`→`0.9.8`→`0.10.56`）；上一级变动时下级归零 |
| 时间 | 4 位年、`.MMDD.HH.mm`；**只取一次**（脚本开头），避免编译耗时导致 about / 安装目录 / PE 资源对不上；不加时间戳括号 |
| `num` | PE 版本资源专用四段纯数字 = `主.次.修订.MMDD`（如 `0.9.1.1107`）；同日重复构建靠 `full` 区分 |
| 关于页 | **只显示 `full` 版本串本身**，不单独展示构建时间 |

`.version.ini` 内容：

```ini
[version]
full=windows-beta-v0.9.1-2026.1107.09.20
platform=windows
channel=beta
semver=0.9.1
num=0.9.1.1107
```

消费链：`gen_version.ps1` → `.version.ini` → `build.ps1` 以 `-p:Version` / `-p:InformationalVersion` / `-p:FileVersion` 注入程序集与 PE 版本资源 → `setup.iss` 用 ISPP `ReadIni` 读取（安装目录名、安装器名、卸载项版本）。

环境变量：`GREENSITE_SEMVER`、`GREENSITE_CHANNEL`、`GREENSITE_PLATFORM`、`GREENSITE_BUILD_TIME`（`yyyy-MM-dd HH:mm:ss`，用于复现构建）。⚠️ 前缀是 `GREENSITE_`，写错会静默回落到 `snapshot` / `0.0.0`。

## 许可证

本项目采用 **[FSL-1.1-ALv2](https://fsl.software/)**（Functional Source License 1.1，ALv2 Future License）。

| | 是否允许 |
|--|--|
| 个人使用 / 学习 / 研究 | 免费 |
| 公司内部使用 | 免费 |
| 基于本软件开发你自己的产品 | 免费 |
| 作为竞争性商业产品/服务对外提供 | 需商业授权 |

**每个版本自发布之日起满 2 年，自动转为 Apache License 2.0**。

> FSL 不属于 OSI 定义的「开源许可证」，而是 Fair Source（源码可见 + 商业受限）。

商业授权联系：2575633220@qq.com

第三方组件与素材声明见 [`NOTICE`](NOTICE) 与 [`THIRD_PARTY_LICENSES/`](THIRD_PARTY_LICENSES/)：运行时零第三方库；界面仅借鉴 ClassIsland 的布局与配色思想，未引入其代码、图标或商标。

## 贡献与安全

提交 PR 即视为同意 [`CONTRIBUTING.md`](CONTRIBUTING.md) 的 CLA。安全漏洞请按 [`SECURITY.md`](SECURITY.md) 私下报告，勿开公开 Issue。

## 致谢

- [ClassIsland](https://github.com/ClassIsland/ClassIsland) — 界面布局与分组导航的设计借鉴（未使用其代码与商标）
- [Translator](https://github.com/MEFEason/translator) — 版本号规范与合规文件体系

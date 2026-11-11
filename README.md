# GreenSite — 网站访问控制程序

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-10-blue)
![License](https://img.shields.io/badge/license-FSL--1.1--ALv2-blue)
![Future License](https://img.shields.io/badge/converts%20to-Apache--2.0%20after%202%20years-green)

**单进程、用户级、零 UAC 的 Windows 网站访问控制工具**。通过本机 HTTP PAC 端点分流限制域名，规则按分组管理、**白名单优先于限制列表**；管理密码守护启动、规则修改、关闭保护与退出。

- 拦截方式：本机 HTTP PAC 端点 `http://127.0.0.1:<port>/proxy.pac?v=<seq>`，只写 HKCU 的 `AutoConfigURL`
- 拦截提示：托盘气泡 + 脱敏拦截日志（不记 URL 与查询参数）+ 主界面今日拦截数
- 安全：密码 PBKDF2-HMAC-SHA256 600k 迭代只存哈希；配置 DPAPI(CurrentUser) 加密 + HMAC-SHA256 完整性校验
- 技术栈：.NET 10 WPF 单进程、框架依赖发布、`net10.0-windows`、零第三方 NuGet 包、无需管理员
- 单实例命名 Mutex、托盘 `.ico` 图标、无服务、无看门狗、空闲零 CPU

© 2026 MEFEason. All rights reserved.

---

## 功能特性

| 模块 | 说明 |
|------|------|
| 保护状态 | 主界面与托盘显示「保护中 / 已关闭 / 已锁定」，含今日拦截数与有效规则数 |
| 拦截方式 | 程序内嵌本机 HTTP PAC 端点，仅绑定 `127.0.0.1:<port>`；开启保护时把 PAC 地址写入 HKCU 的 `AutoConfigURL` |
| 分组规则 | 域名 / 网址规则按分组管理；支持新建 / 重命名 / 删除组、规则跨组移动、搜索、导入默认分组 |
| 白名单优先 | 白名单在 PAC 中**先判断**，命中即放行（跟随系统代理 / 加速器，未检测到时直连），绝不被限制列表拦截；**仅在其生效时段内优先**（未启用时间控制时全天生效） |
| 两级开关 | 每组一个总开关 + 每条域名一个独立开关；**有效规则 = 组开关 ∧ 单条开关** |
| 默认清单 | 默认 7 个分组：6 个限制组（抖音系 17 / TikTok 7 / 快手系 11 / 西瓜·火山 4 / 直播平台 2 / B 站 4 = **45 条**，其中 B 站组**出厂停用**，需手动开启）+ 1 个白名单组（B 站放行 **15 条**）；较宽主域（`bytedance.com` / `snssdk.com` / `pstatp.com` 等）标注「可能误伤」，可一键禁用 |
| 密码门禁 | 首次运行设管理密码（≥ 8 位）；启动进入主界面、规则编辑（会话内一次解锁）、关闭保护、退出程序、修改密码、导出 / 清空日志、开机自启开关均需验证 |
| 拦截提示 | 托盘气泡「已拦截 `<域名>` · 命中 `<组>`/`<规则>`」；拦截日志只记 **时间 / Host / 命中组 / 命中规则 / `allow\|block`**；主界面显示今日拦截数 |
| 托盘菜单 | 打开主界面 / 保护状态 / 今日拦截数 / 分组快捷开关 / 关闭保护 / 退出 |
| 主题 | 浅色 / 深色（`DynamicResource`），左侧导航 + 圆角卡片；设计参考 ClassIsland，仅借鉴布局与配色，未使用其代码、图标或商标 |
| 恢复码 | 首次设置管理密码时同步生成一次性 25 位恢复码（Crockford Base32，仅显示一次，请立即抄录）。忘记管理密码可在锁定页用恢复码解锁，解锁后强制立即设置新密码并轮换出新恢复码；设置页也可重新生成恢复码（需密码）。恢复码也忘记只能卸载重装 |
| 加速器兼容 | 检测到系统手动代理（加速器 / 游戏代理通常写 `ProxyEnable` + `ProxyServer`）时，非黑名单域名自动跟随该上游代理（https 端点优先）；黑名单命中仍 100% 走本机拦截端口。退出 / 关闭保护只还原 `AutoConfigURL`，绝不改写加速器设置 |
| 开机自启 | 随当前用户登录自启，开机自启路径**无需输入管理密码即自动进入防护状态**（加载无密码的 DPAPI 启动快照 `protection.bin`），托盘 / 主界面显示「保护中（未解锁）」；输密码解锁后复用同一控制器。「关闭保护」仅对本次会话有效，下次自启重新自动防护 |
| 白名单生效时段 | 可为白名单配置多个 24 小时制生效时段（`HH:mm`，支持跨午夜）；未启用或时段为空则全天生效，时段外白名单整体失效并回退常规拦截判定。有效条件 = 组开关 ∧ 单条开关 ∧ 时段判定 |

> 默认清单条数指**有效规则数**：白名单来源清单共 17 行、含 2 行注释，实际规则为 15 条，勿按行数计。B 站同时存在于默认限制组（出厂停用）与白名单组（出厂启用）：默认放行；用户打开「B 站」限制组后，如需拦截还需同时关闭白名单组（白名单优先）。

## 拦截原理

### 本机 HTTP PAC 端点

程序内嵌一个轻量端点，只绑定 `127.0.0.1:<port>`，同时提供两件事：

1. 在 `http://127.0.0.1:<port>/proxy.pac?v=<seq>` 提供 PAC 脚本；
2. 承接被 PAC 分流过来的拦截流量，返回 403 提示页。

开启保护时，程序把该 PAC 地址写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 的 `AutoConfigURL`（唯一写入的代理设置），并调用 WinINET 刷新。端点只读请求首行、**不建隧道、不解密 TLS、不装根证书、不改 hosts**。

PAC 的分流逻辑固定为：

```
命中白名单     → GS_UPSTREAM                  // 白名单先判断，优先于限制列表
命中限制列表   → PROXY 127.0.0.1:<port>      // 无 DIRECT 兜底，由端点拦截
ProxyOverride 例外(*.域名) → DIRECT          // 跟随系统代理时的本机例外
其余           → GS_UPSTREAM
```

`GS_UPSTREAM` 是 PAC 里的上游变量：程序检测到系统手动代理（`ProxyEnable=1` + `ProxyServer`，加速器 / 游戏代理通常写在这里）时取其 https 端点（没有则 http / 通用端点）为 `PROXY host:port`，即白名单与其余域名都跟随加速器；未检测到则回退 `DIRECT`。**黑名单命中永远强制走本机拦截端口，不受上游影响**。

限制列表分支**没有 `DIRECT` 兜底**——被限制的域名只会走本机端点，不会因代理失败而放行。规则变更（含组开关、单条开关）或检测到系统代理变化会重建 PAC 并递增 `seq`，配合 URL 的 `?v=<seq>` 与 `Cache-Control: no-store` 强制浏览器刷新缓存。

白名单生效时段由 PAC 脚本内置的 `gsAllowActive()` 以浏览器端 `new Date()` 实时判断当前时间，时段切换无需重建 PAC 即生效（避免 PAC 缓存滞后）；服务端 `RuleEngine` 用本机 `TimeOnly` 同步判断，两端一致（详见「白名单生效时段」）。

### 为何不用静态 `data:` PAC

用户后来把「退出程序」语义明确为「退出即停止拦截」，静态 PAC 的核心收益（退出进程后仍持续拦截）不再需要；且 Chromium 自 72 起已移除 `file://` PAC 支持，Edge 官方要求 PAC 由 http / https / ftp 提供。因此采用确定可用的**本机 HTTP PAC 端点**。

### 退出与关闭保护

| 动作 | 需密码 | 行为 |
|------|--------|------|
| 关闭保护 | 是 | 解除拦截，**进程保留**（端点与托盘仍在，可再次开启） |
| 退出程序 | 是 | 只还原 `AutoConfigURL` → 停止拦截 → 结束进程 |

两者**只精确还原 `AutoConfigURL`**（原本不存在则删除，原本有值则写回）。`ProxyEnable` / `ProxyServer` 属于加速器或其它工具的现值，GreenSite 从不修改、退出时也绝不写回旧快照——否则会抹掉用户在保护期间开启的加速器设置。运行期间 `RegistryGuard` 监视 `AutoConfigURL`，被移走时自动写回；加速器对 `ProxyServer` 的正常改动会被吸收并同步进 PAC 上游。

主窗口关闭按钮行为可在设置页配置：**最小化到托盘继续保护**（默认）或 **停止拦截并退出程序（需密码）**。托盘「退出程序」会先弹后果说明再验证密码；锁定页 / 首启页可直接退出（此时尚无拦截，无需密码）。

## 白名单生效时段

可为白名单配置 24 小时制的生效时间段（`AllowlistSchedule`），让白名单只在指定时段内优先放行。配置项随 `config.bin` 保存，字段如下：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Enabled` | `bool` | 是否启用时间控制；关闭时白名单全天生效 |
| `Windows` | `List<AllowTimeWindow>` | 生效时段列表，可含多个时段 |
| `Start` | `HH:mm` | 时段开始（24 小时制，如 `09:00`） |
| `End` | `HH:mm` | 时段结束（24 小时制，如 `22:00`） |

- **多时段与跨午夜**：`Windows` 可配置多个时段；单个时段允许跨午夜（如 `22:00`–`06:00` 表示当晚 22 点到次日 6 点），当前时间落在任一时段内即视为生效。
- **兼容旧行为**：未启用时间控制（`Enabled=false`）或时段列表为空时，白名单**全天生效**，与旧行为完全一致。
- **时段外回退**：启用且有合法时段、但当前时间不在任一时段内时，**白名单整体自动失效并回退到常规拦截判定**——同一域名若命中黑名单则拦截，否则跟随系统代理 / 直连。
- **有效条件叠加**：与现有「白名单分组开关」「单条规则开关」同时生效，`有效条件 = 组开关 ∧ 单条开关 ∧ 时段判定`；白名单优先语义不变（仅在其生效时优先）。
- **两端一致**：PAC 脚本内置 `gsAllowActive()`，用浏览器端 `new Date()` 实时判断当前时段（避免 PAC 缓存导致时段切换不生效）；服务端 `RuleEngine` 用本机 `TimeOnly` 同步判断。两端共用同一份时段配置，判定一致。

设置页「白名单生效时段」卡片提供启用开关、多个开始 / 结束时间输入、增删时段、`HH:mm` 格式校验与当前状态摘要；保存后立即重建 PAC 生效。

## 安全设计

- **密码**：首次运行设管理密码（≥ 8 位）；PBKDF2-HMAC-SHA256 **600,000 次迭代** + 随机 salt，只存哈希与 salt，绝不存明文。
- **配置**：`%LOCALAPPDATA%\GreenSite\config.bin`，文件格式 `GSIT` 头 / `bodyLen` / DPAPI(JSON) / HMAC-SHA256；用 DPAPI(CurrentUser) 加密 + HMAC-SHA256 完整性校验，并保留 `config.bak` 备份。
- **启动快照与职责分离**：开机自启另用 `%LOCALAPPDATA%\GreenSite\protection.bin`（DPAPI(CurrentUser) 加密、不依赖管理密码，仅含非敏感的保护所需数据），与 `config.bin` 职责分离——**不含任何密码哈希 / 恢复码哈希**，凭据仍只存于 `config.bin`，其安全模型不变。
- **启动校验顺序**：`header → DPAPI 解密 → 密码哈希 → HMAC`；「密码错」与「配置被篡改」统一报「密码或配置校验失败」，不泄露原因。
- **无绕过口**：配置异常**不提供无密码重置**（否则会变成绕过口），唯一恢复路径是卸载重装。
- **口令处理**：密码只经 `SecureString`，用完清零；绝不写入日志、配置或异常。验证失败按 5s / 15s / 1min / 5min / 15min **指数退避**冷却。

- **恢复码（密保）**：首次设置管理密码时同步生成一次性 25 位恢复码（Crockford Base32，形如 `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`），仅显示一次、需立即抄录。磁盘上只存恢复码的 **PBKDF2 哈希**，与密码哈希同级保护。忘记管理密码时可在锁定页输入恢复码解锁，**解锁后强制立即设置新密码并轮换出新的恢复码**（旧码随即失效）；设置页也可主动「重新生成恢复码」（需验证密码）。恢复码不是无密码后门——它只用于解锁改密与轮换这一条路径，不能绕过密码进入程序功能。恢复码也忘记时，唯一恢复路径是卸载重装。

## 定位说明（使用前必读）

1. 这是**高摩擦自约束工具，不是不可破的墙**。防绕过靠统一密码门禁、篡改回滚与开机自启（**开机自启无需密码即自动进入防护**）；本机管理员仍可修改 HKCU 的 `AutoConfigURL`、删除 PAC 或结束进程。
2. Firefox 使用独立代理设置，**默认不受保护**；需在其网络设置里手动指定本程序的 PAC 地址。
3. QUIC / HTTP3（UDP 443）不走 HTTP 代理，PAC 无法分流。
4. PAC 可能被浏览器缓存，规则变更后可能需要重启浏览器才立即生效（程序已用 `?v=<seq>` 与 `no-store` 尽量强制刷新）。
5. HTTPS 下浏览器只向 PAC 传主机名，**网址 / 路径规则退化为域名匹配**；完整 URL 匹配仅对明文 HTTP 生效。
6. 不修改 hosts、不安装根证书、不解密 TLS；只写 HKCU 与当前用户目录（`%LOCALAPPDATA%\GreenSite`），**无需管理员权限**。
7. 单进程、无 Windows 服务、无看门狗、空闲零 CPU。
8. **退出程序即停止拦截**（需密码验证）：只还原 `AutoConfigURL`、不影响加速器的手动代理设置 + 停止拦截 + 结束进程；关闭保护（需密码验证）解除拦截但进程保留，**关闭保护仅对本次会话有效，下次开机自启会重新自动进入防护状态**（这是自启的明确预期行为）。防绕过依赖密码门禁与开机自启。主窗口关闭按钮行为可在设置页改为「最小化到托盘继续保护」（默认）或「退出程序」。
9. **卸载不会替你解除保护**：卸载器只清理 GreenSite 自己写入的 `AutoConfigURL`（仅当值同时含 `127.0.0.1` 与 `/proxy.pac` 才删除），不负责其它代理设置；`AutoConfigURL` 的备份与精确还原由程序在开启保护时快照、在退出或关闭保护时完成。因此卸载前请先于程序内用管理密码关闭保护或正常退出程序。
10. 程序被**强杀**时本机端点消失，浏览器会**回退直连（fail-open）**；但 `AutoConfigURL` 注册表值可能残留并导致上网异常，见下方「手工清理残留 PAC」。
11. **忘记管理密码可用恢复码解锁重置**：在锁定页输入首次设密时抄录的恢复码即可解锁，解锁后强制立即设置新密码并轮换出新恢复码。**恢复码也忘记只能卸载重装**。开机自启的防护不依赖管理密码，忘记密码不影响开机自动防护。
12. **与加速器共存**：开启保护时非黑名单域名自动跟随系统手动代理（加速器），黑名单命中仍被拦截；退出保护只还原 `AutoConfigURL`、**不改加速器设置**（`ProxyEnable` / `ProxyServer` 从不被本程序改写或写回旧值）。
13. **开机自启无需密码即自动进入防护**：自启路径加载 `%LOCALAPPDATA%\GreenSite\protection.bin` 启动快照，只含**非敏感**的保护所需数据（分组、规则、端口、PAC 版本、跟随加速器开关、白名单生效时段、通知开关），以 DPAPI(CurrentUser) 加密、**不依赖管理密码**，因此冷启动无密码也能构建规则集并开启拦截；托盘 / 主界面显示「保护中（未解锁）」，拦截气泡不再因未解锁被过滤。用户随后输入密码解锁时复用同一个保护控制器（不重启端点、不产生端口冲突）。该快照**不含**密码哈希、恢复码哈希、代理备份等任何凭据，`config.bin` 的安全模型完全不变。

### 手工清理残留 PAC

若程序异常退出后上网异常：打开 `设置 → 网络和 Internet → 代理 → 使用设置脚本`，关闭「使用设置脚本」或清空脚本地址；亦可在注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 删除 `AutoConfigURL` 值（必要时同时确认 `ProxyEnable` / `ProxyServer` 是否为你原有值——本程序从不改写这两项，加速器设置不受影响）。程序开启保护时会备份并精确还原 `AutoConfigURL`，仅在异常退出时可能残留。

## 系统要求

- Windows 10 / 11，64 位
- .NET 10 桌面运行时（`Microsoft.WindowsDesktop.App 10`），框架依赖发布
- 无需管理员权限、无需浏览器扩展

## 安装与使用

1. 运行 `dist/GreenSite-Setup-<版本>.exe` 安装；也可直接用 `outputs/GreenSite/` 绿色运行。
2. 首次启动设置管理密码（≥ 8 位），同时生成一次性 25 位恢复码——**请立即抄录**，忘记主密码时靠它解锁；保护自动开启。
3. 访问 `douyin.com` → 被拦，弹托盘气泡「已拦截 … · 命中 抖音系/…」并显示拦截提示页；访问 `bilibili.com` → 正常放行（白名单优先）。
4. 在「限制列表 / 白名单」按组管理：组总开关、每条域名独立开关、新建 / 重命名 / 删除组、规则跨组移动、搜索、导入默认分组。
5. 关闭保护或退出程序均需输入密码：关闭保护解除拦截但进程保留；退出程序停止拦截并结束进程。主窗口关闭按钮可配置为「最小化到托盘继续保护」（默认）或「退出程序（需密码）」。

## 仓库结构

```
GreenSite/
├── src/
│   ├── GreenSite.Core/            # 模型 / 规则引擎 / PAC / 代理 / 拦截端点 / 安全 / 日志（无 WPF 依赖）
│   │   ├── Models/                # 含 AllowlistSchedule.cs（白名单生效时段配置）
│   │   ├── Core/                  # 规则引擎 / PAC / 端点 / 控制器（含 v1.0.4 端点引擎热更新与时段判定）
│   │   └── Security/              # 含 ProtectionSnapshotStore.cs（无密码启动快照）
│   ├── GreenSite/                 # WPF 界面：Views / Services / Themes / data / assets
│   └── GreenSite.SelfTest/        # 手写断言自检（零 NuGet 测试框架）
├── scripts/                       # gen_version / gen_logo / gen_license_rtf / build / selftest / verify_product / publish_github
├── src/setup.iss                  # Inno Setup 安装器（ISPP ReadIni 读取 .version.ini）
├── .github/ISSUE_TEMPLATE/
├── LICENSE                        # FSL-1.1-ALv2 全文
├── NOTICE
├── THIRD_PARTY_LICENSES/
├── SECURITY.md / CONTRIBUTING.md / DEVELOPMENT.md
├── GreenSite.slnx
└── README.md
```

## 从源码构建

依赖：.NET 10 SDK（含 WindowsDesktop）、Inno Setup 6（`ISCC.exe`）、PowerShell 5.1。

```powershell
$env:GREENSITE_SEMVER='1.0.0'; $env:GREENSITE_CHANNEL='release'; ./scripts/build.ps1
```

`scripts/build.ps1` 流水线：生成 `.version.ini` → 生成 `LICENSE.rtf` → `dotnet build` → 框架依赖 `publish`（注入 PE / 程序集版本）→ 组装 `outputs/GreenSite/` → 运行自检 → `verify_product.ps1` 只读体检（打包前）→ `ISCC` 编译安装包 → 打包后复检。自检失败即中止，不会产出安装包。

`src/GreenSite.SelfTest` 为手写断言自检，**11 组用例共 151 条全绿**：规范化 / 后缀匹配、白名单优先、分组开关、PAC 生成、配置加解密与篡改检测、拦截端口端到端、代理设置快照还原、保护状态机、黑白名单切换端到端、白名单生效时段、开机自启无密码快照。

产物：

- `outputs/GreenSite/`（可直接运行的绿色目录）
- `dist/GreenSite-Setup-<full>.exe`（安装包）

构建与发布细节见 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 版本号规范

与 Translator 项目完全一致，**唯一来源**是 `scripts/gen_version.ps1` 生成的根目录 `.version.ini`（不入库）：

```
full = {platform}-{channel}-v{semver}-{YYYY}.{MMDD}.{HH}.{mm}
例   windows-release-v1.0.0-2026.1107.10.05   （正式版）
     windows-beta-v0.9.1-2026.1107.09.23      （测试版）
```

| 规则 | 说明 |
|------|------|
| 端标识 | 全小写，写在渠道**前面**；GreenSite 固定 `windows`；不加 `build` 字样 |
| 渠道 | 7 种白名单：`snapshot` / `alpha` / `beta` / `pre` / `release` / `stable` / `current` |
| 语义版本 | 正式版大迭代改动全部数字（`v1.0.0`、`v2.0.0`）；测试版前段恒为 0（`0.9.1` → `0.9.8` → `0.10.56`），只改后面的数字；上一级变动时下级归零 |
| 时间 | 4 位年 + `.MMDD.HH.mm`；**只取一次**（脚本开头），避免编译耗时导致关于页 / 安装目录 / PE 资源对不上；不加时间戳括号 |
| `num` | PE 版本资源专用四段纯数字 = `主.次.修订.MMDD`（如 `1.0.0.1107`）；同日重复构建靠 `full` 区分 |
| 关于页 | **只显示 `full` 版本串本身**，不单独展示构建时间 |

`.version.ini` 内容：

```ini
[version]
full=windows-release-v1.0.0-2026.1107.10.05
platform=windows
channel=release
semver=1.0.0
num=1.0.0.1107
```

消费链：`gen_version.ps1` → `.version.ini`（不入库）→ `build.ps1` 以 `-p:Version` / `-p:FileVersion` / `-p:AssemblyVersion` / `-p:InformationalVersion` 注入程序集与 PE 版本资源 → `src/setup.iss` 用 ISPP `ReadIni` 读取（安装目录名、安装器名、卸载项版本）。

环境变量：`GREENSITE_SEMVER`、`GREENSITE_CHANNEL`、`GREENSITE_PLATFORM`、`GREENSITE_BUILD_TIME`（`yyyy-MM-dd HH:mm:ss`，用于复现构建）、`GREENSITE_ISCC`（覆盖 `ISCC.exe` 路径）。⚠️ 前缀是 `GREENSITE_`，**写错会静默回落到 `snapshot` / `0.0.0`**。

## 许可证

本项目采用 **[FSL-1.1-ALv2](https://fsl.software/)**（Functional Source License 1.1，ALv2 Future License），全文见 [`LICENSE`](LICENSE)。

| 使用场景 | 是否允许 |
|----------|----------|
| 个人使用 / 学习 / 研究 | 免费 |
| 公司内部使用 | 免费 |
| 基于本软件开发你自己的产品 | 免费 |
| 作为竞争性商业产品 / 服务对外提供 | 需商业授权 |

**每个版本自发布之日起满 2 年，自动转为 Apache License 2.0。**

> FSL 不属于 OSI 定义的「开源许可证」，而是 Fair Source（源码可见 + 商业受限）。

商业授权联系：2575633220@qq.com

第三方组件与素材声明见 [`NOTICE`](NOTICE) 与 [`THIRD_PARTY_LICENSES/`](THIRD_PARTY_LICENSES/)：运行时零第三方库；界面仅借鉴 ClassIsland 的布局与配色思想，未引入其代码、图标或商标。

## 贡献与安全

提交 PR 即视为同意 [`CONTRIBUTING.md`](CONTRIBUTING.md) 的 CLA。安全漏洞请按 [`SECURITY.md`](SECURITY.md) 私下报告，勿开公开 Issue。

## 致谢

- [ClassIsland](https://github.com/ClassIsland/ClassIsland) — 界面布局与分组导航的设计借鉴（未使用其代码、图标与商标）
- [Translator](https://github.com/MEFEason/translator) — 版本号规范与合规文件体系
- nr-gate — 默认白名单清单（B 站全家桶放行；原始清单 17 行含 2 行注释，按有效规则计 15 条）

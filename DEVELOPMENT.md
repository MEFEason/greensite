# GreenSite 开发指南（DEVELOPMENT）

本文件面向从源码构建与发布的维护者。使用者说明见 [README.md](README.md)，贡献约定见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 1. 环境要求

| 组件 | 版本 / 说明 |
|------|-------------|
| .NET 10 SDK | 必须含 `Microsoft.WindowsDesktop.App 10` 运行时；目标框架 `net10.0-windows` |
| Inno Setup | 6.7.3；`ISCC.exe` 默认位于仓库上一级 `tools/innosetup/ISCC.exe`，可用 `GREENSITE_ISCC` 覆盖 |
| PowerShell | 5.1（`powershell.exe`）；构建脚本按 PS 5.1 语义编写 |
| Python 3 | 仅用于 `scripts/gen_logo.py` 生成多尺寸图标，构建主流程不依赖 |
| Git | 提交与发布 |

## 2. 一键构建

在仓库根目录打开 PowerShell 5.1：

```powershell
$env:GREENSITE_SEMVER='1.0.0'; $env:GREENSITE_CHANNEL='release'; ./scripts/build.ps1
```

测试版示例：

```powershell
$env:GREENSITE_SEMVER='0.9.1'; $env:GREENSITE_CHANNEL='beta'; ./scripts/build.ps1
```

| 环境变量 | 默认 | 说明 |
|----------|------|------|
| `GREENSITE_SEMVER` | `0.0.0` | 语义版本 `主.次.修订` |
| `GREENSITE_CHANNEL` | `snapshot` | 7 种渠道白名单之一：`snapshot` / `alpha` / `beta` / `pre` / `release` / `stable` / `current` |
| `GREENSITE_PLATFORM` | `windows` | 端标识，全小写；GreenSite 目前仅发布 `windows` |
| `GREENSITE_BUILD_TIME` | 当前时间 | `yyyy-MM-dd HH:mm:ss`，固定后即可复现构建 |
| `GREENSITE_ISCC` | `tools/innosetup/ISCC.exe` | 覆盖 ISCC 路径 |

⚠️ 环境变量前缀是 **`GREENSITE_`**，写错会静默回落到 `snapshot` / `0.0.0`。

`scripts/build.ps1` 流水线（任一步失败即 `exit 1`，不产出安装包）：

1. 前置断言 `dotnet 10` 与 `ISCC.exe`
2. `gen_version.ps1` 生成根目录 `.version.ini`
3. `gen_license_rtf.ps1` 生成根目录 `LICENSE.rtf`（安装器许可证页）
4. `dotnet build GreenSite.slnx -c Release`
5. `dotnet publish -c Release -r win-x64 --self-contained false -o outputs/GreenSite`，注入 `Version` / `FileVersion` / `AssemblyVersion` = `num`、`InformationalVersion` = `full`
6. 组装 `outputs/GreenSite/`：`data/`、`assets/`、`version.ini`、`LICENSE`、`NOTICE`、`README.md`、`THIRD_PARTY_LICENSES/`
7. 自检 `selftest.ps1`
8. 只读体检 `verify_product.ps1`（打包前）
9. `ISCC /Q /Odist src/setup.iss`
10. 校验安装包存在 + 只读体检 `verify_product.ps1`（打包后）

## 3. 版本号规范

与 Translator 项目完全一致，完整规则见 [README.md「版本号规范」](README.md#版本号规范)。要点：

- **唯一来源**是 `scripts/gen_version.ps1` 生成的根目录 `.version.ini`（UTF-8 无 BOM、不入库），内容为 `[version]` + `full=` `platform=` `channel=` `semver=` `num=`。
- `full = {platform}-{channel}-v{semver}-{YYYY}.{MMDD}.{HH}.{mm}`，例 `windows-release-v1.0.0-2026.1107.10.05`。用于安装器名、安装目录名、关于页、卸载项版本与 `InformationalVersion`。
- `num` 是 PE 四段纯数字版本资源 `主.次.修订.MMDD`（如 `1.0.0.1107`），注入 `Version` / `FileVersion` / `AssemblyVersion`，并写入安装器 `VersionInfoVersion` / `VersionInfoProductVersion`。同日重复构建靠 `full` 区分。
- 端标识全小写且写在渠道**前面**；7 种渠道白名单；4 位年；不加 `build` 字样、不加时间戳括号。
- **时间只取一次**（脚本开头取 `Get-Date` 或 `GREENSITE_BUILD_TIME`），避免编译耗时导致关于页 / 安装目录 / PE 版本资源对不上。
- **关于页只显示 `full` 版本串本身**，不单独展示构建时间。
- 消费链：`.version.ini` → `build.ps1` 以 `-p:` 注入程序集与 PE 版本资源 → `src/setup.iss` 用 ISPP `ReadIni` 读取（安装目录名、安装器名、卸载项版本）。

## 3.1 v1.0.1 行为变更要点

- **恢复码（密保）**：首次设置管理密码时同步生成一次性 25 位恢复码（Crockford Base32，`XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`），仅显示一次；磁盘只存 PBKDF2 哈希。忘记密码可在锁定页用恢复码解锁，解锁后强制立即设置新密码并轮换出新恢复码；设置页可「重新生成恢复码」（需密码）。代码入口 `src/GreenSite.Core/Security/RecoveryCode.cs`。恢复码也忘记只能卸载重装。
- **加速器兼容（PAC 上游跟随）**：PAC 不再写死 DIRECT。检测到系统手动代理（`ProxyEnable=1` + `ProxyServer`）时非黑名单域名跟随该上游（`PROXY host:port`，https 端点优先），白名单同样走上游，`ProxyOverride` 中 `*.域名` 形式例外走 DIRECT；黑名单命中仍 100% 走本机拦截端口。代码入口：`PacGenerator.Generate` 的 `upstream` / `bypass` 参数、`ProxySettings.ReadUpstream` 解析、`ProtectionController.RefreshUpstream` 检测变化并递增 seq 重建 PAC。`AppConfig` 新增 `FollowUpstreamProxy=true`（默认开启）。
- **退出与关闭只还原 AutoConfigURL**：`ProxySettings.Restore` **只**精确还原 `AutoConfigURL`，绝不写回 `ProxyEnable` / `ProxyServer` 旧快照（否则抹掉保护期间开启的加速器设置）；`RegistryGuard.OnTamper` 先经 `RefreshUpstream` 吸收加速器对同键的正常改动，再判断 `AutoConfigURL` 是否被移走。主窗口关闭行为可配置（托盘继续保护 / 停止拦截退出，需密码）。

## 3.2 v1.0.4 行为变更要点

- **开机自启快照（无密码自动进入防护）**：`config.bin` 由「管理密码派生的 HMAC + DPAPI」加密，冷启动无密码无法解密，旧版 `--minimized` 只把窗口藏到托盘等待解锁，进程起来了但拦截从未开启。新增 `%LOCALAPPDATA%\GreenSite\protection.bin` 启动快照，只含**非敏感**保护数据（分组 / 规则 / 端口 / PAC 版本 / 跟随加速器开关 / 白名单生效时段 / 通知开关），DPAPI(CurrentUser) 加密、**不依赖管理密码**。`AppState.Initialize(autoStartLaunch)` 在自启路径直接加载快照 → 构建规则集 → `ProtectionController.Start()`，无需密码、无需点托盘即进入防护状态；用户随后输密码解锁时复用同一个控制器（`Reconfigure`，不重启端点、不产生端口冲突）。快照绝不含密码 / 恢复码哈希、代理备份等凭据，`config.bin` 安全模型不变；「关闭保护」仅本次会话有效，下次自启重新自动防护。
- **端点引擎热更新（黑白名单判定修复）**：旧 `BlockingProxy` 持有构造时创建的 `RuleEngine` 旧实例，而 `ProtectionController.ApplyRules` 只更新了 PAC 生成所用的规则集、未同步端点判定引擎——「关闭白名单组 + 开启同域名黑名单」后 PAC 已把域名送到本机拦截端口，端点却仍按初始规则集判为白名单放行，于是日志记 `allow`、界面不弹拦截提示（「拦截气泡 ↔ 日志 ↔ 实际 403」三者不一致）。现 `ApplyRules` 以最新规则集重建端点的 `RuleEngine`（热更新），命中归因字典按 allow / block 分离，黑名单命中记录正确的命中组 / 命中规则，`Blocked` 事件只在 `DecisionKind.Block` 触发。日志字段不变（时间 / Host / 命中组 / 命中规则 / `allow|block`，不含 URL）。
- **白名单生效时段（`AllowlistSchedule`）**：配置 `AllowlistSchedule { bool Enabled; List<AllowTimeWindow> Windows }`，每个 `AllowTimeWindow` 含 `Start` / `End`（`HH:mm`，24 小时制），支持多个时段与跨午夜。未启用或时段为空时全天生效（旧行为一致），启用且有合法时段时仅落在任一时段内才放行，时段外白名单整体失效并回退常规拦截判定；与 `Ruleset` 的分组开关 / 单条开关同时生效（组开关 ∧ 单条开关 ∧ 时段判定）。时段配置随 `Ruleset` 一起绑定到 PAC 与端点两端：PAC 脚本内置 `gsAllowActive()` 用浏览器端 `new Date()` 实时判断（**不能依赖服务端定时重建 PAC**，浏览器缓存会使时段切换滞后），服务端 `RuleEngine` 用本机 `TimeOnly` 同步判断，两端共用同一份配置、判定一致。

## 3.3 v1.0.5 行为变更要点

- **自启未解锁门禁（`AccessPolicy`）**：开机自启以无密码快照自动开启防护，但 `IsUnlocked=false`，旧版仍可从托盘 / 锁定页直接退出或关闭保护，等于绕过解锁停止拦截。现把判定下沉为 Core 纯逻辑 `AccessPolicy.RequiresUnlock(isFirstRun, isUnlocked, isProtectionActive)`；命中时 `AppState.RequestExit` / `CloseProtection` / `StartProtection` 统一提示后拒绝，托盘两项禁用、锁定页退出入口隐藏，`OnWindowClose` 返回 false（关闭窗口仅最小化到托盘）。首启无配置豁免（保留「设置密码页可退出」修复），保护未激活的普通冷启动豁免，恢复码解锁未重设态豁免（避免僵局）。解锁后 `RulesChanged` 会重建托盘菜单恢复可用。自检用例 12 覆盖完整状态矩阵。
- **卸载管理密码门禁（`UninstallGuard` + Inno `InitializeUninstall`）**：卸载器在最早阶段以 `--verify-uninstall` 启动主程序；`App.OnStartup` **必须在创建单实例 Mutex 之前分流**，否则正在运行的 GreenSite 会让第二实例直接 Shutdown、卸载器拿不到退出码。验证窗口 `UninstallGuardWindow` 用 `UninstallGuard.Verify(SecureString)` 走 `SecureConfigStore.Load` 完整链路，进程退出码 0/1 交给 Inno；`InitializeUninstall` 返回 False 立即终止卸载并用 `MsgBox` 显示失败提示。密码不进命令行、不落盘；`config.bin` 不存在（从未设密）时放行卸载。自检用例 13 覆盖正确 / 错误 / 空密码 / 篡改配置 / 无配置。
- **只读体检新增安装器静态检查**：`verify_product.ps1` 第 10 项校验 `setup.iss` 含 `InitializeUninstall` / `--verify-uninstall` / `ResultCode <> 0` / `MsgBox` / `Result := False`，防止未来误删卸载门禁。

## 4. 自检与只读体检

### 自检 `scripts/selftest.ps1`

调用 `src/GreenSite.SelfTest`（手写断言 runner，零 NuGet 测试框架），共 **173 条断言 / 13 组用例**：

1. 规则规范化与后缀匹配
2. 白名单优先于限制列表
3. 组开关 / 单条开关 / 跨组移动
4. PAC 生成（白名单判断在前、黑名单只走本地端口、带 seq 与 sha256）
5. 配置加解密往返 / 篡改检测 / 统一报错 / 密码门禁冷却退避
6. 拦截端口端到端（PAC / HTTP / CONNECT / 日志脱敏）
7. 代理设置快照与精确还原（使用临时注册表键，不碰 `Internet Settings`）
8. 保护状态机（开启 / 规则变更 / 退出恢复代理设置）
9. 黑白名单切换端到端（端点引擎热更新、日志 `block` / 拦截气泡 / 403 一致）
10. 白名单生效时段（多时段 / 跨午夜 / 时段外回退 + 端到端）
11. 开机自启无密码快照（`protection.bin` 往返 / 自动 Active / 解锁复用端点）
12. 自启未解锁门禁（关闭保护 / 退出 / 关闭窗口仅最小化的状态矩阵）
13. 卸载管理密码门禁（正确 / 错误 / 空密码 / 篡改配置 / 无配置）

失败即退出码 1，`build.ps1` 中止。可单独运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1
```

### 只读体检 `scripts/verify_product.ps1`

**只读**：只检查，绝不修改 / 删除 / 创建任何文件；全部通过退出 0，任一 `[FAIL]` 退出 1。`build.ps1` 在打包前、打包后各跑一次。检查项：

- `outputs/GreenSite/GreenSite.exe` 存在；`GreenSite.runtimeconfig.json` 引用 `Microsoft.WindowsDesktop.App`
- `assets/app.ico`、`data/default_blocklist.txt`、`data/default_allowlist.txt` 存在
- `version.ini` 存在且 `full` 非空
- PE 版本资源 `FileVersion` / `ProductVersion` 非空
- 所有 `scripts/*.ps1` 以 UTF-8 BOM 开头；根目录 `LICENSE.rtf` 存在
- 发布目录内无 `*.pfx` / `*.key` / `config.bin` 等敏感文件
- 产物 mtime 不早于 `src/**/*.{cs,xaml}` 最大 mtime（±2s）
- 若 `dist/` 已有安装包，其 mtime 晚于主程序（±2s）

### ⚠️ 破坏性验证只能由用户手动执行

构建脚本与体检脚本**不会**安装、卸载、删除用户配置、清理注册表或结束进程。以下均属破坏性操作，必须由用户在本机手动执行并自行确认结果：

- 运行 `dist/GreenSite-Setup-<full>.exe` 安装 / 卸载
- 在程序内用管理密码关闭保护或退出程序
- 验证 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 的 `AutoConfigURL` 是否被精确还原（卸载器仅在本程序写入的 PAC 残留时才清理）
- 清理 `%LOCALAPPDATA%\GreenSite` 下的配置与日志

## 5. 产物路径

| 路径 | 说明 |
|------|------|
| `outputs/GreenSite/` | 绿色运行目录，可直接运行 `GreenSite.exe`（框架依赖，需 .NET 10 桌面运行时） |
| `dist/GreenSite-Setup-<full>.exe` | Inno Setup 安装包 |

`outputs/`、`dist/`、`.version.ini`、`LICENSE.rtf` 均被 `.gitignore` 排除，不入库。

## 6. 目录结构

```
GreenSite/
├── src/
│   ├── GreenSite.Core/            # 无 WPF 依赖、零 NuGet PackageReference
│   │   ├── Models/                # AppConfig / Rule / RuleGroup / RuleType / ProtectionState / BlockDecision / BlockLogEntry
│   │   ├── Core/                  # RuleEngine / RuleNormalizer / Ruleset / DefaultRules / PacGenerator
│   │   │                         # BlockingProxy / ProtectionController / ProxySettings / RegistryGuard / LogService
│   │   └── Security/             # PasswordHasher / PasswordGate / SecureConfigStore / Win32/
│   ├── GreenSite/                # WPF 主程序（net10.0-windows，UseWPF + UseWindowsForms）
│   │   ├── Views/                # 各功能页面与 PasswordDialog
│   │   ├── Services/             # AppState / 主题 / 托盘通知 / 开机自启
│   │   ├── Themes/               # Colors.Light / Colors.Dark / Controls.xaml 样式
│   │   ├── data/                 # default_blocklist.txt / default_allowlist.txt（默认清单）
│   │   ├── assets/               # app.ico（多尺寸）/ logo.svg / logo.png
│   │   └── app.manifest          # asInvoker，用户级运行
│   ├── GreenSite.SelfTest/       # 手写断言自检（零 NuGet 测试框架）
│   └── setup.iss                 # Inno Setup 6 安装器脚本（ISPP ReadIni 读 .version.ini）
├── scripts/                      # gen_version / gen_license_rtf / gen_logo.py / build / selftest / verify_product / publish_github.py
├── outputs/GreenSite/            # 绿色运行目录（构建产物，不入库）
├── dist/                         # 安装包（构建产物，不入库）
├── .version.ini                  # 版本号唯一来源（构建时生成，不入库）
├── LICENSE / NOTICE / THIRD_PARTY_LICENSES/
├── README.md / DEVELOPMENT.md / CONTRIBUTING.md / SECURITY.md
└── GreenSite.slnx                # .NET 10 解决方案文件
```

## 7. 常见坑清单

| 坑 | 说明 / 规避 |
|----|-------------|
| `.ps1` 必须 UTF-8 BOM | PowerShell 5.1 默认按 ANSI 读脚本，无 BOM 的中文注释/字符串会乱码。`verify_product.ps1` 逐字节检查 `scripts/*.ps1` 的 `EF BB BF`。 |
| PS 5.1 的 `*>` 重定向写 UTF-16LE | `*>`、`>`、`Out-File` 默认输出 UTF-16LE。脚本写文件必须显式 `[System.IO.File]::WriteAllText($p, $s, (New-Object System.Text.UTF8Encoding($false)))`，否则 `.version.ini` / `version.ini` / `LICENSE.rtf` 被 Inno `ReadIni` 按 UTF-8 读取会乱码。 |
| 安装器脚本也要 UTF-8 BOM | `src/setup.iss` 含中文（启动描述、卸载提示），以 UTF-8 BOM 保存，Inno Setup 6 据此按 UTF-8 读取。 |
| ISPP `ReadIni` 相对路径以 `.iss` 所在目录为基准 | 不是进程 CWD；`src/setup.iss` 用 `..\.version.ini` 指向根目录。`#define` 里也不要用 `YearOf(CurrentDate)` 之类函数调用。 |
| 注册表根键一律 HKA | HKA 随安装权限自动映射到 HKLM（管理员安装）或 HKCU（用户级安装）；写死 HKLM/HKCU 会让「仅为当前用户安装」失效或漏写。安装器不写开机自启（由程序自己在 HKCU Run 写）、不修改系统 PATH、不在尾声手工写注册表键。 |
| Git Bash 给 ISCC 传 `/D` 需 `MSYS_NO_PATHCONV=1` | MSYS 会把 `/Dxxx` 当路径转换吃掉；`build.ps1` 用 PowerShell 直接调用 ISCC，不受影响。 |
| `dotnet new sln` 在 .NET 10 生成 `.slnx` | 是 XML 格式解决方案文件，不再是 `.sln`；`build.ps1` 用的是 `GreenSite.slnx`。 |
| WPF + WinForms 同开时的类型歧义 | `UseWPF` 与 `UseWindowsForms` 同时为 true 时，`Application` / `MessageBox` 在 `System.Windows` 与 `System.Windows.Forms` 间歧义，必须用完全限定名。 |
| 托盘图标必须 `.ico` 路径 | `NotifyIcon` 只接受 `.ico` 文件；用 `assets/app.ico`（多尺寸 ICO，由 `gen_logo.py` 生成），不要用 `.png` / `.svg`。 |
| 禁止按进程名结束进程 | 安装/卸载不得 `taskkill`，会误杀宿主进程；程序用命名 Mutex 自行处理单实例与退出。 |
| `SecureConfigStore.Save` 第二参数已可选 | v1.0.1 起第二参数是可选的 `secretForInit`（仅首次设密 / 恢复码轮换等需要下发 DPAPI 密钥的场景传入），日常保存配置直接调用 `Save(config)` 即可，不要误以为必须传密钥。 |
| `BlockingProxy` 的 `RuleEngine` 不能在构造后固定 | 端点判定引擎若只在构造时创建一次，`ApplyRules` 更新 PAC 规则集后端点仍用旧规则判定，导致 PAC 已分流到拦截端口、端点却按旧白名单放行（日志 `allow`、无拦截气泡）。规则变更时必须以最新规则集同步重建端点 `RuleEngine`。 |
| 无密码启动快照绝不能写入凭据 | `protection.bin` 不依赖管理密码即可解密，仅用于冷启动重建保护；**绝不能**写入密码哈希 / 恢复码哈希 / 代理备份等敏感字段，否则等于开了无密码读取凭据的口子。凭据只存于 `config.bin`，其安全模型不变。 |
| 时段切换不能靠服务端定时重建 PAC | 浏览器会缓存 PAC 脚本，服务端定时重建（递增 seq）无法保证时段切换即时生效；PAC 内须用 `gsAllowActive()` + `new Date()` 实时判断当前时间，服务端 `RuleEngine` 用 `TimeOnly` 同步判定，两端共用同一份时段配置。 |
| 卸载验证必须在单实例 Mutex 之前分流 | `--verify-uninstall` 若走到单实例检查，正在运行的 GreenSite 会让第二实例 Set 事件后直接 `Shutdown()`，卸载器拿不到退出码；`App.OnStartup` 必须最早判断该参数，用 `Dispatcher.BeginInvoke` 弹窗后 `Shutdown(0/1)` 返回退出码。 |
| 卸载门禁不能只读 `config.bin` 是否存在 | 必须走 `SecureConfigStore.Load` 完整校验（DPAPI + 密码哈希 + HMAC），否则篡改配置或伪造空文件即可绕过；`config.bin` 不存在（从未设密）才放行，避免装了未用却卸不掉。 |
| 未解锁门禁要留三类豁免 | 首启无配置（否则「设置密码页无法退出」回归）、保护未激活的普通冷启动（退出无拦截可解除）、恢复码解锁未重设态（否则忘记旧密码会形成退出僵局）。判定集中在 `AccessPolicy`，不要在 UI 各处分散写条件。 |

## 8. 发布流程

1. **本地构建**：设置 `GREENSITE_SEMVER` / `GREENSITE_CHANNEL`，运行 `./scripts/build.ps1`；流水线内已跑自检与打包前/后只读体检。
2. **人工确认**：核对 `dist/GreenSite-Setup-<full>.exe` 的 `full` 与关于页一致；破坏性验证（安装 / 卸载 / 清理）由用户手动执行。
3. **提交并 push**：确认未提交任何凭据或构建产物（`*.pfx`、`*.key`、`config.bin`、`config.bak`、`outputs/`、`dist/`、`.version.ini`），提交源码后 `git push`。`scripts/publish_github.py` 负责建仓与 push：token 由 `git credential fill` 取得、**只驻留进程内存**，不打印、不落盘、不进命令行参数，remote 用普通 https URL（不含 token），用完立即从内存移除。
4. **创建 GitHub Release**：以 `full` 作为 tag / 标题创建 Release，并上传 `dist/GreenSite-Setup-<full>.exe`（网页或 `gh release create`）。凭据只在内存中由凭据管理器 / `gh` 提供，**绝不写入仓库或任何文件**。
5. **许可证**：每个版本自发布之日起满 2 年自动转为 Apache-2.0，详见 [README.md「许可证」](README.md#许可证)。

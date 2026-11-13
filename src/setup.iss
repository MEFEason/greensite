; GreenSite 安装器脚本（Inno Setup 6 / Unicode）。
; 所有相对路径以本文件所在的 src\ 为基准，..\ 即仓库根目录。
; 版本号唯一来源是根目录 .version.ini（由 scripts/gen_version.ps1 生成，UTF-8 无 BOM）。
; 脚本含中文字符串，故以 UTF-8 BOM 保存：Inno Setup 6 据此按 UTF-8 读取。

#define MyAppVersion ReadIni("..\.version.ini", "version", "full", "windows-snapshot-v0.0.0-unknown")
#define MyAppVersionNumber ReadIni("..\.version.ini", "version", "num", "0.0.0.0")
#define MyAppSemVer ReadIni("..\.version.ini", "version", "semver", "0.0.0")

[Setup]
AppName=GreenSite
AppVersion={#MyAppVersion}
AppVerName=GreenSite {#MyAppVersion}
AppComments=GreenSite (SemVer {#MyAppSemVer})
AppPublisher=MEFEason
AppPublisherURL=https://github.com/MEFEason/greensite
AppSupportURL=https://github.com/MEFEason/greensite
AppCopyright=Copyright 2026 MEFEason. All rights reserved.
DefaultDirName={autopf}\GreenSite
DefaultGroupName=GreenSite
; 程序本体是用户级（asInvoker，只写 HKCU 与 LOCALAPPDATA），但安装包默认按管理员安装到 Program Files；
; 允许用户在权限对话框里改为「仅为我和当前用户安装」。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\LICENSE.rtf
OutputDir=..\dist
OutputBaseFilename=GreenSite-Setup-{#MyAppVersion}
VersionInfoVersion={#MyAppVersionNumber}
VersionInfoProductVersion={#MyAppVersionNumber}
VersionInfoTextVersion={#MyAppVersion}
SetupIconFile=..\src\GreenSite\assets\app.ico
UninstallDisplayName=GreenSite
UninstallDisplayIcon={app}\GreenSite.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 一条通配覆盖 build.ps1 组装的整个绿色运行目录：主程序、依赖、data/、assets/、version.ini、
; LICENSE、NOTICE、README.md、THIRD_PARTY_LICENSES/ 全部随包安装。
Source: "..\outputs\GreenSite\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GreenSite"; Filename: "{app}\GreenSite.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GreenSite"; Filename: "{app}\GreenSite.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GreenSite.exe"; Description: "启动 GreenSite"; Flags: nowait postinstall skipifsilent

[Registry]
; 根键一律 HKA：随安装权限自动映射到 HKLM（管理员安装）或 HKCU（用户级安装）。
; 只写卸载/产品显示信息与 App Paths；不写开机自启（由程序自己在 HKCU Run 写），不修改系统 PATH。
Root: HKA; Subkey: "Software\GreenSite"; ValueType: string; ValueName: "DisplayName"; ValueData: "GreenSite"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\GreenSite"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\GreenSite"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\GreenSite.exe"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\GreenSite"; ValueType: string; ValueName: "Publisher"; ValueData: "MEFEason"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\GreenSite"; ValueType: string; ValueName: "URLInfoAbout"; ValueData: "https://github.com/MEFEason/greensite"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\GreenSite.exe"; ValueType: string; ValueData: "{app}\GreenSite.exe"; Flags: uninsdeletekey

[UninstallDelete]
; 只清安装目录内可再生的临时/日志项。用户配置与运行日志在 LOCALAPPDATA\GreenSite，绝不删除。
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\*.tmp"
Type: files; Name: "{app}\logs\*.log"
Type: dirifempty; Name: "{app}\logs"
Type: files; Name: "{app}\data\*.tmp"

[Messages]
; 卸载确认页：明确告知需验证管理密码，以及验证失败的后果。
ConfirmUninstall=卸载需要验证管理密码；验证通过后才继续卸载，验证失败或取消会立即退出卸载程序，不会删除任何文件或更改设置。%n%n若保护正在运行，请先于程序内用管理密码关闭保护或退出程序。%n%n确定要完全移除 %1 及其所有组件吗？

[Code]
// 程序用命名 Mutex 自行处理单实例与退出，安装器不按进程名结束任何进程，避免误杀宿主进程。
// 卸载收尾只精确清理本程序留下的 PAC 残留，删除后不弹窗。

// 卸载门禁：InitializeUninstall 在卸载器最早阶段执行，返回 False 立即终止卸载，
// 不删除任何文件、不写注册表、不进入任何后续卸载步骤。
// 主程序以 --verify-uninstall 拉起独立验证窗口（不参与单实例 Mutex、不启动防护），
// 验证走完整链路 header → DPAPI → 密码哈希 → HMAC，退出码 0 = 通过。
function InitializeUninstall(): Boolean;
var
  AppDir, GuardExe: String;
  ResultCode: Integer;
begin
  Result := False;
  AppDir := ExpandConstant('{app}');
  GuardExe := AddBackslash(AppDir) + 'GreenSite.exe';

  if not FileExists(GuardExe) then
  begin
    MsgBox('无法完成管理密码验证：找不到主程序 GreenSite.exe。' + #13#10 +
           '卸载已取消，未删除任何文件、未更改任何设置。', mbError, MB_OK);
    exit;
  end;

  if not Exec(GuardExe, '--verify-uninstall', AppDir,
              SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('无法启动管理密码验证。' + #13#10 +
           '卸载已取消，未删除任何文件、未更改任何设置。', mbError, MB_OK);
    exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox('管理密码验证失败或已取消，卸载已取消。' + #13#10 +
           '未删除任何文件、未更改任何设置。', mbError, MB_OK);
    exit;
  end;

  Result := True;
end;

function IsGreenSitePac(const S: String): Boolean;
begin
  // 仅当同时命中本地回环地址与本程序的 PAC 路径才认为是 GreenSite 写入的值，
  // 避免误删用户原有的代理脚本配置。
  Result := (Pos('127.0.0.1', S) > 0) and (Pos('/proxy.pac', S) > 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  PacValue: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
      'AutoConfigURL', PacValue) then
    begin
      if IsGreenSitePac(PacValue) then
      begin
        RegDeleteValue(HKEY_CURRENT_USER,
          'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
          'AutoConfigURL');
      end;
    end;
  end;
end;

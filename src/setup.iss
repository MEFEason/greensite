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

// ===== 安装/卸载用户配置与守护服务相关全局变量 =====
var
  ConfigOptionsPage: TInputOptionWizardPage;   // 安装时的“用户配置”选项页
  GuardOptionsPage: TInputOptionWizardPage;    // 安装时的“守护服务”选项页
  OverwriteUserConfig: Boolean;                // 安装时是否覆盖用户配置
  InstallGuardService: Boolean;                // 安装时是否创建守护服务
  UninstallDeleteUserConfig: Boolean;          // 卸载时是否删除用户配置

// 删除本程序的用户配置与加密备份。
// 安全约束：仅当目标路径确实位于 LOCALAPPDATA 下、且命中受控目录名时才删除；
// 只删除白名单文件与备份目录，不触碰 logs 日志目录；任何异常均静默兜底。
procedure DeleteGreenSiteUserData;
var
  LocalData, GreenDir, BkpDir: String;
begin
  try
    LocalData := ExpandConstant('{localappdata}');

    // 用户配置目录：LOCALAPPDATA 下的 GreenSite
    GreenDir := AddBackslash(LocalData) + 'GreenSite';
    if DirExists(GreenDir) then
    begin
      // 路径安全校验：必须以 LOCALAPPDATA 开头且命中 GreenSite 目录名。
      if (Pos(Uppercase(AddBackslash(LocalData)), Uppercase(GreenDir)) = 1) and
         (Pos('GREENSITE', Uppercase(GreenDir)) > 0) then
      begin
        DeleteFile(AddBackslash(GreenDir) + 'config.bin');
        DeleteFile(AddBackslash(GreenDir) + 'config.bak');
        DeleteFile(AddBackslash(GreenDir) + 'protection.bin');
        DeleteFile(AddBackslash(GreenDir) + 'options.bin');
        // 注意：保留 logs 日志目录，不删除。
      end;
    end;

    // 加密备份目录：LOCALAPPDATA 下的 stgren\bkp
    BkpDir := AddBackslash(LocalData) + 'stgren\bkp';
    if DirExists(BkpDir) then
    begin
      // 路径安全校验：必须以 LOCALAPPDATA 开头且命中 stgren\bkp 目录名。
      if (Pos(Uppercase(AddBackslash(LocalData)), Uppercase(BkpDir)) = 1) and
         (Pos('STGREN\BKP', Uppercase(BkpDir)) > 0) then
        DelTree(BkpDir, True, True, True);
    end;
  except
    // 删除失败不影响安装/卸载流程，静默兜底。
  end;
end;

// 安装向导初始化：在 Ready 页之前追加“用户配置”（默认不勾选）与“守护服务”（v1.1.2 默认勾选）两个选项页。
// CreateInputOptionPage 的 Exclusive=False 表示复选框（Values[i] 为 Boolean），ListBox=False 表示平铺。
procedure InitializeWizard;
begin
  ConfigOptionsPage := CreateInputOptionPage(
    wpSelectTasks,
    '用户配置',
    '检测到已有用户配置时，可选择保留或覆盖。覆盖会删除本机密码、恢复码、规则与备份，且不可恢复。',
    '请选择：', False, False);
  ConfigOptionsPage.Add('覆盖已有用户配置（删除本机密码、恢复码、规则与备份，不可恢复）');
  ConfigOptionsPage.Values[0] := False;

  GuardOptionsPage := CreateInputOptionPage(
    ConfigOptionsPage.ID,
    '守护服务',
    '推荐：安装 Windows 服务（LocalSystem），检测主进程异常退出并在用户会话自动恢复；普通用户无法结束服务。需管理员权限，用户级安装会跳过。默认勾选；可在卸载时移除。',
    '请选择：', False, False);
  GuardOptionsPage.Add('安装 GreenSite 守护服务（LocalSystem，普通用户无法结束，需管理员权限）');
  GuardOptionsPage.Values[0] := True;  // v1.1.2：唯一高权限守护，默认勾选
end;

// 安装收尾：按用户选择覆盖配置、创建守护服务；失败均不阻断安装。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ScExe, SvcParams: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    OverwriteUserConfig := (ConfigOptionsPage <> nil) and ConfigOptionsPage.Values[0];
    InstallGuardService := (GuardOptionsPage <> nil) and GuardOptionsPage.Values[0];

    // 仅当用户勾选覆盖时删除用户配置，静默完成。
    if OverwriteUserConfig then
      DeleteGreenSiteUserData;

    // 仅当用户勾选且当前具备管理员权限时创建守护服务。
    if InstallGuardService and IsAdmin then
    begin
      try
        ScExe := ExpandConstant('{sys}\sc.exe');
        ResultCode := 0;
        SvcParams := 'create GreenSiteGuard binPath= "' + ExpandConstant('{app}\Guard.exe') +
                     '" start= auto DisplayName= "GreenSite 守护服务"';
        if Exec(ScExe, SvcParams, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
           (ResultCode = 0) then
        begin
          // v1.1.2：把安装用户的数据目录写进服务参数，LocalSystem 才能读对心跳/配置。
          RegWriteStringValue(HKEY_LOCAL_MACHINE,
            'SYSTEM\CurrentControlSet\Services\GreenSiteGuard\Parameters',
            'UserDataDir', ExpandConstant('{localappdata}\GreenSite'));
          Exec(ScExe, 'start GreenSiteGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        end;
      except
        // 守护服务安装失败不阻断安装，静默兜底。
      end;
    end;
  end;
end;

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

  // 仅在管理密码验证通过后才询问是否删除用户配置；默认保留，取消或“否”不影响卸载。
  // 卸载器在 InitializeUninstall 阶段向导窗口通常尚未创建，故用 MsgBox 询问最稳妥。
  UninstallDeleteUserConfig := False;
  if not UninstallSilent then
  begin
    if MsgBox('是否同时删除本机用户配置？' + #13#10 +
              '包括管理密码、恢复码、规则、加密备份。默认保留。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      UninstallDeleteUserConfig := True;
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
  ResultCode: Integer;
  ScExe: String;
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

    // 用户勾选时清理用户配置与备份，保留 logs，静默完成。
    if UninstallDeleteUserConfig then
      DeleteGreenSiteUserData;

    // 移除守护服务：服务存在则先停止再删除，失败静默。
    try
      ScExe := ExpandConstant('{sys}\sc.exe');
      ResultCode := 0;
      if Exec(ScExe, 'query GreenSiteGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
         (ResultCode = 0) then
      begin
        Exec(ScExe, 'stop GreenSiteGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Exec(ScExe, 'delete GreenSiteGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end;
    except
      // 服务移除失败不影响卸载，静默兜底。
    end;
  end;
end;

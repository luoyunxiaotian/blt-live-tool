; 直播小帮手 · Windows 安装程序（Inno Setup 6/7）
;
; 由 tools/make-installer.py 调用编译，路径与版本通过 /D 宏传入：
;   /DPayloadDir=<打包好的发布树>   /DOutputDir=<产物目录>
;   /DAppVer=0.1.3                 /DOutputBase=<产物文件名（不含扩展名）>
;   /DSetupIcon=<tray.ico 路径>    /DChineseIsl=<ChineseSimplified.isl 路径，可选>
;
; 注意：本文件必须保存为「UTF-8 带 BOM」，否则 Inno 会按系统 ANSI 码页读取，
;       AppName/快捷方式里的中文会变乱码（make-installer.py 会自动补 BOM）。
;
; 为什么默认装到 {localappdata}\Programs：应用把 data\（配置/歌词/录屏/点歌列表）、
; update_staging\、update_backup\ 和 WebView2 配置目录全部写在 exe 旁边，并且内置更新器
; 会就地替换自身文件 —— Program Files 下非管理员不可写，装过去整个应用都会出问题。

#ifndef PayloadDir
  #error 缺少 /DPayloadDir=<发布树目录>
#endif
#ifndef OutputDir
  #error 缺少 /DOutputDir=<产物目录>
#endif
#ifndef AppVer
  #define AppVer "0.0.0"
#endif
#ifndef OutputBase
  #define OutputBase "setup"
#endif
#ifndef SetupIcon
  #define SetupIcon ""
#endif

[Setup]
; AppId 固定不变：升级安装靠它识别同一产品（不要改）
AppId={{7C4E9A31-5D82-4B6F-A3E1-9F0C2D7B54A8}
AppName=直播小帮手
AppVersion={#AppVer}
AppVerName=直播小帮手 {#AppVer}
AppPublisher=罗运小天
AppComments=弹幕 / 点歌 / 念弹幕 / OBS 浮层的直播小帮手（MAUI + Blazor Hybrid）
DefaultDirName={localappdata}\Programs\直播小帮手
DefaultGroupName=直播小帮手
DisableProgramGroupPage=yes
; 允许用户改安装目录（中文路径可用；但不要选 Program Files，应用需要写自己的目录）
DisableDirPage=no
; 免管理员：每用户安装，安装目录与用户数据都在当前用户可写位置
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
#if SetupIcon != ""
SetupIconFile={#SetupIcon}
#endif
UninstallDisplayIcon={app}\BiLi_live_Tool.exe
UninstallDisplayName=直播小帮手 {#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; 安装时若旧实例还占着 exe/dll，交给 Restart Manager 提示关闭（应用内有单实例互斥体，
; 但互斥体按安装目录哈希生成，换目录安装时挡不住旧实例）
CloseApplications=yes
RestartApplications=no
AppMutex=Local\BiLi_live_Tool_MAUI_Setup

[Languages]
#ifdef ChineseIsl
Name: "chinese"; MessagesFile: "{#ChineseIsl}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: checkedonce

[Files]
; 整棵发布树（788 个文件：程序本体 + wwwroot + wwwroot\legacy 浮层/面板 + tts 两个引擎 exe
; + verify-key.txt + default-config.json + update-now.* 等），已由打包流程排除 data\、
; update_staging\、update_backup\、WebView2 配置与 config.json
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单：组内一份 + Programs 根目录一份（后者保证开始菜单搜索能命中）
Name: "{group}\直播小帮手"; Filename: "{app}\BiLi_live_Tool.exe"; WorkingDir: "{app}"; IconFilename: "{app}\tray.ico"; Comment: "直播小帮手 {#AppVer}"
Name: "{autoprograms}\直播小帮手"; Filename: "{app}\BiLi_live_Tool.exe"; WorkingDir: "{app}"; IconFilename: "{app}\tray.ico"; Comment: "直播小帮手 {#AppVer}"
Name: "{autodesktop}\直播小帮手"; Filename: "{app}\BiLi_live_Tool.exe"; WorkingDir: "{app}"; IconFilename: "{app}\tray.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\BiLi_live_Tool.exe"; Description: "立即启动 直播小帮手"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清「运行期残留」；用户数据 data\ 刻意保留（Inno 本来也不会删运行期生成的文件）
Type: filesandordirs; Name: "{app}\update_staging"
Type: filesandordirs; Name: "{app}\update_backup"
Type: filesandordirs; Name: "{app}\BiLi_live_Tool.exe.WebView2"

[Code]
const
  WebView2ClientKey = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2HelpUrl = 'https://developer.microsoft.com/microsoft-edge/webview2/';

function WebView2Installed(): Boolean;
var
  pv: String;
begin
  pv := '';
  RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientKey, 'pv', pv);
  if pv = '' then
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientKey, 'pv', pv);
  if pv = '' then
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2ClientKey, 'pv', pv);
  Result := (pv <> '') and (pv <> '0.0.0.0');
end;

function InitializeSetup(): Boolean;
begin
  // 界面基于 WebView2（Edge 内核）。Win11 / 装过 Edge 的 Win10 一般自带；
  // 这里只提示不捆绑（避免安装包多出上百 MB），用户按提示装一次即可。
  if not WebView2Installed() then
    MsgBox('未检测到 Microsoft Edge WebView2 运行时。' + #13#10 + #13#10 +
           '界面需要它才能显示。若启动后界面空白，请到下面地址下载安装「Evergreen Runtime」，装完再启动本程序：' + #13#10 +
           WebView2HelpUrl, mbInformation, MB_OK);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  runValue: String;
begin
  // 只在自启项确实指向本次安装目录时才移除，避免误删用户的其它自启配置
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BiLi_live_Tool', runValue) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(runValue)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BiLi_live_Tool');
  end;

  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    MsgBox('已卸载 直播小帮手。' + #13#10 + #13#10 +
           '你的用户数据（配置、歌词、录屏、点歌列表等）保留在：' + #13#10 +
           ExpandConstant('{app}') + '\data' + #13#10 + #13#10 +
           '如需彻底清理，手动删除该目录即可；想保留配置重装，直接再装一次即可沿用。',
           mbInformation, MB_OK);
end;

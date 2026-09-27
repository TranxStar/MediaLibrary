; ─────────────────────────────────────────────────────────────
;  素材库 MediaLibrary 安装脚本（阶段 4）
;  面向学校电脑：普通用户权限即可安装（PrivilegesRequired=lowest）
; ─────────────────────────────────────────────────────────────

#define MyAppName "素材库 MediaLibrary"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MediaLibrary"
#define MyAppExeName "MediaLibrary.exe"

[Setup]
AppId={{5C7D2B1E-9A3F-4C2B-8E1D-6F5A4B3C2D1E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MediaLibrary
DefaultGroupName=素材库 MediaLibrary
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=MediaLibrary-Setup-{#MyAppVersion}
SetupIconFile=..\src\MediaLibrary.App\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

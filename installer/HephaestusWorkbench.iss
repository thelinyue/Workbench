#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#ifndef AppSource
  #error AppSource must be supplied by build-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1
#endif

; 该脚本是正式发行安装包的唯一入口。应用文件由构建脚本发布到临时目录，
; Inno Setup 再将完整的 self-contained 主程序压缩进一个标准离线安装包。
[Setup]
AppId={{3D79B409-48B0-48D0-81AC-B57784210F32}
AppName=Hephaestus工作台
AppVersion={#MyAppVersion}
AppVerName=Hephaestus工作台
AppPublisher=thelinyue
AppPublisherURL=https://github.com/thelinyue/Hephaestus-Workbench-Releases
AppSupportURL=https://github.com/thelinyue/Hephaestus-Workbench-Releases/issues
AppUpdatesURL=https://github.com/thelinyue/Hephaestus-Workbench-Releases/releases
DefaultDirName={autopf}\HephaestusWorkbench
DefaultGroupName=Hephaestus工作台
DisableProgramGroupPage=no
LicenseFile=..\distribution\public\DISTRIBUTION-LICENSE.md
OutputDir={#OutputDir}
OutputBaseFilename=Hephaestus工作台_v{#MyAppVersion}
SetupIconFile=..\src\HephaestusWorkbench.App\Assets\AppIcon\app-icon.ico
UninstallDisplayName=Hephaestus工作台
UninstallDisplayIcon={app}\HephaestusWorkbench.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=thelinyue
VersionInfoDescription=Hephaestus工作台安装程序
VersionInfoProductName=Hephaestus工作台
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; ignoreversion 只跳过文件版本比较；安装失败时 Inno Setup 仍会自动回滚已替换文件。
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Hephaestus工作台"; Filename: "{app}\HephaestusWorkbench.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Hephaestus工作台"; Filename: "{app}\HephaestusWorkbench.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\HephaestusWorkbench.exe"; Description: "启动 Hephaestus工作台"; Flags: nowait postinstall skipifsilent

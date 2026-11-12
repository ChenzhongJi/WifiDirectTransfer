; Wi-Fi Direct Transfer installer (Inno Setup 6).
; Per-user install, no admin rights. Keep AppId in sync with
; InstallationInfo.InstallerAppId in the application.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef AppLabel
  #define AppLabel AppVersion
#endif

[Setup]
AppId={{8F3C2A1E-5B7D-4E9A-9C41-2D6B8F0A7E13}
AppName=Wi-Fi Direct Transfer
AppVersion={#AppVersion}
AppVerName=Wi-Fi Direct Transfer {#AppVersion}
AppPublisher=ChenzhongJi
AppPublisherURL=https://github.com/ChenzhongJi/WifiDirectTransfer
AppSupportURL=https://github.com/ChenzhongJi/WifiDirectTransfer/issues
AppUpdatesURL=https://github.com/ChenzhongJi/WifiDirectTransfer/releases
DefaultDirName={localappdata}\Programs\WifiDirectTransfer
DefaultGroupName=Wi-Fi Direct Transfer
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\artifacts
OutputBaseFilename=WifiDirectTransfer-{#AppLabel}-setup
SetupIconFile=..\src\WifiDirectTransfer.App\Assets\app.ico
UninstallDisplayIcon={app}\WifiDirectTransfer.App.exe
UninstallDisplayName={cm:ProductDisplayName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Wi-Fi Direct Transfer
VersionInfoCompany=ChenzhongJi
VersionInfoDescription=Wi-Fi Direct Transfer Setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.ProductDisplayName=Wi-Fi Direct 文件传输
english.ProductDisplayName=Wi-Fi Direct Transfer
chinesesimplified.LaunchApp=启动 Wi-Fi Direct 文件传输
english.LaunchApp=Launch Wi-Fi Direct Transfer
chinesesimplified.UninstallDataPrompt=是否同时删除设置、历史记录和未完成的传输文件？
english.UninstallDataPrompt=Delete settings, history and unfinished transfers?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\artifacts\stage-{#AppLabel}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{cm:ProductDisplayName}"; Filename: "{app}\WifiDirectTransfer.App.exe"
Name: "{autodesktop}\{cm:ProductDisplayName}"; Filename: "{app}\WifiDirectTransfer.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WifiDirectTransfer.App.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Registry]
; 开机自启由应用写入 Run 项；卸载时删除，避免残留指向已删除的 exe。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WifiDirectTransfer"; Flags: dontcreatekey uninsdeletevalue

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\WifiDirectTransfer');

    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox(ExpandConstant('{cm:UninstallDataPrompt}') + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;

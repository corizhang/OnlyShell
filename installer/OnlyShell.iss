#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef VariantName
  #define VariantName "SelfContained"
#endif
#ifndef VpkSetupPath
  #define VpkSetupPath "..\artifacts\velopack\self-contained\OnlyShellSelfContained-win-x64-self-contained-Setup.exe"
#endif

[Setup]
AppId=OnlyShell.Setup.{#VariantName}
AppName=OnlyShell
AppVersion={#AppVersion}
AppPublisher=OnlyShell
DefaultDirName={localappdata}\Programs\OnlyShell-{#VariantName}
DefaultGroupName=OnlyShell
DisableProgramGroupPage=yes
DisableDirPage=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
OutputDir=..\artifacts\setup
OutputBaseFilename=OnlyShell-{#VariantName}-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#VpkSetupPath}"; DestDir: "{tmp}"; DestName: "OnlyShell-Velopack-Setup.exe"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\OnlyShell-Velopack-Setup.exe"; Parameters: "--silent --installto ""{app}"""; StatusMsg: "正在安装 OnlyShell..."; Flags: waituntilterminated runhidden
Filename: "{app}\OnlyShell.exe"; Description: "启动 OnlyShell"; Flags: postinstall nowait skipifsilent

; Installer for Dave. Built by "Make Installer.bat" (in the folder above); don't compile this by hand.
; Installs just for the current user (no admin prompt), like Chrome or Discord, with .NET included.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Keep this id the same forever: it's how Windows knows a new installer is an update of Dave.
AppId={{8C4B6D2E-5F7A-4E3B-9D1C-2A6F0E8B7D43}
AppName=Dave
AppVersion={#AppVersion}
AppVerName=Dave {#AppVersion}
AppPublisher=Dave
DefaultDirName={localappdata}\Programs\Dave
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=DaveSetup
SetupIconFile=..\windows\Dave.ico
UninstallDisplayIcon={app}\Dave.exe
UninstallDisplayName=Dave
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 or newer (needed for the voices and screen features)
MinVersion=10.0.19041
; Close a running Dave when updating
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Start clean on updates, so files from an older version can't get in the way
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Dave"; Filename: "{app}\Dave.exe"; Comment: "Dave voice assistant"
Name: "{autodesktop}\Dave"; Filename: "{app}\Dave.exe"; Comment: "Dave voice assistant"; Tasks: desktopicon

[Registry]
; Dave adds "start with Windows" himself when you turn it on in his settings; remove it again when uninstalling.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Dave"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Dave.exe"; Description: "{cm:LaunchProgram,Dave}"; Flags: nowait postinstall skipifsilent
; Automatic updates run the installer silently: start the new Dave right away
Filename: "{app}\Dave.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Dave.exe"; Flags: runhidden; RunOnceId: "StopDave"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
; Your settings and keys (in %APPDATA%\Dave Windows) are kept, so reinstalling doesn't lose them.

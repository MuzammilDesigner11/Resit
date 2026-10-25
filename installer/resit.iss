; Windows installer for Resit (Inno Setup 6). build.ps1 compiles it after publishing
; dist\windows\Resit.exe:  ISCC.exe /DAppVersion=0.1.0 installer\resit.iss
;
; Installs for the current user by default (no admin prompt), with an option to install for
; everyone. The wizard asks whether to add Start menu and desktop shortcuts.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E2C4D1B-8F3A-4B7E-9C21-5A0D3E7F4B19}
AppName=Resit
AppVersion={#AppVersion}
AppVerName=Resit {#AppVersion}
AppPublisher=MuzammilDesigner11
AppPublisherURL=https://github.com/MuzammilDesigner11/Resit
AppSupportURL=https://github.com/MuzammilDesigner11/Resit/issues
AppUpdatesURL=https://github.com/MuzammilDesigner11/Resit/releases
DefaultDirName={autopf}\Resit
DefaultGroupName=Resit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\Resit\Resit.ico
UninstallDisplayIcon={app}\Resit.exe
UninstallDisplayName=Resit
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
OutputDir=..\dist
OutputBaseFilename=Resit-Setup-{#AppVersion}

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start menu shortcut"; GroupDescription: "Shortcuts:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\windows\Resit.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Resit"; Filename: "{app}\Resit.exe"; Comment: "Android emulator"; Tasks: startmenuicon
Name: "{autodesktop}\Resit"; Filename: "{app}\Resit.exe"; Comment: "Android emulator"; Tasks: desktopicon

[Run]
Filename: "{app}\Resit.exe"; Description: "Start Resit"; Flags: nowait postinstall skipifsilent

; Instances (~\.android\avd), the Android SDK and Resit's settings (%APPDATA%\Resit) are kept on
; uninstall, so reinstalling picks up where you left off.

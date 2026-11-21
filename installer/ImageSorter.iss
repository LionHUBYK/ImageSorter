; Image Sorter installer script, for Inno Setup 6 (https://jrsoftware.org).
;
; 1. Publish the app first so dist\ImageSorter.exe exists (see README, "Building from source").
; 2. Compile this file:  ISCC.exe installer\ImageSorter.iss
; 3. The installer is written to dist\ImageSorterSetup.exe.

#define AppName "Image Sorter"
#define AppVersion "1.0.1"
#define AppExe "ImageSorter.exe"

[Setup]
; AppId identifies the app to Windows. Never change it, or upgrades install side by side.
AppId={{7C1E4A52-93B6-4D0F-8E2A-5F6B1D3C9A47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppSupportURL=https://github.com/LionHUBYK/ImageSorter
AppUpdatesURL=https://github.com/LionHUBYK/ImageSorter/releases

; Installs for the current user only: no administrator prompt, files go under
; %LocalAppData%\Programs and the shortcut into that user's Start menu.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes

OutputDir=..\dist
OutputBaseFilename=ImageSorterSetup
SetupIconFile=..\app.ico
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The Start menu shortcut is what makes the app show up in Windows search.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

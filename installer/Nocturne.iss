; Nocturne installer.
;
; Produces a per-user setup that needs no administrator rights. A media player
; has no reason to write outside the user's profile, and asking for elevation
; on every install is the kind of friction that makes people keep using the
; player they already have.
;
; Build:  ISCC.exe /DAppPlatform=x64 installer\Nocturne.iss

#ifndef AppPlatform
  #define AppPlatform "x64"
#endif

#define AppName "Nocturne"
#define AppVersion "0.1.0"
#define AppPublisher "Nocturne"
#define AppExeName "Nocturne.exe"
#define SourceDir "..\artifacts\publish\" + AppPlatform

[Setup]
AppId={{4C1B7E2A-9F3D-4A18-B6C4-2E9F5A7D3B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Nocturne-{#AppVersion}-{#AppPlatform}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Per-user install, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

#if AppPlatform == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif

; Windows 10 1903 is the floor the app manifest and Windows App SDK agree on.
MinVersion=10.0.18362

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "associate"; Description: "Open video files with {#AppName}"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; App Paths, so "nocturne" works from Run and from a terminal without PATH edits.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey

; The capability entry. Registering here rather than writing the extension keys
; directly is what lets Windows offer Nocturne in "Open with" and in Default
; Apps, instead of silently seizing the association.
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; \
    ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; \
    ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Media player"
Root: HKCU; Subkey: "Software\RegisteredApplications"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; \
    Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\{#AppName}.Media"; \
    ValueType: string; ValueName: ""; ValueData: "Media file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#AppName}.Media\DefaultIcon"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#AppName}.Media\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

; One capability line per extension. Kept in step with
; MediaFormats.VideoExtensions in Nocturne.Core.
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "{#AppName}.Media"; Tasks: associate
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "{#AppName}.Media"; Tasks: associate

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings live here once the settings milestone lands. Removing the folder on
; uninstall keeps a reinstall from inheriting a stale schema.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

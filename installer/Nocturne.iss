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
#define ProgId "Nocturne.Media"
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
ChangesAssociations=yes
; Makes Inno raise SHCNE_ASSOCCHANGED after install. Without it Explorer keeps
; its cached associations until the next sign-in, so a user who installs and
; immediately right-clicks a file sees nothing change even though the registry
; is correct.
SetupIconFile=..\src\Nocturne.App\Assets\Nocturne.ico
UninstallDisplayIcon={app}\{#AppExeName}

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

[InstallDelete]
; The crash marker written by RenderGuard. Installing a new build is the one
; moment where "the video pipeline killed the process last time" stops being
; evidence about this build, so the app must be allowed to try again — otherwise
; the fix ships and the app still refuses to draw.
Type: files; Name: "{localappdata}\{#AppName}\render-attempt.marker"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; App Paths, so "nocturne" works from Run and from a terminal without PATH edits.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey

; The capability entry: it is what puts the app on the Default apps page in
; Settings. On its own it does NOT place the app in the "Open with" menu — the
; first release shipped only this and the association appeared to do nothing.
; See the three-mechanism note further down.
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; \
    ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; \
    ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Media player"
Root: HKCU; Subkey: "Software\RegisteredApplications"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; \
    Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; \
    ValueType: string; ValueName: ""; ValueData: "Media file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

; Three separate mechanisms, each feeding a different part of Windows. Missing
; any one of them removes the app from that place only, so they do not collapse
; into each other.
;
;   Capabilities\FileAssociations  -> the Default apps page in Settings
;   <ext>\OpenWithProgIds          -> the right-click "Open with" menu
;   Classes\Applications\<exe>     -> the "Choose another app" dialog
;
; Registered unconditionally, with no checkbox. Appearing in "Open with" is
; harmless and is what someone expects after deliberately installing a player.
; Becoming the DEFAULT is a different matter: since Windows 10 an installer is
; not allowed to do that silently. See [Run] at the end of this file.
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "{#ProgId}"

Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""

[Run]

; Windows 10 and later do not let an installer seize default associations. The
; most an installer can honestly do is open the page where the user makes that
; choice, which beats a checkbox that promises something impossible.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent
Filename: "ms-settings:defaultapps"; Description: "Choose {#AppName} as the default player (opens Windows Settings)"; \
    Flags: postinstall shellexec nowait skipifsilent unchecked

[UninstallDelete]
; Settings live here once the settings milestone lands. Removing the folder on
; uninstall keeps a reinstall from inheriting a stale schema.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

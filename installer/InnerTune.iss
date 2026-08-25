#ifndef SourceDir
  #error SourceDir must point to the prepared installer payload.
#endif

#ifndef OutputDir
  #define OutputDir ".\artifacts"
#endif

#define AppName "InnerTune"
#define AppVersion "1.1.24"
#define AppPublisher "InnerTune"
#define AppExeName "InnerTune.exe"

[Setup]
AppId={{A327AB5E-1221-4FDB-BC5B-E708A8FBD635}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=InnerTune-Setup-{#AppVersion}
SetupIconFile=..\Assets\InnerTune.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=yes
ChangesAssociations=yes
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startupicon"; Description: "Start InnerTune when I sign in"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\setup.ps1"
Type: filesandordirs; Name: "{app}\provider\node_modules"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\InnerTune-{#AppVersion}.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\InnerTune-{#AppVersion}.ico"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\InnerTune-{#AppVersion}.ico"; Tasks: startupicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\innertune"; ValueType: string; ValueName: ""; ValueData: "URL:InnerTune Playlist Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\innertune"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\innertune\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\innertune\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

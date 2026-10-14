#ifndef AppVersion
  #error AppVersion must be supplied by Build-WindowsInstaller.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by Build-WindowsInstaller.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by Build-WindowsInstaller.ps1
#endif

[Setup]
AppId={{C0A4DB9D-C325-47B8-A73C-07A53FA7A08F}
AppName=Le Robocopier
AppVersion={#AppVersion}
AppVerName=Le Robocopier {#AppVersion}
VersionInfoVersion={#AppVersion}.0
DefaultDirName={commonpf}\LeRobocopier
DisableDirPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=LeRobocopier-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayIcon={app}\LeRobocopier.Desktop.exe
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\Le Robocopier"; Filename: "{app}\LeRobocopier.Desktop.exe"; WorkingDir: "{app}"

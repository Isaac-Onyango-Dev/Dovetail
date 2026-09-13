; Inno Setup script for Dovetail.
;
; Produces one DovetailSetup-<version>.exe that installs the whole product: a normal Windows
; install into Program Files, a Start Menu entry, an Add/Remove Programs entry, and an
; uninstaller that reverses what Dovetail did to the machine rather than only deleting files.
;
; Build:  ISCC.exe /DAppVersion=1.1.0 packaging\Dovetail.iss
; Expects build-dist.ps1 to have staged dist\Dovetail first.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

#define AppName        "Dovetail"
#define AppPublisher   "Isaac Onyango"
#define AppURL         "https://github.com/Isaac-Onyango-Dev/Dovetail"
#define AppExe         "Dovetail.exe"
#define SourceDir      "..\dist\Dovetail"

[Setup]
; Never change AppId. It is what lets a later version upgrade this one in place instead of
; installing a second copy beside it.
AppId={{8C4F2E17-6B3A-4D9E-9F21-5A7C0D3B84E6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=DovetailSetup-{#AppVersion}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

; x64 only: the HID and ViGEm interop is compiled x64 and there is no 32-bit build.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Writing to Program Files needs elevation. The app itself runs as a normal user afterwards;
; only the driver setup elevates, and only for its own process.
PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Dovetail.exe,dovetail-engine.exe,dovetail-diag.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole staged folder. It is self-contained, so there is no .NET runtime to install
; first and nothing for the user to go and find.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Straight into first-time setup, which is where the driver install and calibration live.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} and set up my controller"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Files are the easy half. This is the half that matters: the auto-start value, Dovetail's
; own HidHide allow-list entries, and un-hiding any device Dovetail hid. Without it an
; uninstall would leave the controller cloaked and nothing on the machine explaining why.
;
; --keep-profiles is deliberate. Calibration is measured data that costs a full sweep per pad
; to recreate, it lives in %LOCALAPPDATA% rather than here, and an uninstall is not consent to
; destroy it. 'dovetail-engine uninstall run' with no flags would prompt; this cannot.
Filename: "{app}\dovetail-engine.exe"; Parameters: "uninstall run --yes --keep-profiles"; \
    Flags: runhidden waituntilterminated; RunOnceId: "DovetailUndoMachineChanges"

[UninstallDelete]
; Publishing writes these beside the executables; they are ours and nothing else reads them.
Type: filesandordirs; Name: "{app}\profiles"
Type: dirifempty; Name: "{app}"

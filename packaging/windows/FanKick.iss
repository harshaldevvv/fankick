; Windows installer (Inno Setup 6). Built by .github/workflows/release.yml:
;   iscc /DAppVersion=1.2.3 /DArch=x64 packaging\windows\FanKick.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{5B2E7C3A-9F4D-4C71-8E2B-6A1D0F3C9E57}
AppName=FanKick
AppVersion={#AppVersion}
AppVerName=FanKick {#AppVersion}
AppPublisher=harshal.dev
AppPublisherURL=https://harshal.dev
AppSupportURL=https://github.com/harshaldevvv/fankick/issues
AppUpdatesURL=https://github.com/harshaldevvv/fankick/releases
AppCopyright=Copyright (c) 2026 Harshal Narkhede
VersionInfoVersion={#AppVersion}
VersionInfoCompany=harshal.dev
VersionInfoDescription=FanKick Setup
VersionInfoProductName=FanKick
VersionInfoCopyright=Copyright (c) 2026 Harshal Narkhede
DefaultDirName={autopf}\FanKick
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir=..\..\artifacts
OutputBaseFilename=FanKick-Setup-win-{#Arch}
SetupIconFile=..\..\src\FanKick\Assets\fankick.ico
UninstallDisplayIcon={app}\FanKick.exe
UninstallDisplayName=FanKick
PrivilegesRequired=admin
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Asks to close a running FanKick (it hands the fans back to the BIOS on the way out).
AppMutex=FanKick.App
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763

[Tasks]
Name: "autostart"; Description: "Start FanKick when I sign in (recommended: the fans need it running)"
Name: "pawnio"; Description: "Download and install the PawnIO driver (needed for CPU temperatures, motherboard sensors and fan headers)"; Check: NeedsPawnIO
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\..\artifacts\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"
Source: "..\..\PRIVACY.md"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\FanKick"; Filename: "{app}\FanKick.exe"; Comment: "Fan and thermal control"
Name: "{autodesktop}\FanKick"; Filename: "{app}\FanKick.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FanKick.exe"; Parameters: "--set-autostart on"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Setting up start at sign-in..."
Filename: "{app}\FanKick.exe"; Parameters: "--no-elevate"; Description: "Start FanKick now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\FanKick.exe"; Parameters: "--set-autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[Code]
const
  PawnIOUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe';
  PawnIOSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';

function NeedsPawnIO: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

// Fetches the official signed PawnIO installer (hash-checked) and runs it so the user sees and accepts it.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('pawnio') then
  begin
    try
      DownloadTemporaryFile(PawnIOUrl, 'PawnIO_setup.exe', PawnIOSha256, nil);
      Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '', '', SW_SHOW, ewWaitUntilTerminated, Code);
    except
      MsgBox('PawnIO could not be downloaded. You can install it later from https://pawnio.eu.', mbInformation, MB_OK);
    end;
  end;
end;

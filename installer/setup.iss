; ============================================================================
;  Jalyro Convert - Inno Setup script (Phase 1)
;
;  Per-user by default, no elevation. This is deliberate:
;  Add-AppxPackage is inherently a per-user operation, so a machine-wide
;  install would leave the app installed for everyone but the context menu
;  present for exactly one account.
;
;  Build with:  build\make-installer.cmd
; ============================================================================

#define AppName        "Jalyro Convert"
#define AppShortName   "JalyroConvert"
#define AppVersion     "0.9.32"
#define AppPublisher   "Petrus Sprenkels"
#define PackageName    "Jalyro.Convert"

[Setup]
AppId={{492D454B-A289-4D83-B82C-B9C3A5A1A260}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; Windows 11 22H2. There is no Windows 10 legacy IContextMenu handler, so on
; Windows 10 the product would install and provide no menu integration at all.
MinVersion=10.0.22621

; No elevation. Everything lives under the user profile.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

UninstallDisplayIcon={app}\Jalyro.Convert.Host.exe
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=..\installer\third-party-notice.txt
SetupIconFile=..\src\Shell\convert.ico

; Shown before installation. Distributing GPL ffmpeg alongside MIT code is the
; standard aggregation position, but it carries obligations - stating it plainly
; in the installer is one of them.
[Messages]
BeveledLabel=Free and open source

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\stage\Jalyro.Convert.Shell.dll";   DestDir: "{app}"; Flags: ignoreversion
Source: "..\stage\Jalyro.Convert.Host.exe";    DestDir: "{app}"; Flags: ignoreversion
Source: "..\stage\Jalyro.Convert.Host.dll";    DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\stage\Jalyro.Convert.Worker.exe";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\stage\Jalyro.Convert.Worker.dll";  DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\stage\NetVips.dll";                   DestDir: "{app}"; Flags: ignoreversion
; libvips native binaries land under runtimes\win-x64\native from the NuGet package
Source: "..\stage\runtimes\*";                   DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\stage\*.dll";                         DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\stage\*.json";                        DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\stage\Jalyro.Convert.msix";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\stage\Assets\*";                      DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs
; ffmpeg: audio, video, and HEIC decoding. GPL - its licence files ship with it.
Source: "..\stage\ffmpeg\*";                      DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Jalyro.Convert.Host.exe"; Parameters: "--resident"
Name: "{group}\{#AppName} Settings"; Filename: "{app}\Jalyro.Convert.Host.exe"; Parameters: "--settings"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Start the Host at login, WITHOUT package identity.
;
; This is the whole point of the v0.2.1 architecture: a Host started by the
; shell (Explorer -> Run key) is unpackaged, so its storage is the real one.
; A Host spawned by the COM surrogate would inherit identity and be virtualized.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "JalyroConvert"; \
  ValueData: """{app}\Jalyro.Convert.Host.exe"" --resident"; \
  Flags: uninsdeletevalue

[Run]
Filename: "{app}\Jalyro.Convert.Host.exe"; Parameters: "--resident"; \
  Description: "Start Jalyro Convert now"; Flags: postinstall nowait skipifsilent

[Code]

// ---------------------------------------------------------------------------
// Sparse package registration.
//
// Runs PowerShell hidden and waits. Not elevated - Add-AppxPackage is per-user.
// ---------------------------------------------------------------------------
// Escapes a value for a PowerShell single-quoted string. A profile such as
// C:\Users\O'Brien would otherwise terminate the string early and break
// package registration.
function PsQuote(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

function RunPowerShell(const Script: string; var ResultCode: Integer): Boolean;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RegisterSparsePackage();
var
  ResultCode: Integer;
  Script: string;
begin
  Script :=
    'Add-AppxPackage -Path ''' + PsQuote(ExpandConstant('{app}\{#PackageName}.msix')) +
    ''' -ExternalLocation ''' + PsQuote(ExpandConstant('{app}')) + '''';

  if not RunPowerShell(Script, ResultCode) or (ResultCode <> 0) then
    MsgBox('The context menu entry could not be registered (code ' +
           IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'The application is installed, but "Convert to" will not appear ' +
           'until registration succeeds. Launching the app will retry ' +
           'automatically.',
           mbError, MB_OK);
end;

procedure UnregisterSparsePackage();
var
  ResultCode: Integer;
begin
  // MUST happen before files are replaced or removed: the COM surrogate holds
  // the DLL open and the copy will fail with a sharing violation otherwise.
  // This is the trap VS Code documented in microsoft/vscode#151186.
  RunPowerShell(
    'Get-AppxPackage -Name ''{#PackageName}'' | Remove-AppxPackage',
    ResultCode);
end;

// ---------------------------------------------------------------------------
// Detect the classic context menu override.
//
// Phase 0 finding #1: while this CLSID exists under HKCU, the Windows 11
// primary menu is disabled and NO IExplorerCommand handler can appear.
// Installing without warning would look like our bug.
// ---------------------------------------------------------------------------
function ClassicMenuOverrideIsSet(): Boolean;
begin
  Result := RegKeyExists(HKEY_CURRENT_USER,
    'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}');
end;

procedure WarnAboutClassicMenu();
begin
  if not ClassicMenuOverrideIsSet() then
    Exit;

  if MsgBox(
      'Windows is currently configured to use the classic (Windows 10) ' +
      'context menu.' + #13#10#13#10 +
      'While that setting is active, "Convert to" cannot appear in the ' +
      'right-click menu at all.' + #13#10#13#10 +
      'Switch back to the Windows 11 context menu now?',
      mbConfirmation, MB_YESNO) = IDYES then
  begin
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
      'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}');
  end;
end;

// ---------------------------------------------------------------------------
// Explorer restart.
//
// Sequential with a pause. A one-line kill-and-start races: start can fire
// while the old shell is still tearing down, and the taskbar never returns.
// ---------------------------------------------------------------------------
procedure RestartExplorer();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im explorer.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Exec(ExpandConstant('{win}\explorer.exe'), '',
       '', SW_SHOW, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Release the DLL lock before any file is written.
    UnregisterSparsePackage();
  end
  else if CurStep = ssPostInstall then
  begin
    WarnAboutClassicMenu();
    RegisterSparsePackage();

    // Deliberately NOT restarting Explorer automatically.
    //
    // On a machine with many shell extensions the restart took ~30 seconds
    // with a dead taskbar - which reads as "the installer broke my PC", and
    // the user blames us. Offer it instead, and say what the alternative is.
    if MsgBox('The context menu entry is registered.' + #13#10#13#10 +
              'File Explorer needs to restart before "Convert to" appears. ' +
              'This takes a few seconds, and any open Explorer windows will ' +
              'close.' + #13#10#13#10 +
              'Restart File Explorer now? (Choosing No means it appears after ' +
              'your next sign-in.)',
              mbConfirmation, MB_YESNO) = IDYES then
      RestartExplorer();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    UnregisterSparsePackage();
    Sleep(1000);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RestartExplorer();
  end;
end;

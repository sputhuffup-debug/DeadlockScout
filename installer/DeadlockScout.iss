; DeadlockScout installer (Inno Setup 6). Built by CI and the release workflow from `dotnet publish` output:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=<publish folder> /O<output folder> installer\DeadlockScout.iss
; Per-user: no administrator rights, installed under %LOCALAPPDATA%\Programs\DeadlockScout. Settings, cache and logs live
; in %LOCALAPPDATA%\DeadlockScout and are kept on uninstall unless the player ticks the box to remove them.
; The in-app updater runs this silently after checking it against the signed release manifest (docs/release.md):
;   DeadlockScout-Setup-1.2.3.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
; Never change AppId: it is how upgrades and the uninstaller find an existing install.
AppId={{67A422B7-C203-4E51-83E1-5CA9038B21EA}
AppName=DeadlockScout
AppVersion={#AppVersion}
AppVerName=DeadlockScout {#AppVersion}
AppPublisher=DeadlockScout
AppPublisherURL=https://github.com/sputhuffup-debug/DeadlockScout
AppSupportURL=https://github.com/sputhuffup-debug/DeadlockScout/blob/main/docs/troubleshooting.md
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DeadlockScout
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=DeadlockScout
UninstallDisplayIcon={app}\DeadlockScout.exe
UninstallDisplayName=DeadlockScout
SetupIconFile=..\src\DeadlockScout.App\Assets\app.ico
OutputBaseFilename=DeadlockScout-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app holds this mutex while it runs: setup asks to close it rather than replacing files in use.
AppMutex=DeadlockScout.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start DeadlockScout with Windows (it sits in the tray)"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DeadlockScout"; Filename: "{app}\DeadlockScout.exe"
Name: "{autodesktop}\DeadlockScout"; Filename: "{app}\DeadlockScout.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeadlockScout"; \
  ValueData: """{app}\DeadlockScout.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; After a normal install the last page offers to start it; after a silent update it starts again only when asked.
Filename: "{app}\DeadlockScout.exe"; Description: "Start DeadlockScout"; Flags: nowait postinstall skipifsilent
Filename: "{app}\DeadlockScout.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[Code]
const
  RuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

{ True when any .NET 10 Desktop Runtime (x64) is installed, machine-wide or for this user. }
function HasDesktopRuntime10(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, RuntimeKey, Names) or RegGetValueNames(HKCU, RuntimeKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup(): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if HasDesktopRuntime10() then Exit;
  { Never downloads anything itself: it points to Microsoft's own page. }
  if WizardSilent() then
  begin
    Log('The .NET 10 Desktop Runtime (x64) is not installed; the update is not installed.');
    Result := False;
    Exit;
  end;
  if MsgBox('DeadlockScout needs the .NET 10 Desktop Runtime (x64), which is not installed.' + #13#10#13#10 +
            'Open Microsoft''s download page now? Install the "Desktop Runtime" for x64, then run this setup again.',
            mbConfirmation, MB_YESNO) = IDYES then
    ShellExec('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, Code);
  Result := False;
end;

function RelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent() and (Pos('/RELAUNCH', Uppercase(GetCmdTail())) > 0);
end;

{ Uninstall: keep settings, cache and logs unless the player asks to remove them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\DeadlockScout');
  if not DirExists(Data) then Exit;
  if UninstallSilent() then Exit;
  if MsgBox('Also remove your DeadlockScout settings, cache and logs (' + Data + ')?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;

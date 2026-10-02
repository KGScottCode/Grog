; Grog per-user Windows installer.
; No admin: installs to %LocalAppData%\Grog so settings and future in-app updates can write beside
; the binary. The plain ZIP stays the portable option for people who want no installer at all.
;
; Build:  iscc /DAppVersion=0.1.0 /DSourceDir=..\..\publish packaging\windows\Grog.iss
; Output: dist\Grog-win-x64-setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

#define AppName    "Grog"
#define AppExeName "Grog.exe"
#define CliExeName "grogcli.exe"
#define Publisher  "Kevin G. Scott"
#define AppUrl     "https://github.com/KGScottCode/Grog"

[Setup]
; AppId is the upgrade identity. It must NEVER change or an upgrade installs alongside the old copy
; instead of over it.
AppId={{B391152D-77A6-4A52-816C-303E09C09210}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; lowest = never prompt for UAC. The whole point of the per-user layout.
PrivilegesRequired=lowest
; Empty (not "none") is how Inno disallows overrides: no /ALLUSERS switch, no elevation dialog.
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; win-x64 publish only. Refuse ARM/x86 rather than install a binary that cannot start.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir={#OutputDir}
OutputBaseFilename=Grog-win-x64-setup
SetupIconFile=..\..\src\Grog.App\Assets\grog-amber.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

LicenseFile=..\..\LICENSE

; An upgrade over a running Grog silently leaves stale files behind. Ask instead.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

; Broadcasts WM_SETTINGCHANGE so a new shell sees the PATH edit without a sign-out.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; On by default: a desktop icon is what people expect from an installer, it is visible the moment
; setup finishes, and undoing it is deleting an icon.
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
; Opt-in, and per-user (HKCU) so it still needs no admin. Off by default: editing someone's PATH is
; not something an installer should do without being asked. The NOTE under this box is a real label
; built in [Code]: a Description is one wrapping block and cannot force its own line break.
Name: "addtopath"; Description: "Add &grogcli to PATH so it runs from any folder"; \
  GroupDescription: "Command line (optional):"; \
  Flags: unchecked

[Registry]
; Append only when {app} is not already on PATH, or a reinstall stacks duplicates.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
  Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Files]
; Everything the publish step staged: app, CLI, shared runtime, LICENSE, notices, README.
; EXCEPT GrogData: that folder is the portable-mode marker (state lives beside the exe when present), and it
; belongs to the ZIP only. An installed Grog keeps its state in %APPDATA%; shipping GrogData here would
; silently flip every installed copy to portable.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\GrogData\*,\GrogData"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PathNote: TNewStaticText;

// The PATH caveat as its own line under the checkbox. A [Tasks] Description is a single wrapping
// block, so the break has to come from a real control; PATH is the last task, so a label under the
// list sits directly beneath it.
procedure InitializeWizard();
begin
  PathNote := TNewStaticText.Create(WizardForm);
  PathNote.Parent := WizardForm.SelectTasksPage;
  PathNote.AutoSize := False;
  PathNote.WordWrap := True;
  PathNote.Height := ScaleY(30);
  PathNote.Visible := False;
  PathNote.Caption := 'NOTE: Only needed for the grogcli command-line tool in a terminal or a' +
                      ' scheduled task. Not required to use the Grog app.';
end;

const
  LB_GETITEMHEIGHT = $01A1;

// Positioned here, not in InitializeWizard: the tasks are not populated until the page is shown.
// Row heights are PER ITEM, not uniform: NewCheckListBox.pas MeasureItem gives a wrapping row more
// than MinItemHeight and stores it via LB_SETITEMHEIGHT, so the true content height is the sum of
// LB_GETITEMHEIGHT over the rows. Shrinking the list to that is what turns "below the list" into
// "below the last checkbox" rather than the bottom of the page.
procedure CurPageChanged(CurPageID: Integer);
var
  I, H: Integer;
begin
  if CurPageID <> wpSelectTasks then exit;

  H := 0;
  for I := 0 to WizardForm.TasksList.Items.Count - 1 do
    H := H + SendMessage(WizardForm.TasksList.Handle, LB_GETITEMHEIGHT, I, 0);

  WizardForm.TasksList.Height := H + ScaleY(4);
  PathNote.Left := WizardForm.TasksList.Left + ScaleX(20);
  PathNote.Width := WizardForm.TasksList.Width - ScaleX(20);
  PathNote.Top := WizardForm.TasksList.Top + WizardForm.TasksList.Height + ScaleY(2);
  PathNote.Visible := True;
end;

// True when {app} is not already a PATH entry. Compared with separators on both ends so
// C:\Foo\Grog does not match C:\Foo\Grog2.
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

// Surgical: cut OUR entry out and write the rest back. Never uninsdeletevalue on Path, which would
// delete the user's entire PATH along with ours.
procedure RemoveFromPath(Param: string);
var
  OrigPath, NewPath: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then exit;
  NewPath := ';' + OrigPath + ';';
  P := Pos(';' + Uppercase(Param) + ';', Uppercase(NewPath));
  if P = 0 then exit;
  Delete(NewPath, P, Length(Param) + 1);
  NewPath := Copy(NewPath, 2, Length(NewPath) - 2);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', NewPath);
end;

// Mirrors GrogPaths.ResolveConfigDir: GROG_CONFIG_DIR wins, else the Roaming profile folder. The
// uninstaller must not assume Roaming or it would offer to delete a folder the app is not using.
function GrogConfigDir(): string;
begin
  Result := GetEnv('GROG_CONFIG_DIR');
  if Result = '' then Result := ExpandConstant('{userappdata}\Grog');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Msg: string;
begin
  if CurUninstallStep <> usPostUninstall then exit;

  RemoveFromPath(ExpandConstant('{app}'));

  // Opt-in and defaulted to No. Settings and the manifest are the user's, so removing them is a
  // deliberate answer to a plain question, never a side effect of uninstalling.
  DataDir := GrogConfigDir();
  if not DirExists(DataDir) then exit;

  // No line may START with '#': the preprocessor reads that as a directive, not as a Pascal char code.
  Msg := 'Also remove Grog''s settings and library record?' + #13#10 + #13#10
       + DataDir + #13#10 + #13#10
       + 'This deletes your preferences, sign-in tokens and the record of what has been backed up.'
       + #13#10 + #13#10
       + 'NOTE: Your backed-up game files are stored in your backup folder, not here. They are never '
       + 'touched by the uninstaller. Keeping this folder lets a reinstall pick up where you left off '
       + 'without rescanning.';

  if MsgBox(Msg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;

[UninstallDelete]
; Config, manifest and grog.log all live in %APPDATA%\Grog (GrogPaths), never under {app}, so the
; uninstaller touches nothing of the user's. Removing a backup manifest is not the uninstaller's job.
Type: dirifempty; Name: "{app}"

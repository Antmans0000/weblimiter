; WebLimiter installer (Inno Setup 6).
; Build: tools\innosetup\ISCC.exe installer\WebLimiter.iss  (after "dotnet publish" into publish\app,
; see installer\build.ps1). Produces installer\Output\WebLimiter-Setup-<version>.exe.

#define AppName "WebLimiter"
#define AppVersion "0.1.0"
#define AppExe "WebLimiter.exe"
#define ServiceName "WebLimiter"
#define PublishDir "..\publish\app"

[Setup]
AppId={{6B0E4D2A-8C61-4F1E-9B57-3E2D7A9C5F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\WebLimiter.App\Assets\weblimiter.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=WebLimiter-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; The app and its service are stopped by [Code] below, not by the Restart Manager.
CloseApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
fr.DesktopIcon=Créer une icône sur le Bureau
en.DesktopIcon=Create a desktop shortcut
fr.LaunchApp=Lancer WebLimiter
en.LaunchApp=Launch WebLimiter
fr.InstallingService=Installation du service WebLimiter…
en.InstallingService=Installing the WebLimiter service…
fr.ServiceFailed=Le service WebLimiter n'a pas pu être installé (code %1). Vous pourrez l'installer depuis l'onglet Paramètres de WebLimiter.
en.ServiceFailed=The WebLimiter service could not be installed (code %1). You can install it from the Settings tab of WebLimiter.
fr.RemoveData=Supprimer aussi les règles et l'historique de consommation (dossier %1) ?
en.RemoveData=Also delete the rules and usage history (folder %1)?

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,WinDivert64.sys"; Flags: ignoreversion recursesubdirs createallsubdirs
; The WinDivert driver may still be loaded from the previous install: replace it at reboot if needed.
Source: "{#PublishDir}\WinDivert64.sys"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[Code]
const
  ServiceStopped = 1;

function Run(const Exe, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\') + Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function ServiceExists(): Boolean;
begin
  Result := Run('sc.exe', 'query {#ServiceName}') = 0;
end;

{ Waits until "sc query" no longer reports the service as running or pending. }
procedure WaitForServiceStop();
var
  I, Code: Integer;
  Output: AnsiString;
  OutFile: String;
begin
  OutFile := ExpandConstant('{tmp}\svcstate.txt');
  for I := 1 to 40 do
  begin
    Exec(ExpandConstant('{cmd}'), '/C sc query {#ServiceName} > "' + OutFile + '"', '', SW_HIDE,
      ewWaitUntilTerminated, Code);
    if not LoadStringFromFile(OutFile, Output) then Exit;
    if (Pos('STOPPED', Output) > 0) or (Pos('1060', Output) > 0) then Exit;
    Sleep(250);
  end;
end;

{ Before copying files: close the interface and remove a previous service, whatever its path. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Run('taskkill.exe', '/F /IM {#AppExe}');
  if ServiceExists() then
  begin
    Run('sc.exe', 'stop {#ServiceName}');
    WaitForServiceStop();
    Run('sc.exe', 'delete {#ServiceName}');
  end;
  { The service process may need a moment to release its files. }
  Sleep(1000);
  Result := '';
end;

procedure InstallService();
var
  Code: Integer;
  BinPath: String;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingService');
  BinPath := '"\"' + ExpandConstant('{app}\{#AppExe}') + '\" --service"';
  Code := Run('sc.exe', 'create {#ServiceName} binPath= ' + BinPath +
    ' start= auto DisplayName= "{#AppName}" depend= BFE');
  if Code <> 0 then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
    Exit;
  end;
  Run('sc.exe', 'description {#ServiceName} "Surveille, limite et bloque le trafic reseau par application."');
  Run('sc.exe', 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
  Run('sc.exe', 'start {#ServiceName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallService();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{commonappdata}\{#AppName}');
  if DirExists(DataDir) and
     (SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveData'), [DataDir]), mbConfirmation, MB_YESNO, IDNO) = IDYES) then
    DelTree(DataDir, True, True, True);
end;

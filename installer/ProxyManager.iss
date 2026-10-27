; Inno Setup 6 script. Built by scripts/build-release.ps1, which passes AppVersion and PublishDir.

#define AppName "Proxy Manager"
; TODO: set the real publisher (also <Company> in src/ProxyManager/ProxyManager.csproj)
#define AppPublisher "Proxy Manager"
#define AppExe "ProxyManager.exe"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
; Never change AppId: it identifies the product for upgrades and uninstall.
AppId={{34E8CC0B-5364-4ABB-9F04-D3DC9BE8F656}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; The app itself needs admin rights (TUN adapter), so install per-machine.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=ProxyManager-{#AppVersion}-setup
SetupIconFile=..\src\ProxyManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Running instances are closed in [Code] (Restart Manager can't close a tray app reliably).
CloseApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.RemoveSettings=Удалить настройки Proxy Manager (список прокси и правил)?
en.RemoveSettings=Remove Proxy Manager settings (proxies and rules)?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Drop files of a previous core version that the new one doesn't ship.
Type: filesandordirs; Name: "{app}\core"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Autostart tasks created by the app (one per user who enabled it).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-ScheduledTask -TaskPath '\ProxyManager\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false"""; \
  Flags: runhidden; RunOnceId: "RemoveAutostartTasks"

[Code]
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  { Killing the app also stops its sing-box core (they share a job object). }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\ProxyManager'), True, True, True);
end;

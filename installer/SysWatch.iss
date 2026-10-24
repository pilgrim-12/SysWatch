; Установщик SysWatch (Inno Setup 6). Собирается скриптом scripts\build-installer.ps1 или в GitHub Actions.
; Файлы берутся из publish\app — туда в одну папку публикуются агент и окно (self-contained, .NET ставить не нужно).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\app"
#endif

[Setup]
AppId={{6F3D2A8E-4B7C-4E1A-9C55-2E8F1B7A4D93}
AppName=SysWatch
AppVersion={#AppVersion}
AppVerName=SysWatch {#AppVersion}
AppPublisher=pilgrim-12
AppPublisherURL=https://github.com/pilgrim-12/SysWatch
AppSupportURL=https://github.com/pilgrim-12/SysWatch/issues
DefaultDirName={autopf}\SysWatch
DefaultGroupName=SysWatch
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\SysWatch.App\syswatch.ico
UninstallDisplayIcon={app}\SysWatch.exe
UninstallDisplayName=SysWatch
OutputDir=Output
OutputBaseFilename=SysWatch-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no
ShowLanguageDialog=auto
VersionInfoVersion={#AppVersion}
VersionInfoProductName=SysWatch

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
en.Desktop=Create a desktop shortcut
ru.Desktop=Создать ярлык на рабочем столе
es.Desktop=Crear un acceso directo en el escritorio
fr.Desktop=Créer un raccourci sur le bureau
en.Launch=Start SysWatch
ru.Launch=Запустить SysWatch
es.Launch=Iniciar SysWatch
fr.Launch=Lancer SysWatch
en.Configuring=Registering the SysWatch agent service and tray autostart…
ru.Configuring=Регистрирую службу агента SysWatch и автозапуск в трее…
es.Configuring=Registrando el servicio del agente de SysWatch y el inicio en la bandeja…
fr.Configuring=Enregistrement du service de l'agent SysWatch et du démarrage dans la zone de notification…
en.RemoveData=Also delete the collected history (C:\ProgramData\SysWatch)?%n%nChoose "No" to keep it for a future reinstall.
ru.RemoveData=Удалить и собранную историю (C:\ProgramData\SysWatch)?%n%n«Нет» — оставить её на случай повторной установки.
es.RemoveData=¿Eliminar también el historial recopilado (C:\ProgramData\SysWatch)?%n%nElige «No» para conservarlo para una futura reinstalación.
fr.RemoveData=Supprimer aussi l'historique collecté (C:\ProgramData\SysWatch) ?%n%nChoisissez « Non » pour le conserver en vue d'une réinstallation.
en.NoWebView=Microsoft Edge WebView2 Runtime was not found. SysWatch uses it to draw its window.%n%nIt is built into Windows 11 and most Windows 10 PCs. If the window stays empty after installation, install it from:%nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703%n%nContinue installation?
ru.NoWebView=Не найден Microsoft Edge WebView2 Runtime — SysWatch рисует через него своё окно.%n%nОн встроен в Windows 11 и в большинство Windows 10. Если после установки окно будет пустым, установите его отсюда:%nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703%n%nПродолжить установку?
es.NoWebView=No se encontró Microsoft Edge WebView2 Runtime, que SysWatch usa para dibujar su ventana.%n%nViene incluido en Windows 11 y en la mayoría de equipos con Windows 10. Si la ventana aparece vacía tras la instalación, instálalo desde:%nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703%n%n¿Continuar con la instalación?
fr.NoWebView=Microsoft Edge WebView2 Runtime est introuvable. SysWatch l'utilise pour afficher sa fenêtre.%n%nIl est intégré à Windows 11 et à la plupart des PC Windows 10. Si la fenêtre reste vide après l'installation, installez-le depuis :%nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703%n%nContinuer l'installation ?

[Tasks]
Name: "desktopicon"; Description: "{cm:Desktop}"; Flags: unchecked

[InstallDelete]
; Раскладка старой установки скриптом: Program Files\SysWatch\Agent и \App
Type: filesandordirs; Name: "{app}\Agent"
Type: filesandordirs; Name: "{app}\App"
; Сборки интерфейса меняют имена файлов — старые не оставляем
Type: filesandordirs; Name: "{app}\wwwroot\assets"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "setup.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "remove.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SysWatch"; Filename: "{app}\SysWatch.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SysWatch.exe"
Name: "{autodesktop}\SysWatch"; Filename: "{app}\SysWatch.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SysWatch.exe"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\setup.ps1"" -AppDir ""{app}"""; StatusMsg: "{cm:Configuring}"; Flags: runhidden waituntilterminated
; Запуск через задачу планировщика (тот же способ, что при входе в Windows; задача стартует SysWatch в трее),
; затем повторный запуск SysWatch.exe — он просит уже работающую копию открыть окно. Иначе после «Готово» был бы только значок в трее.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""schtasks /Run /TN SysWatch | Out-Null; Start-Sleep -Seconds 2; Start-Process -FilePath '{app}\SysWatch.exe'"""; Description: "{cm:Launch}"; Flags: postinstall runhidden nowait

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\remove.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSysWatch"

[Code]
function WebView2Installed(): Boolean;
var
  V: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2Installed() then
    Result := MsgBox(CustomMessage('NoWebView'), mbConfirmation, MB_YESNO) = IDYES;
end;

{ Перед копированием файлов: закрыть окно и остановить агента, иначе их файлы заняты. }
procedure StopRunning();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SysWatch.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop SysWatchAgent', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(3000);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then StopRunning();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if (not UninstallSilent()) and (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{commonappdata}\SysWatch'), True, True, True);
end;

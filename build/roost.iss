; El instalador de Roost para Windows (Inno Setup 6). Lo compila el workflow Release:
;
;   ISCC /DAppVersion=0.1.1 /DNumericVersion=0.1.1 /DSourceDir=<carpeta publicada> /DOutputDir=<salida> build\roost.iss
;
; <carpeta publicada> es la de build\publish-windows.ps1 (lo mismo que lleva el zip).
;
; Opcionales (sin ellos compila exactamente igual que antes):
;   /DVcRedist=<ruta a vc_redist.x64.exe>  lo incrusta y lo instala en silencio SOLO si falta o es anterior
;                                          al 14.50 (Apache); PHP, MariaDB y MySQL también lo necesitan.
;   /DSignTool=<nombre> y /S<nombre>=<orden> $f  firman el setup y el desinstalador (unins000.exe) al
;                                          compilar, con el <orden> que ISCC ejecuta por cada fichero
;                                          (build\sign.ps1 en el workflow). Sin firma, Smart App Control
;                                          bloquea justo el setup, aunque los exe de dentro vayan firmados.
;
; Instala para toda la máquina, en Program Files: roost-helper.exe se ejecuta elevado (UAC) y no
; puede vivir en una carpeta que pueda escribir cualquier programa del usuario (ROOST.md §10,
; riesgo 4). Los datos (C:\roost, con los proyectos en C:\roost\www, y %APPDATA%\Roost) no
; los toca: ni al actualizar ni al desinstalar. Antes de sustituir o borrar ficheros, para el stack (roost down): roostd tiene
; abiertas las dll.
;
; Actualizar desde la app (ROOST.md, decisión 33): la app lo lanza con
;   /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /ROOSTRELAUNCH=1 [/ROOSTSTART=1]
; (UpdateInstaller.SetupArguments) y sale. Con /ROOSTRELAUNCH=1, al terminar vuelve a abrir
; roost-desktop.exe --updated (y --start si el stack estaba en marcha) como el usuario original: la
; entrada postinstall de siempre no sirve en silencioso (skipifsilent). roost update --install hace lo
; mismo sin la app: solo /ROOSTSTART=1, y entonces se lanza roost up.

#ifndef AppVersion
  #error Falta /DAppVersion
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef SourceDir
  #error Falta /DSourceDir (la carpeta de build\publish-windows.ps1)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Fijo para siempre: es lo que hace que una versión nueva actualice la instalada.
AppId={{6222FA9C-D7F6-4B33-A97E-F02D6A2BF1F4}
AppName=Roost
AppVersion={#AppVersion}
AppVerName=Roost {#AppVersion}
AppPublisher=MYA Consulting
AppPublisherURL=https://get-roost.com
AppSupportURL=https://github.com/get-roost/roost/issues
AppUpdatesURL=https://github.com/get-roost/roost/releases
AppCopyright=Copyright (c) 2026 MYA Consulting
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\Roost
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=roost-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\branding\icons\roost.ico
UninstallDisplayIcon={app}\roost-desktop.exe
UninstallDisplayName=Roost
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no
#ifdef SignTool
; Firma el setup y el desinstalador con el /S<nombre>=... que recibe ISCC (ver arriba).
SignTool={#SignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.AddToPath=Añadir «roost» a la línea de comandos (PATH del sistema)
en.AddToPath=Add "roost" to the command line (system PATH)
es.CommandLine=Línea de comandos:
en.CommandLine=Command line:
es.InstallingVcRedist=Instalando Visual C++ Redistributable (lo necesitan PHP, MariaDB, MySQL y Apache)...
en.InstallingVcRedist=Installing Visual C++ Redistributable (PHP, MariaDB, MySQL and Apache need it)...

[Tasks]
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:CommandLine}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef VcRedist
; Solo se extrae a {tmp} si hace falta (InstallVcRedist): no se queda en {app}.
Source: "{#VcRedist}"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\Roost"; Filename: "{app}\roost-desktop.exe"
Name: "{autodesktop}\Roost"; Filename: "{app}\roost-desktop.exe"; Tasks: desktopicon

[Run]
; Como el usuario que instala, no elevado: si no, la app y el stack correrían como administrador.
Filename: "{app}\roost-desktop.exe"; Description: "{cm:LaunchProgram,Roost}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Actualización silenciosa lanzada desde la app (/ROOSTRELAUNCH=1): vuelve a abrir Roost, con --start si el stack estaba en marcha.
Filename: "{app}\roost-desktop.exe"; Parameters: "{code:RelaunchArgs}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunchApp
; Sin app (roost update --install con el stack en marcha): solo volver a levantar el stack.
Filename: "{app}\roost.exe"; Parameters: "up"; Flags: nowait runhidden runasoriginaluser; Check: ShouldStartStackOnly

[UninstallRun]
Filename: "{app}\roost.exe"; Parameters: "down"; Flags: runhidden waituntilterminated; RunOnceId: "RoostDown"

[Code]
const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  // Donde apunta el propio redistribuible lo que hay instalado (VcRedist.cs lee la misma clave).
  VcKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';

function PathHas(const Paths, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(const Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Paths) then Paths := '';
  if PathHas(Paths, Dir) then exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(const Dir: string);
var
  Paths: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Paths) then exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then exit;
  Delete(Paths, P, Length(Dir) + 1);
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Copy(Paths, 2, Length(Paths) - 2));
end;

// Los parámetros propios de la línea de órdenes (/ROOSTRELAUNCH=1, /ROOSTSTART=1), vacíos si no vienen.
function RoostParam(const Name: string): string;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
end;

function ShouldRelaunchApp: Boolean;
begin
  Result := WizardSilent and (RoostParam('ROOSTRELAUNCH') = '1');
end;

function ShouldStartStackOnly: Boolean;
begin
  Result := WizardSilent and (RoostParam('ROOSTSTART') = '1') and not ShouldRelaunchApp;
end;

function RelaunchArgs(Param: string): string;
begin
  Result := '--updated';
  if RoostParam('ROOSTSTART') = '1' then Result := Result + ' --start';
end;

// Falta el VC++ Redistributable x64, o es anterior al 14.50 (Apache de Apache Lounge, VS18).
function VcRedistMissing: Boolean;
var
  Installed, Major, Minor: Cardinal;
begin
  Result := True;
  if not RegQueryDWordValue(HKLM64, VcKey, 'Installed', Installed) or (Installed = 0) then exit;
  if not RegQueryDWordValue(HKLM64, VcKey, 'Major', Major) or not RegQueryDWordValue(HKLM64, VcKey, 'Minor', Minor) then exit;
  Result := (Major < 14) or ((Major = 14) and (Minor < 50));
end;

#ifdef VcRedist
procedure InstallVcRedist;
var
  Code: Integer;
  Exe: string;
begin
  if not VcRedistMissing then exit;
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingVcRedist');
  ExtractTemporaryFile('{#ExtractFileName(VcRedist)}');
  Exe := ExpandConstant('{tmp}\{#ExtractFileName(VcRedist)}');
  if not Exec(Exe, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('vc_redist: no se pudo ejecutar: ' + SysErrorMessage(Code))
  // 1638: ya hay uno más nuevo; 3010: instalado, pide reiniciar (PHP arranca sin reiniciar).
  else if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
    Log('vc_redist: salio con el codigo ' + IntToStr(Code))
  else
    Log('vc_redist: instalado (codigo ' + IntToStr(Code) + ')');
end;
#endif

// Al actualizar, parar el stack antes de sustituir ficheros: roostd tiene abiertas las dll.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Exe: string;
begin
  Result := '';
  Exe := ExpandConstant('{app}\roost.exe');
  if FileExists(Exe) then
    Exec(Exe, 'down', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
#ifdef VcRedist
    InstallVcRedist;
#endif
    if WizardIsTaskSelected('addtopath') then
      AddToPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;

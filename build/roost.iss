; El instalador de Roost para Windows (Inno Setup 6). Lo compila el workflow Release:
;
;   ISCC /DAppVersion=0.1.1 /DNumericVersion=0.1.1 /DSourceDir=<carpeta publicada> /DOutputDir=<salida> build\roost.iss
;
; <carpeta publicada> es la de build\publish-windows.ps1 (lo mismo que lleva el zip).
;
; Instala para toda la máquina, en Program Files: roost-helper.exe se ejecuta elevado (UAC) y no
; puede vivir en una carpeta que pueda escribir cualquier programa del usuario (ROOST.md §10,
; riesgo 4). Los datos (C:\roost, con los proyectos en C:\roost\www, y %APPDATA%\Roost) no
; los toca: ni al actualizar ni al desinstalar. Antes de sustituir o borrar ficheros, para el stack (roost down): roostd tiene
; abiertas las dll.

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

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.AddToPath=Añadir «roost» a la línea de comandos (PATH del sistema)
en.AddToPath=Add "roost" to the command line (system PATH)
es.CommandLine=Línea de comandos:
en.CommandLine=Command line:

[Tasks]
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:CommandLine}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Roost"; Filename: "{app}\roost-desktop.exe"
Name: "{autodesktop}\Roost"; Filename: "{app}\roost-desktop.exe"; Tasks: desktopicon

[Run]
; Como el usuario que instala, no elevado: si no, la app y el stack correrían como administrador.
Filename: "{app}\roost-desktop.exe"; Description: "{cm:LaunchProgram,Roost}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\roost.exe"; Parameters: "down"; Flags: runhidden waituntilterminated; RunOnceId: "RoostDown"

[Code]
const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

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
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the staged installer payload.
#endif

[Setup]
AppId={{3E9CD6A4-C084-4A41-9C10-4AF015B3A2EE}
AppName=File Organizer
AppVersion={#AppVersion}
AppPublisher=federyuk91
AppPublisherURL=https://github.com/federyuk91/Organizer
AppSupportURL=https://github.com/federyuk91/Organizer/issues
DefaultDirName={localappdata}\Programs\FileOrganizer
DefaultGroupName=File Organizer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.10240
OutputBaseFilename=FileOrganizer-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#PayloadDir}\FileOrganizer.ico
UninstallDisplayIcon={app}\FileOrganizer.ico
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
SetupLogging=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\File Organizer"; Filename: "{app}\FileOrganizer.Configurator.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FileOrganizer.ico"
Name: "{group}\Avvia organizzazione"; Filename: "{app}\engine\FileOrganizer.exe"; Parameters: "run"; WorkingDir: "{app}"; IconFilename: "{app}\FileOrganizer.ico"
Name: "{group}\Scansione adesso"; Filename: "{app}\engine\FileOrganizer.exe"; Parameters: "scan"; WorkingDir: "{app}"; IconFilename: "{app}\FileOrganizer.ico"
Name: "{group}\Disinstalla File Organizer"; Filename: "{uninstallexe}"; IconFilename: "{app}\FileOrganizer.ico"
Name: "{autodesktop}\File Organizer"; Filename: "{app}\FileOrganizer.Configurator.exe"; Tasks: desktopicon; IconFilename: "{app}\FileOrganizer.ico"

[Run]
Filename: "{app}\FileOrganizer.Configurator.exe"; Description: "{cm:LaunchProgram,File Organizer}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
// Remove only this installation's autorun commands; preserve all configurations and data.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  Command, Prefix, RunKey: String;
  Index: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  Prefix := '"' + ExpandConstant('{app}\engine\FileOrganizer.exe') + '" run --config "';
  if RegGetValueNames(HKCU, RunKey, Names) then
    for Index := 0 to GetArrayLength(Names) - 1 do
      if (Pos('FileOrganizer-', Names[Index]) = 1) and
         RegQueryStringValue(HKCU, RunKey, Names[Index], Command) then
        if (CompareText(Copy(Command, 1, Length(Prefix)), Prefix) = 0) and
           (Copy(Command, Length(Command), 1) = '"') then
          if not RegDeleteValue(HKCU, RunKey, Names[Index]) then
            Log('Could not remove autorun value: ' + Names[Index]);
end;

; CleanSuite installer (Inno Setup 6.4 or later).
; Build it with ..\build.ps1, which publishes the program first and passes the version and the publish folder:
;   ISCC.exe /DAppVersion=1.1.0 /DPublishDir=..\artifacts\publish installer\CleanSuite.iss
; The program is installed for the current user without administrator rights (Program Files only when the user chooses
; "for all users" in the first page). Settings, logs and backups in %LocalAppData%\CleanSuite are kept on uninstall unless
; the user asks to remove them; the scheduled task "CleanSuite Maintenance" is always removed.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#define AppName "CleanSuite"
#define AppPublisher "CleanSuite"
#define AppExe "CleanSuite.App.exe"
#define TaskName "CleanSuite Maintenance"

[Setup]
; Never change AppId: it identifies the installation for upgrades and uninstall.
AppId={{5AE85B48-6272-42A9-AE04-AEADB6D93A55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 1809 (build 17763), the oldest the program supports
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\CleanSuite.App\Assets\CleanSuite.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
CloseApplications=yes
RestartApplications=no
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
LicenseFile=..\LICENSE
#endif

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
arabic.RemoveUserData=هل تريد حذف إعدادات CleanSuite وسجلاته ونسخه الاحتياطية أيضًا؟%n%n%1%n%nالنسخ الاحتياطية هي ما يتيح استعادة الملفات وبيانات السجل التي نظّفها البرنامج. اختر «لا» للإبقاء عليها.
english.RemoveUserData=Also delete CleanSuite's settings, logs and backups?%n%n%1%n%nThe backups are what lets you restore files and registry data the program cleaned. Choose No to keep them.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Self-contained single-file builds: no .NET installation is needed on the target computer.
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\CleanSuite.Cli.exe"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The Scheduler tab registers this task for the user who saved the schedule; without the program it could only fail.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteScheduledTask"

[UninstallDelete]
; Native parts of the single-file program, unpacked by .NET on first start (DOTNET_BUNDLE_EXTRACT_BASE_DIR default).
Type: filesandordirs; Name: "{%TEMP}\.net\CleanSuite.App"
Type: filesandordirs; Name: "{%TEMP}\.net\CleanSuite.Cli"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    Data := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(Data) then
      if MsgBox(FmtMessage(CustomMessage('RemoveUserData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;

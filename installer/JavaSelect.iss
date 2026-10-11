; Requires Inno Setup 7.0.2+ with its official language files.
; Language sources: https://jrsoftware.org/files/is7-whatsnew.htm
; ISCC /DAppVersion=0.1.0 /DAppSource=<publish-directory> /DOutputDir=<output-directory> JavaSelect.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppSource
  #define AppSource AddBackslash(SourcePath) + "..\artifacts\JavaSelect"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\release"
#endif

#if !FileExists(AddBackslash(AppSource) + "JavaSelect.exe")
  #error AppSource must contain the published JavaSelect.exe.
#endif
#if !FileExists(AddBackslash(AppSource) + "JavaSelect.Repair.exe")
  #error AppSource must include the published JavaSelect.Repair.exe.
#endif
#if !FileExists(AddBackslash(AppSource) + "LICENSE")
  #error AppSource must include LICENSE.
#endif
#if !FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl") || !FileExists(AddBackslash(CompilerPath) + "Languages\ChineseTraditional.isl")
  #error Install Inno Setup 7.0.2 or newer with its official Chinese language files.
#endif

[Setup]
AppId={{6402C049-C5E4-4617-BD78-83741CB00650}
AppName=JavaSelect
AppVersion={#AppVersion}
AppPublisher=霄染
AppPublisherURL=https://github.com/xiaoranawa/javaselect
AppSupportURL=https://github.com/xiaoranawa/javaselect/issues
AppUpdatesURL=https://github.com/xiaoranawa/javaselect/releases
AppCopyright=Copyright (c) 2026 霄染
DefaultDirName={localappdata}\Programs\JavaSelect
DefaultGroupName=JavaSelect
AllowNoIcons=yes
PrivilegesRequired=lowest
SetupArchitecture=x86
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UsePreviousAppDir=yes
UsePreviousLanguage=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
DisableProgramGroupPage=yes
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\Assets\icon.ico
UninstallDisplayName=JavaSelect
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=JavaSelect-{#AppVersion}-win-x64-setup
VersionInfoVersion={#GetVersionNumbersString(AddBackslash(AppSource) + "JavaSelect.exe")}
VersionInfoProductTextVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Excludes: "\smoke-config,\smoke-config\*,*.pdb,*.log,*.tmp"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JavaSelect"; Filename: "{app}\JavaSelect.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\icon.ico"
Name: "{autodesktop}\JavaSelect"; Filename: "{app}\JavaSelect.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\JavaSelect.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,JavaSelect}"; Flags: nowait postinstall skipifsilent unchecked

[CustomMessages]
english.CloseRunningApp=JavaSelect is running. Choose Exit from its notification-area menu, then retry.
chinesesimplified.CloseRunningApp=JavaSelect 正在运行。请在通知区域的 JavaSelect 菜单中选择“退出”，然后重试。
chinesetraditional.CloseRunningApp=JavaSelect 正在執行。請在通知區域的 JavaSelect 選單中選擇「結束」，然後重試。
japanese.CloseRunningApp=JavaSelect は実行中です。通知領域の JavaSelect メニューから「終了」を選び、再試行してください。
english.CloseForInstall=JavaSelect is running. Close it safely and continue installing?
chinesesimplified.CloseForInstall=JavaSelect 正在运行。是否正常关闭 JavaSelect 后继续安装？
chinesetraditional.CloseForInstall=JavaSelect 正在執行。是否正常關閉 JavaSelect 後繼續安裝？
japanese.CloseForInstall=JavaSelect は実行中です。正常に終了してインストールを続行しますか？
english.CloseForUninstall=JavaSelect is running. Close it safely and continue uninstalling?
chinesesimplified.CloseForUninstall=JavaSelect 正在运行。是否正常关闭 JavaSelect 后继续卸载？
chinesetraditional.CloseForUninstall=JavaSelect 正在執行。是否正常關閉 JavaSelect 後繼續解除安裝？
japanese.CloseForUninstall=JavaSelect は実行中です。正常に終了してアンインストールを続行しますか？
english.CloseRunningAppSilent=JavaSelect is running. Silent installation or uninstallation cannot close it without confirmation. Exit JavaSelect and retry.
chinesesimplified.CloseRunningAppSilent=JavaSelect 正在运行。静默安装或卸载不能在未经确认时关闭程序。请先退出 JavaSelect 后重试。
chinesetraditional.CloseRunningAppSilent=JavaSelect 正在執行。靜默安裝或解除安裝不能在未經確認時關閉程式。請先結束 JavaSelect 後重試。
japanese.CloseRunningAppSilent=JavaSelect は実行中です。確認なしで終了できないため、サイレント操作を続行できません。JavaSelect を終了して再試行してください。
english.CloseRunningAppFailed=JavaSelect could not be closed within 20 seconds. Choose Exit from its notification-area menu, then retry.
chinesesimplified.CloseRunningAppFailed=未能在 20 秒内正常关闭 JavaSelect。请在通知区域的 JavaSelect 菜单中选择“退出”，然后重试。
chinesetraditional.CloseRunningAppFailed=未能在 20 秒內正常關閉 JavaSelect。請在通知區域的 JavaSelect 選單中選擇「結束」，然後重試。
japanese.CloseRunningAppFailed=20 秒以内に JavaSelect を正常に終了できませんでした。通知領域の JavaSelect メニューから「終了」を選び、再試行してください。

[Code]
type
  { The installer is x86; these records match the 32-bit Win32 ABI. }
  TQuitStartupInfo = record
    cb, lpReserved, lpDesktop, lpTitle: LongWord;
    dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars: LongWord;
    dwFillAttribute, dwFlags: LongWord;
    wShowWindow, cbReserved2: Word;
    lpReserved2, hStdInput, hStdOutput, hStdError: LongWord;
  end;
  TQuitProcessInfo = record
    hProcess, hThread, dwProcessId, dwThreadId: LongWord;
  end;

function CreateQuitProcess(ApplicationName, CommandLine: String;
  ProcessAttributes, ThreadAttributes: LongWord; InheritHandles: Integer;
  CreationFlags, Environment: LongWord; CurrentDirectory: String;
  var StartupInfo: TQuitStartupInfo; var ProcessInfo: TQuitProcessInfo): Integer;
  external 'CreateProcessW@kernel32.dll stdcall';
function WaitForQuitProcess(Handle, Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function GetQuitProcessExitCode(Handle: LongWord; var ExitCode: LongWord): Integer;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CloseQuitHandle(Handle: LongWord): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

function GetJavaSelectMutex(Param: String): String;
begin
  { Must match App.xaml.cs, including UTF-8 for non-ASCII account names. }
  Result := 'Local\JavaSelect.' + Uppercase(Copy(
    GetSHA256OfString(Utf8Encode(GetUserNameString + 'JavaSelect')), 1, 20));
end;

function RequestNormalExit(const Executable: String): Boolean;
var
  StartupInfo: TQuitStartupInfo;
  ProcessInfo: TQuitProcessInfo;
  ExitCode: LongWord;
begin
  Result := False;
  StartupInfo.cb := SizeOf(StartupInfo);
  StartupInfo.dwFlags := 1;
  StartupInfo.wShowWindow := SW_HIDE;
  if CreateQuitProcess(Executable, '"' + Executable + '" --quit-for-update',
    0, 0, 0, $08000000, 0, ExtractFileDir(Executable), StartupInfo, ProcessInfo) = 0 then Exit;
  try
    if WaitForQuitProcess(ProcessInfo.hProcess, 20000) <> 0 then Exit;
    if GetQuitProcessExitCode(ProcessInfo.hProcess, ExitCode) = 0 then Exit;
    Result := ExitCode = 0;
  finally
    CloseQuitHandle(ProcessInfo.hThread);
    CloseQuitHandle(ProcessInfo.hProcess);
  end;
end;

function EnsureJavaSelectStopped(Uninstalling: Boolean; var Declined: Boolean): String;
var
  Executable, Prompt: String;
  Silent: Boolean;
begin
  Result := '';
  Declined := False;
  if not CheckForMutexes(GetJavaSelectMutex('')) then Exit;
  if Uninstalling then Silent := UninstallSilent else Silent := WizardSilent;
  if Silent then begin
    Result := CustomMessage('CloseRunningAppSilent');
    Log(Result);
    Exit;
  end;
  Executable := ExpandConstant('{app}\JavaSelect.exe');
  if not FileExists(Executable) then begin
    Result := CustomMessage('CloseRunningApp');
    Exit;
  end;
  if Uninstalling then Prompt := CustomMessage('CloseForUninstall')
  else Prompt := CustomMessage('CloseForInstall');
  if MsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then begin
    Declined := True;
    Result := CustomMessage('CloseRunningApp');
    Exit;
  end;
  if not RequestNormalExit(Executable) then begin
    Result := CustomMessage('CloseRunningAppFailed');
    Exit;
  end;
  if CheckForMutexes(GetJavaSelectMutex('')) then
    Result := CustomMessage('CloseRunningAppFailed');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Declined: Boolean;
begin
  Result := EnsureJavaSelectStopped(False, Declined);
end;

function InitializeUninstall: Boolean;
var
  Failure: String;
  Declined: Boolean;
begin
  Failure := EnsureJavaSelectStopped(True, Declined);
  Result := Failure = '';
  if not Result then begin
    Log(Failure);
    if not UninstallSilent and not Declined then
      MsgBox(Failure, mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SavedCommand, InstalledCommand: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  InstalledCommand := '"' + ExpandConstant('{app}\JavaSelect.exe') + '" --background';
  { Preserve a startup entry owned by another portable or installed copy. }
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'JavaSelect', SavedCommand) then
    if CompareText(SavedCommand, InstalledCommand) = 0 then
      if not RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'JavaSelect') then
        Log('Could not remove the startup entry for this installation.');
end;

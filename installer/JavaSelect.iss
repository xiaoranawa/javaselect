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
; WinUI language folders the package ships. Keep in sync with build/WinUILanguages.targets (package.ps1 verifies it).
#ifndef KeptLanguages
  #define KeptLanguages "en-us,zh-CN,zh-TW,ja-JP"
#endif
#define DotNetMajor "10"
; The page the .NET apphost's own "install .NET" dialog sends people to.
#define DotNetDownloadUrl "https://aka.ms/dotnet-core-applaunch?framework=Microsoft.NETCore.App&framework_version=" + DotNetMajor + ".0.0&arch=x64&rid=win-x64&os=win10"

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
; A payload without its own .NET host files is the framework-dependent flavor: it needs the machine's .NET runtime
; (Setup checks for it), and an upgrade has to clear the old bundled runtime out of the install folder.
#if FileExists(AddBackslash(AppSource) + "hostfxr.dll")
  #define FrameworkDependentPackage 0
#else
  #define FrameworkDependentPackage 1
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
; Unchecked on every run (see CurPageChanged): an upgrade must not switch back on what was turned off in JavaSelect.
Name: "startup"; Description: "{cm:StartAtSignIn}"; GroupDescription: "{cm:StartupGroup}"; Flags: unchecked

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
english.RemoveStartupFailed=JavaSelect startup could not be removed. Allow administrator access or disable startup in JavaSelect, then retry.
chinesesimplified.RemoveStartupFailed=未能清理 JavaSelect 开机启动。请允许管理员授权，或先在 JavaSelect 中关闭开机启动后重试。
chinesetraditional.RemoveStartupFailed=無法清理 JavaSelect 開機啟動。請允許系統管理員授權，或先在 JavaSelect 中關閉開機啟動後重試。
japanese.RemoveStartupFailed=JavaSelect の自動起動を削除できませんでした。管理者権限を許可するか、JavaSelect で自動起動を無効にしてから再試行してください。
english.NeedDotNetHint=JavaSelect needs the .NET {#DotNetMajor} Runtime (x64) for this step; install it first if it is missing.
chinesesimplified.NeedDotNetHint=此步骤需要 .NET {#DotNetMajor} 运行时（x64）；如果缺少，请先安装。
chinesetraditional.NeedDotNetHint=此步驟需要 .NET {#DotNetMajor} 執行階段（x64）；若缺少，請先安裝。
japanese.NeedDotNetHint=この処理には .NET {#DotNetMajor} ランタイム（x64）が必要です。未インストールの場合は先にインストールしてください。
english.StartupGroup=Startup:
chinesesimplified.StartupGroup=启动：
chinesetraditional.StartupGroup=啟動：
japanese.StartupGroup=起動：
english.StartAtSignIn=Start JavaSelect when I sign in
chinesesimplified.StartAtSignIn=开机自动启动 JavaSelect
chinesetraditional.StartAtSignIn=開機自動啟動 JavaSelect
japanese.StartAtSignIn=サインイン時に JavaSelect を自動的に起動する
english.EnableStartupFailed=Setup could not turn on start at sign-in. You can enable it later in JavaSelect > General.
chinesesimplified.EnableStartupFailed=安装程序未能开启开机自动启动。你可以稍后在 JavaSelect 的“常规”设置中开启。
chinesetraditional.EnableStartupFailed=安裝程式無法開啟開機自動啟動。你可以稍後在 JavaSelect 的「一般」設定中開啟。
japanese.EnableStartupFailed=サインイン時の自動起動を有効にできませんでした。後で JavaSelect の「一般」設定から有効にできます。
english.DotNetMissing=JavaSelect needs the .NET {#DotNetMajor} Runtime (x64), which was not found on this computer.%n%nYes: open the Microsoft download page and exit Setup. Install the runtime, then run Setup again.%nNo: continue installing anyway. JavaSelect will not start until .NET {#DotNetMajor} is installed.%nCancel: exit Setup.
chinesesimplified.DotNetMissing=JavaSelect 需要 .NET {#DotNetMajor} 运行时（x64），但在此电脑上未找到。%n%n选择“是”：打开微软下载页面并退出安装程序。安装运行时后请重新运行安装程序。%n选择“否”：仍然继续安装，但在安装 .NET {#DotNetMajor} 之前 JavaSelect 无法启动。%n选择“取消”：退出安装程序。
chinesetraditional.DotNetMissing=JavaSelect 需要 .NET {#DotNetMajor} 執行階段（x64），但在此電腦上找不到。%n%n選擇「是」：開啟 Microsoft 下載頁面並結束安裝程式。安裝執行階段後請再次執行安裝程式。%n選擇「否」：仍然繼續安裝，但在安裝 .NET {#DotNetMajor} 之前 JavaSelect 無法啟動。%n選擇「取消」：結束安裝程式。
japanese.DotNetMissing=JavaSelect の実行には .NET {#DotNetMajor} ランタイム（x64）が必要ですが、このコンピューターで見つかりませんでした。%n%n［はい］：Microsoft のダウンロードページを開いて、セットアップを終了します。ランタイムをインストールしてから、もう一度セットアップを実行してください。%n［いいえ］：このままインストールを続行します。.NET {#DotNetMajor} をインストールするまで JavaSelect は起動できません。%n［キャンセル］：セットアップを終了します。
english.RemoveOldRuntimeFailed=Setup could not remove the old bundled .NET runtime file %1 from the installation folder. Exit JavaSelect completely (notification-area menu > Exit), close any program using that folder, then retry.
chinesesimplified.RemoveOldRuntimeFailed=安装程序无法从安装目录中删除旧的内置 .NET 运行时文件 %1。请完全退出 JavaSelect（通知区域菜单 > 退出），关闭占用该目录的程序后重试。
chinesetraditional.RemoveOldRuntimeFailed=安裝程式無法從安裝目錄刪除舊的內建 .NET 執行階段檔案 %1。請完全結束 JavaSelect（通知區域選單 > 結束），並關閉佔用該目錄的程式後再試。
japanese.RemoveOldRuntimeFailed=インストール先から古い同梱の .NET ランタイムのファイル %1 を削除できませんでした。JavaSelect を完全に終了し（通知領域のメニュー > 終了）、そのフォルダーを使用しているプログラムを閉じてから再試行してください。

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

function RequestMaintenance(const Executable, Arguments: String): Boolean;
var
  StartupInfo: TQuitStartupInfo;
  ProcessInfo: TQuitProcessInfo;
  ExitCode: LongWord;
begin
  Result := False;
  StartupInfo.cb := SizeOf(StartupInfo);
  StartupInfo.dwFlags := 1;
  StartupInfo.wShowWindow := SW_HIDE;
  if CreateQuitProcess(Executable, '"' + Executable + '" ' + Arguments,
    0, 0, 0, $08000000, 0, ExtractFileDir(Executable), StartupInfo, ProcessInfo) = 0 then Exit;
  try
    if Arguments = '--quit-for-update' then begin
      if WaitForQuitProcess(ProcessInfo.hProcess, 20000) <> 0 then Exit;
    end else if WaitForQuitProcess(ProcessInfo.hProcess, 120000) <> 0 then Exit;
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
  if not RequestMaintenance(Executable, '--quit-for-update') then begin
    Result := CustomMessage('CloseRunningAppFailed');
    Exit;
  end;
  if CheckForMutexes(GetJavaSelectMutex('')) then
    Result := CustomMessage('CloseRunningAppFailed');
end;

{ ---- The flavor of this package is fixed when the script is compiled. ---- }

function IsFrameworkDependentPackage: Boolean;
begin
  Result := {#FrameworkDependentPackage} = 1;
end;

{ ---- The system .NET runtime (framework-dependent package only) ---- }

// DotNetRoot is a folder that holds "shared\Microsoft.NETCore.App". Only release builds count: the app asks for
// {#DotNetMajor}.0.0, which a preview runtime does not satisfy.
function HasDotNetRuntimeIn(const DotNetRoot: String): Boolean;
var
  FindRec: TFindRec;
  Directory: String;
begin
  Result := False;
  if DotNetRoot = '' then Exit;
  Directory := AddBackslash(DotNetRoot) + 'shared\Microsoft.NETCore.App\';
  if FindFirst(Directory + '{#DotNetMajor}.*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Pos('-', FindRec.Name) = 0) and
         FileExists(Directory + FindRec.Name + '\Microsoft.NETCore.App.deps.json') then begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Mirrors where the .NET apphost looks for the x64 runtime. Setup is a 32-bit process, hence HKLM64.
function IsDotNetRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  Location, ProgramFiles: String;
  I: Integer;
begin
  Result := True;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Copy(Names[I], 1, Length('{#DotNetMajor}.')) = '{#DotNetMajor}.') and (Pos('-', Names[I]) = 0) then Exit;
  if HasDotNetRuntimeIn(GetEnv('DOTNET_ROOT_X64')) or HasDotNetRuntimeIn(GetEnv('DOTNET_ROOT')) then Exit;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    if HasDotNetRuntimeIn(Location) then Exit;
  ProgramFiles := GetEnv('ProgramW6432');
  if ProgramFiles <> '' then
    if HasDotNetRuntimeIn(ProgramFiles + '\dotnet') then Exit;
  Result := False;
end;

{ ---- Upgrading over an old self-contained install ----
  Earlier versions bundled the whole .NET runtime (about 190 files) and 86 WinUI language folders next to the app.
  Setup only overwrites files, so without this cleanup they would stay behind. Worse, a leftover hostfxr.dll makes the
  apphost treat the install folder as a self-contained app, so the new framework-dependent JavaSelect would refuse to
  start even though .NET is installed. Everything below is guarded by IsOldSelfContainedInstall and touches nothing
  otherwise, because the install folder may be shared with other files. }

// JavaSelect.exe, a bundled host file and a runtimeconfig that lists "includedFrameworks" (the self-contained
// marker). The runtimeconfig is replaced only after the cleanup, so a retried upgrade is still recognized.
function IsOldSelfContainedInstall: Boolean;
var
  Config: AnsiString;
  Content: String;
begin
  Result := False;
  if not FileExists(ExpandConstant('{app}\JavaSelect.exe')) then Exit;
  if not (FileExists(ExpandConstant('{app}\hostfxr.dll')) or FileExists(ExpandConstant('{app}\hostpolicy.dll')) or
          FileExists(ExpandConstant('{app}\coreclr.dll'))) then Exit;
  if not LoadStringFromFile(ExpandConstant('{app}\JavaSelect.runtimeconfig.json'), Config) then Exit;
  Content := Config;
  Result := Pos('"includedFrameworks"', Content) > 0;
end;

// A file that is briefly locked (antivirus scanning it, a process that is still shutting down) is retried every
// 250 ms before it counts as undeletable.
function DeleteObsoleteFile(const FileName: String; const Attempts: Integer): Boolean;
var
  Attempt: Integer;
begin
  Result := not FileExists(FileName);
  if Result then Exit;
  for Attempt := 1 to Attempts do begin
    if DeleteFile(FileName) then begin
      Log('Removed obsolete file: ' + FileName);
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Log('Could not remove obsolete file: ' + FileName);
end;

// Deletes every file in {app} that matches a wildcard. Names are collected first so nothing is deleted while
// enumerating. Returns False if a matching file could not be removed.
function DeleteMatchingFiles(const Pattern: String): Boolean;
var
  FindRec: TFindRec;
  Names: TArrayOfString;
  Directory: String;
  Count, I: Integer;
begin
  Result := True;
  Directory := ExpandConstant('{app}\');
  Count := 0;
  if FindFirst(Directory + Pattern, FindRec) then
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then begin
        SetArrayLength(Names, Count + 1);
        Names[Count] := FindRec.Name;
        Count := Count + 1;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  for I := 0 to Count - 1 do
    if not DeleteObsoleteFile(Directory + Names[I], 4) then Result := False;
end;

// de-DE, ja-JP, sr-Cyrl-RS, ca-Es-VALENCIA: a 2-3 letter language followed by one or two alphanumeric subtags.
function IsLocaleFolderName(const Name: String): Boolean;
var
  Rest, Part: String;
  P, I, Parts: Integer;
begin
  Result := False;
  Rest := Name;
  Parts := 0;
  while Rest <> '' do begin
    P := Pos('-', Rest);
    if P = 0 then begin
      Part := Rest;
      Rest := '';
    end else begin
      Part := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    if (Part = '') or (Length(Part) > 8) then Exit;
    for I := 1 to Length(Part) do
      if not (((Part[I] >= 'a') and (Part[I] <= 'z')) or ((Part[I] >= 'A') and (Part[I] <= 'Z')) or
              ((Part[I] >= '0') and (Part[I] <= '9') and (Parts > 0))) then Exit;
    if (Parts = 0) and (Length(Part) > 3) then Exit;
    Parts := Parts + 1;
  end;
  Result := Parts >= 2;
end;

// True only for a folder that holds nothing but the WinUI resource files this app used to ship.
function IsObsoleteLanguageFolder(const Directory: String): Boolean;
var
  FindRec: TFindRec;
  Count: Integer;
begin
  Result := False;
  Count := 0;
  if FindFirst(Directory + '\*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) or
           ((CompareText(FindRec.Name, 'Microsoft.ui.xaml.dll.mui') <> 0) and
            (CompareText(FindRec.Name, 'Microsoft.UI.Xaml.Phone.dll.mui') <> 0)) then Exit;
        Count := Count + 1;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  Result := Count > 0;
end;

procedure RemoveObsoleteLanguageFolders;
var
  FindRec: TFindRec;
  Names: TArrayOfString;
  Directory, Kept: String;
  Count, I: Integer;
begin
  Directory := ExpandConstant('{app}\');
  Kept := LowerCase(',{#KeptLanguages},');
  Count := 0;
  if FindFirst(Directory + '*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (FindRec.Attributes and $400 = 0) and
         IsLocaleFolderName(FindRec.Name) and (Pos(',' + LowerCase(FindRec.Name) + ',', Kept) = 0) and
         IsObsoleteLanguageFolder(Directory + FindRec.Name) then begin
        SetArrayLength(Names, Count + 1);
        Names[Count] := FindRec.Name;
        Count := Count + 1;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  for I := 0 to Count - 1 do
    if DelTree(Directory + Names[I], True, True, True) then Log('Removed obsolete language folder: ' + Names[I])
    else Log('Could not remove obsolete language folder: ' + Names[I]);
end;

// Returns '' when done, otherwise the message that stops Setup before it replaces any file.
function RemoveOldSelfContainedRuntime: String;
var
  Directory, Failed, Patterns: String;
  P: Integer;
begin
  Result := '';
  if not IsOldSelfContainedInstall then Exit;
  Directory := ExpandConstant('{app}\');
  Log('A self-contained JavaSelect installation was found in ' + ExpandConstant('{app}') + '; removing its bundled .NET runtime.');
  // The host files go first, and coreclr.dll first of all: a running copy of the app locks it, so a locked
  // runtime stops Setup while the old installation is still complete. hostfxr.dll is the file that actually breaks
  // the new app; a stale one must never be left behind silently, hence the long retry (10 s) before giving up.
  Failed := '';
  if not DeleteObsoleteFile(Directory + 'coreclr.dll', 40) then Failed := 'coreclr.dll'
  else if not DeleteObsoleteFile(Directory + 'hostpolicy.dll', 40) then Failed := 'hostpolicy.dll'
  else if not DeleteObsoleteFile(Directory + 'hostfxr.dll', 40) then Failed := 'hostfxr.dll';
  if Failed <> '' then begin
    Result := FmtMessage(CustomMessage('RemoveOldRuntimeFailed'), [Failed]);
    Exit;
  end;
  // The rest of the bundled runtime (the files the self-contained layout has and the framework-dependent one does
  // not). They are harmless but bulky, so a locked one is logged and skipped. The new files are copied afterwards,
  // which restores the few System.* assemblies the app really ships (System.Drawing.Common and friends).
  Patterns := 'clrjit.dll|clrgc.dll|clrgcexp.dll|clretwrc.dll|createdump.exe|mscordaccore.dll|mscordaccore_*.dll|' +
    'mscordbi.dll|mscorrc.dll|mscorlib.dll|netstandard.dll|WindowsBase.dll|msquic.dll|api-ms-win-*.dll|' +
    'System.dll|System.*.dll|Microsoft.CSharp.dll|Microsoft.VisualBasic.dll|Microsoft.VisualBasic.*.dll|' +
    'Microsoft.Win32.*.dll|Microsoft.DiaSymReader.Native.*.dll|';
  while Patterns <> '' do begin
    P := Pos('|', Patterns);
    DeleteMatchingFiles(Copy(Patterns, 1, P - 1));
    Delete(Patterns, 1, P);
  end;
  RemoveObsoleteLanguageFolders;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Declined: Boolean;
begin
  Result := EnsureJavaSelectStopped(False, Declined);
  // Only once the app is stopped, and before Setup copies a single file.
  if (Result = '') and IsFrameworkDependentPackage then Result := RemoveOldSelfContainedRuntime;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsFrameworkDependentPackage then Exit;
  if IsDotNetRuntimeInstalled then Exit;
  Log('The .NET {#DotNetMajor} runtime (x64) was not found.');
  // Setup cannot ask when it runs silently; the .NET host shows its own "install .NET" dialog when JavaSelect starts.
  if WizardSilent then Exit;
  case MsgBox(CustomMessage('DotNetMissing'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1) of
    IDYES:
      begin
        ShellExec('open', '{#DotNetDownloadUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
        Result := False;
      end;
    IDCANCEL:
      Result := False;
  end;
end;

var
  StartupTaskReset: Boolean;

function StartupNamedOnCommandLine: Boolean;
begin
  Result := (Pos('startup', LowerCase(ExpandConstant('{param:TASKS|}'))) > 0) or
            (Pos('startup', LowerCase(ExpandConstant('{param:MERGETASKS|}'))) > 0);
end;

// UsePreviousTasks restores the previous run's choices when the Select Tasks page is first prepared, which would
// switch "start at sign-in" back on after it was turned off in JavaSelect. So every run starts with it unchecked,
// unless /TASKS or /MERGETASKS names it. Done once, so going Back and forth in the wizard keeps the user's choice.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not StartupTaskReset then begin
    StartupTaskReset := True;
    if not StartupNamedOnCommandLine then WizardSelectTasks('!startup');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('startup') then begin
    // JavaSelect registers its own elevated sign-in task, which is why Windows shows no UAC prompt at sign-in, and
    // records the setting. The bootstrap already runs Setup elevated as the same account, so this normally needs no prompt.
    if not RequestMaintenance(ExpandConstant('{app}\JavaSelect.exe'), '--enable-startup') then begin
      Log('JavaSelect could not turn on start at sign-in.');
      if not WizardSilent then MsgBox(CustomMessage('EnableStartupFailed'), mbInformation, MB_OK);
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  Failure, Executable, Arguments: String;
  Declined: Boolean;
begin
  Failure := EnsureJavaSelectStopped(True, Declined);
  Result := Failure = '';
  if Result then begin
    Executable := ExpandConstant('{app}\JavaSelect.exe');
    Arguments := '--remove-startup';
    if UninstallSilent then Arguments := Arguments + ' --no-elevation';
    if FileExists(Executable) and not RequestMaintenance(Executable, Arguments) then begin
      Failure := CustomMessage('RemoveStartupFailed');
      // Removing the sign-in task runs JavaSelect.exe, which a framework-dependent install cannot do without .NET.
      if IsFrameworkDependentPackage then Failure := Failure + #13#10 + CustomMessage('NeedDotNetHint');
      Result := False;
    end;
  end;
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

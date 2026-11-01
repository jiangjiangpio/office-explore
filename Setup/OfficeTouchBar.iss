#define MyAppName "OfficeTouchBar"
#define MyAppVersion "0.3.19"
#define MyAppPublisher "OfficeTouchBar"
#define MyAppExeName "OfficeTouchBar.exe"

[Setup]
AppId={{E90F4A85-19D9-4DBD-8E4B-5A4A0A9F8B19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\OfficeTouchBar
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts
OutputBaseFilename=OfficeTouchBar-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=no
CloseApplications=yes
CloseApplicationsFilter=OfficeTouchBar.exe,OfficeTouchBar.dll
RestartApplications=no
SetupMutex=OfficeTouchBar.Setup
VersionInfoVersion=0.3.18.0

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish-files.txt"; DestName: "current-payload.txt"; Flags: dontcopy
Source: "LegacyPayloadFiles.txt"; Flags: dontcopy

[Icons]
Name: "{group}\OfficeTouchBar"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\OfficeTouchBar"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 OfficeTouchBar"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "OfficeTouchBar"; Flags: uninsdeletevalue

[UninstallDelete]
Type: files; Name: "{app}\installed-files.txt"

[Code]
var
  OldPayload, CurrentPayload, LegacyPayload: TArrayOfString;

function PayloadContains(const Items: TArrayOfString; const Name: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Items) - 1 do
    if CompareText(Trim(Items[I]), Name) = 0 then begin Result := True; Exit; end;
end;

function GetAttributes(const Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function SafePayloadPath(const RelativeName: String): String;
var Root, Parent, Extension: String; Attributes: Cardinal;
begin
  Result := '';
  if (RelativeName = '') or (Pos('..', RelativeName) > 0) or
     (Pos(':', RelativeName) > 0) or (Pos('/', RelativeName) > 0) or
     (Copy(RelativeName, 1, 1) = '\') then Exit;
  Extension := Lowercase(ExtractFileExt(RelativeName));
  if (Extension <> '.dll') and (Extension <> '.exe') and (Extension <> '.json') and
     (Extension <> '.pdb') and (Extension <> '.dat') then Exit;
  if (CompareText(ExtractFileName(RelativeName), 'settings.json') = 0) or
     (Pos('unins', Lowercase(ExtractFileName(RelativeName))) = 1) then Exit;
  Root := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  Result := ExpandFileName(Root + RelativeName);
  if CompareText(Copy(Result, 1, Length(Root)), Root) <> 0 then begin Result := ''; Exit; end;
  Parent := Result;
  while Length(Parent) >= Length(RemoveBackslashUnlessRoot(Root)) do begin
    Attributes := GetAttributes(Parent);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin Result := ''; Exit; end;
    if CompareText(Parent, RemoveBackslashUnlessRoot(Root)) = 0 then Break;
    Parent := ExtractFileDir(Parent);
  end;
end;

procedure ValidateOldPayload(const Items: TArrayOfString);
var I: Integer; Name: String;
begin
  for I := 0 to GetArrayLength(Items) - 1 do begin
    Name := Trim(Items[I]);
    if (Name <> '') and not PayloadContains(CurrentPayload, Name) and (SafePayloadPath(Name) = '') then
      RaiseException('旧文件清单包含不安全或无法识别的路径，已停止升级：' + Name);
  end;
end;

procedure RemoveObsoletePayload(const Items: TArrayOfString);
var I: Integer; Name, Path, Parent, Root: String;
begin
  Root := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  for I := 0 to GetArrayLength(Items) - 1 do begin
    Name := Trim(Items[I]);
    if (Name <> '') and not PayloadContains(CurrentPayload, Name) then begin
      Path := SafePayloadPath(Name);
      if Path = '' then RaiseException('无法安全清理旧文件：' + Name);
      if FileExists(Path) then begin
        if not DeleteFile(Path) then
          RaiseException('旧文件仍被占用，升级清理未完成。请关闭 OfficeTouchBar 后重新运行安装：' + Path);
        Log('Removed obsolete payload: ' + Path);
        Parent := ExtractFileDir(Path);
        while (CompareText(Parent, Root) <> 0) and (Length(Parent) > Length(Root)) do begin
          if not RemoveDir(Parent) then Break;
          Parent := ExtractFileDir(Parent);
        end;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    if CheckForMutexes('OfficeTouchBar.SingleInstance') then
      RaiseException('OfficeTouchBar 尚未完全退出，请退出本应用后重试。无需关闭 PowerPoint。');
    ExtractTemporaryFile('current-payload.txt');
    ExtractTemporaryFile('LegacyPayloadFiles.txt');
    if not LoadStringsFromFile(ExpandConstant('{tmp}\current-payload.txt'), CurrentPayload) then
      RaiseException('无法读取新版本文件清单。');
    if not LoadStringsFromFile(ExpandConstant('{tmp}\LegacyPayloadFiles.txt'), LegacyPayload) then
      RaiseException('无法读取旧版本兼容清单。');
    if FileExists(ExpandConstant('{app}\installed-files.txt')) then
      if not LoadStringsFromFile(ExpandConstant('{app}\installed-files.txt'), OldPayload) then
        RaiseException('无法读取已安装版本文件清单。');
    ValidateOldPayload(OldPayload);
    ValidateOldPayload(LegacyPayload);
  end;
  if CurStep = ssPostInstall then begin
    RemoveObsoletePayload(OldPayload);
    RemoveObsoletePayload(LegacyPayload);
    { Keep the old manifest until cleanup succeeds, so a failed upgrade is retryable. }
    if not SaveStringsToFile(ExpandConstant('{app}\installed-files.txt'), CurrentPayload, False) then
      RaiseException('无法保存安装文件清单，请重新运行安装。');
  end;
end;

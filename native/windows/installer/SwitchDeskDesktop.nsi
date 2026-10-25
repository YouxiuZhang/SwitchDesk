# -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef APP_DIR
  !error "APP_DIR must point to the compiled application directory"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE must point to the installer output file"
!endif

Name "双栖 SwitchDesk"
!define MUI_ICON "${APP_DIR}/SwitchDesk.ico"
!define MUI_UNICON "${APP_DIR}/SwitchDesk.ico"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\SwitchDesk"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "0.2.10.0"
VIAddVersionKey /LANG=2052 "ProductName" "双栖 SwitchDesk"
VIAddVersionKey /LANG=2052 "FileDescription" "双栖桌面联动安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "0.2.10.0"
VIAddVersionKey /LANG=2052 "LegalCopyright" "SwitchDesk"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装双栖 SwitchDesk"
!define MUI_WELCOMEPAGE_TEXT "两台电脑，一个工作空间。$\r$\n$\r$\n安装后与 Mac 配对，选择键鼠来源，共享剪贴板与桌面预览。Mac 和 Windows 各用视频线连接同一台显示器，保持局域网可达。$\r$\n$\r$\n仅安装到当前用户目录，无需管理员权限。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\SwitchDesk.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开双栖"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro CheckRunning Prefix
Function ${Prefix}CheckRunning
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\SwitchDeskGui") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先退出旧版切屏测试程序。"
    Abort
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\SwitchDeskDdcWorker") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "显示器操作仍在收尾，请等待片刻再重新运行安装或卸载程序。"
    Abort
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\SwitchDeskDesktopGui") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先退出双栖，再重新运行安装或卸载程序。"
    Abort
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\SwitchDeskTestTrial") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "切屏试验仍在收尾，请等待片刻再重新运行安装或卸载程序。"
    Abort
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CheckRunning ""
!insertmacro CheckRunning "un."

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "本程序需要 64 位 Windows 10 或 Windows 11。"
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "本程序需要 Windows 10 或 Windows 11。"
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_OK|MB_ICONSTOP "此电脑需要 .NET Framework 4.8 或更新版本。请先通过 Windows 更新或微软官方安装程序安装该组件。"
    Abort
  ${EndIf}
  Call CheckRunning
FunctionEnd

Section "双栖" Main
  Call CheckRunning
  SetShellVarContext current
  SetRegView 64
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${APP_DIR}/SwitchDesk.exe"
  File "${APP_DIR}/SwitchDesk.exe.config"
  File "${APP_DIR}/README.zh-CN.md"
  File "${APP_DIR}/SwitchDesk.ico"
  File "${APP_DIR}/SwitchDesk.png"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\双栖 SwitchDesk"
  CreateShortcut "$SMPROGRAMS\双栖 SwitchDesk\双栖.lnk" "$INSTDIR\SwitchDesk.exe"
  CreateShortcut "$SMPROGRAMS\双栖 SwitchDesk\卸载.lnk" "$INSTDIR\Uninstall.exe"
  Call CreateDesktopShortcut
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "DisplayName" "双栖 SwitchDesk"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "DisplayVersion" "0.2.10"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "Publisher" "SwitchDesk"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "DisplayIcon" "$INSTDIR\SwitchDesk.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "NoRepair" 1
SectionEnd

Function CreateDesktopShortcut
  CreateShortcut "$DESKTOP\双栖 SwitchDesk.lnk" "$INSTDIR\SwitchDesk.exe" "" "$INSTDIR\SwitchDesk.exe" 0
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk" "InstallLocation"
  ${If} $0 != $INSTDIR
    MessageBox MB_OK|MB_ICONSTOP "卸载程序位置与安装记录不一致，请从 Windows 应用列表启动卸载。"
    Abort
  ${EndIf}
  Call un.CheckRunning
FunctionEnd

Section "Uninstall"
  Call un.CheckRunning
  SetShellVarContext current
  SetRegView 64
  Delete "$INSTDIR\SwitchDesk.exe"
  Delete "$INSTDIR\SwitchDesk.exe.config"
  Delete "$INSTDIR\README.zh-CN.md"
  Delete "$INSTDIR\SwitchDesk.ico"
  Delete "$INSTDIR\SwitchDesk.png"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\双栖 SwitchDesk.lnk"
  Delete "$SMPROGRAMS\双栖 SwitchDesk\双栖.lnk"
  Delete "$SMPROGRAMS\双栖 SwitchDesk\卸载.lnk"
  RMDir "$SMPROGRAMS\双栖 SwitchDesk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchDesk"
SectionEnd

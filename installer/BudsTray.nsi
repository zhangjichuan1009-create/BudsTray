; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef VERSION
!define VERSION "0.9.9"
!endif
!ifndef PAYLOAD
!define PAYLOAD "..\artifacts\portable"
!endif
!ifndef OUTFILE
!define OUTFILE "..\artifacts\BudsTray-Setup-${VERSION}.exe"
!endif
Name "Buds Tray"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\BudsTray"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails nevershow
ShowUninstDetails nevershow
AutoCloseWindow true
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "Buds Tray"
VIAddVersionKey /LANG=2052 "FileDescription" "Buds Tray 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "GPL-3.0-only"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Var NoAutorun

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "需要 64 位 Windows。"
    Abort
  ${EndIf}
  SetRegView 64
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/NOAUTORUN" $1
  ${IfNot} ${Errors}
    StrCpy $NoAutorun "1"
  ${EndIf}
FunctionEnd

Section "安装"
  ${If} $NoAutorun != "1"
    IfFileExists "$INSTDIR\RedmiBudsTray.exe" 0 +3
      ExecWait '"$INSTDIR\RedmiBudsTray.exe" --shutdown'
      Sleep 1500
  ${EndIf}
  SetOutPath "$INSTDIR"
  File "${PAYLOAD}\RedmiBudsTray.exe"
  File "${PAYLOAD}\LICENSE"
  File "${PAYLOAD}\README.md"
  File "${PAYLOAD}\THIRD_PARTY.md"
  File "${PAYLOAD}\TOOTHTRAY-LICENSE.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Buds Tray.lnk" "$INSTDIR\RedmiBudsTray.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "DisplayName" "Buds Tray"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "DisplayIcon" "$INSTDIR\RedmiBudsTray.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "URLInfoAbout" "https://github.com/zhangjichuan1009-create/BudsTray"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray" "NoRepair" 1
  ${If} $NoAutorun != "1"
    Exec '"$INSTDIR\RedmiBudsTray.exe"'
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/NOAUTORUN" $1
  ${IfNot} ${Errors}
    StrCpy $NoAutorun "1"
  ${EndIf}
FunctionEnd

Section "Uninstall"
  ${If} $NoAutorun != "1"
    ExecWait '"$INSTDIR\RedmiBudsTray.exe" --shutdown'
    Sleep 1500
    ExecWait '"$INSTDIR\RedmiBudsTray.exe" --uninstall-cleanup' $0
    ${If} $0 != 0
      MessageBox MB_OK|MB_ICONSTOP "无法清理本安装副本的自启动，请关闭自启动后重试。"
      Abort
    ${EndIf}
  ${EndIf}
  ; Remove only shipped files. Keep user settings, logs and unrelated files.
  Delete "$INSTDIR\RedmiBudsTray.exe"
  Delete "$INSTDIR\D3DCompiler_47_cor3.dll"
  Delete "$INSTDIR\PenImc_cor3.dll"
  Delete "$INSTDIR\PresentationNative_cor3.dll"
  Delete "$INSTDIR\vcruntime140_cor3.dll"
  Delete "$INSTDIR\wpfgfx_cor3.dll"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\THIRD_PARTY.md"
  Delete "$INSTDIR\TOOTHTRAY-LICENSE.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Buds Tray.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BudsTray"
SectionEnd

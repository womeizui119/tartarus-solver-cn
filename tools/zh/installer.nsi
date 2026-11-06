; 困兽玩法推理 Windows 安装程序（NSIS）。
; 用法：makensis -DVERSION=1.2.2 -DSRC=解压好的程序文件夹 -DOUT=输出exe installer.nsi
; 安装到 用户目录\AppData\Local\Programs\困兽玩法推理（不需要管理员权限），桌面和开始菜单建快捷方式，带卸载。

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APPNAME "困兽玩法推理"
!define EXENAME "困兽玩法推理.exe"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"

Name "${APPNAME} ${VERSION}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${APPNAME} ${VERSION}  ·  基于 glcoder/TartarusSolver（MIT）"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APPNAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "原作者 Artem Gurevich（glcoder/TartarusSolver），MIT 协议"

!define MUI_ICON "${SRC}\..\app.ico"
!define MUI_UNICON "${SRC}\..\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 ${APPNAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "《火炬之光：无限》塔罗斯监狱玩法的走法提示工具。$\r$\n$\r$\n软件只截取游戏窗口画面、读取游戏日志，不碰游戏操作。$\r$\n$\r$\n点“下一步”继续。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "现在运行 ${APPNAME}"
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_SHOWREADME_TEXT "在桌面创建快捷方式"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION DesktopShortcut
!define MUI_FINISHPAGE_SHOWREADME_CHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function DesktopShortcut
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0
FunctionEnd

Section "安装"
  SetOutPath "$INSTDIR"
  ; 旧版本还在运行的话先关掉
  nsExec::ExecToLog 'taskkill /F /IM "${EXENAME}" /T'
  nsExec::ExecToLog 'taskkill /F /IM "TartarusSolver.exe" /T'
  File /r "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\卸载.exe"
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0
  CreateShortcut "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk" "$INSTDIR\卸载.exe"
  WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${REGKEY}" "Publisher" "困兽玩法推理（基于 glcoder/TartarusSolver）"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '"$INSTDIR\卸载.exe"'
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${REGKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'taskkill /F /IM "${EXENAME}" /T'
  nsExec::ExecToLog 'taskkill /F /IM "TartarusSolver.exe" /T'
  RMDir /r "$INSTDIR"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey HKCU "Software\${APPNAME}"
  ; 设置和报告（%LOCALAPPDATA%\TartarusSolver）留着，重装后还在
SectionEnd

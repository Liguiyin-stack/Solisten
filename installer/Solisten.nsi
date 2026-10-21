; Solisten 安装程序（NSIS 3）。由 scripts/build-installer.sh 调用：
;   makensis /DVERSION=1.0.0 /DSOURCE=<程序目录> /DOUTFILE=<输出 exe> /DSIZEKB=<安装后大小> installer\Solisten.nsi
; 只为当前用户安装（不需要管理员权限），装到 %LOCALAPPDATA%\Programs\Solisten。

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel user
ManifestDPIAware true

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef SOURCE
  !error "需要 /DSOURCE=<发布目录>"
!endif
!ifndef OUTFILE
  !define OUTFILE "Solisten-Setup-${VERSION}-x64.exe"
!endif
!ifndef SIZEKB
  !define SIZEKB 0
!endif

!define APP "Solisten"
!define EXE "Solisten.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"
!define ICON "${SOURCE}\Assets\Solisten.ico"

Name "${APP}"
Caption "${APP} ${VERSION} 安装"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "Software\${APP}" "InstallDir"
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Solisten"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 ${APP} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "一首歌，一个空间。$\r$\n$\r$\n${APP} 是一个 Windows 原生的本地音乐播放器：动态封面、整屏歌词，可选连接网易云音乐同步歌单。$\r$\n$\r$\n安装只针对当前用户，不需要管理员权限。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${APP}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; 正在运行时文件被占用：请用户确认后关闭它
!macro CloseRunning
  nsExec::ExecToStack 'cmd /c tasklist /FI "IMAGENAME eq ${EXE}" /NH | find /I "${EXE}"'
  Pop $0
  ${If} $0 == 0
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "${APP} 正在运行。点击“确定”关闭它并继续。" IDOK +2
    Abort
    nsExec::Exec 'taskkill /IM ${EXE}'
    Sleep 1500
    nsExec::Exec 'taskkill /F /IM ${EXE}'
    Sleep 500
  ${EndIf}
!macroend

Section "${APP}（必需）" SecMain
  SectionIn RO
  !insertmacro CloseRunning
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${SOURCE}\*.*"

  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\${APP}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" ${SIZEKB}
SectionEnd

Section "桌面快捷方式" SecDesktop
  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "程序文件和开始菜单快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在桌面上放一个快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; 卸载时会删除整个安装目录：只允许以 "Solisten" 结尾的目录，避免误删其他文件。
Function .onVerifyInstDir
  StrLen $0 "${APP}"
  StrCpy $1 "$INSTDIR" "" -$0
  ${If} $1 != "${APP}"
    Abort
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro CloseRunning
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP}"

  ; 曲库数据（播放记录、收藏、标签、缓存）默认保留；静默卸载时不删
  IfSilent done
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "是否同时删除 ${APP} 的曲库数据和缓存？$\r$\n$\r$\n（播放记录、收藏、标签、备注与封面缓存，位于 $LOCALAPPDATA\${APP}。音乐文件本身不会被删除。）" IDNO done
  RMDir /r "$LOCALAPPDATA\${APP}"
  done:
SectionEnd

; ============================================================
;  Local Model Hub - NSIS 安装程序
;  标准 Windows 桌面程序交付：per-user 安装、开始菜单/桌面快捷方式、
;  “应用和功能”卸载项、静默安装（/S）、保留用户模型数据。
;  构建：makensis installer/LocalModelHub.nsi
; ============================================================

Unicode true
ManifestDPIAware true
ManifestSupportedOS all

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

; ---------------------------- 常量 ----------------------------
!define APP_NAME      "星坞"
!define APP_NAME_CN   "星坞 · 本地模型启动器"
!define APP_EXE       "LocalModelHub.exe"
!define APP_VERSION   "0.34.0"
!define APP_PUBLISHER "星坞"
!define APP_ID        "LocalModelHub"
!define UNINST_KEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
!define BUILD_DIR     "../dist/win-x64"
!define APP_ASSETS    "../src/LocalModelHub.App/Assets"

; ---------------------------- 基本信息 ----------------------------
Name "${APP_NAME} ${APP_VERSION}"
OutFile "../../LocalModelHub-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_ID}"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallDir"
; 以当前用户可获得的最高权限运行：管理员账户下自动提权，确保安装前能关闭正在运行的星坞
RequestExecutionLevel highest
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${APP_NAME} ${APP_VERSION}"
ShowInstDetails nevershow
ShowUninstDetails nevershow
CRCCheck on
XPStyle on

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName"     "${APP_NAME_CN}"
VIAddVersionKey /LANG=2052 "FileDescription" "星坞安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"     "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright"  "Copyright (C) 2026 ${APP_PUBLISHER}"
VIAddVersionKey /LANG=1033 "ProductName"     "Xingwu"
VIAddVersionKey /LANG=1033 "FileDescription" "Xingwu Setup"
VIAddVersionKey /LANG=1033 "FileVersion"     "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey /LANG=1033 "LegalCopyright"  "Copyright (C) 2026 ${APP_PUBLISHER}"

; ---------------------------- 界面 ----------------------------
!define MUI_ABORTWARNING
!define MUI_ICON              "${APP_ASSETS}/app.ico"
!define MUI_UNICON            "${APP_ASSETS}/app.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP       "assets/header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets/welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "assets/welcome.bmp"

!define MUI_WELCOMEPAGE_TITLE "$(WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT  "$(WelcomeText)"

!define MUI_FINISHPAGE_TITLE  "$(FinishTitle)"
!define MUI_FINISHPAGE_RUN    "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(RunAppText)"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------------------------- 本地化字符串 ----------------------------
LangString WelcomeTitle  2052 "欢迎安装星坞"
LangString WelcomeTitle  1033 "Welcome to Xingwu Setup"
LangString WelcomeText   2052 "本向导将把星坞安装到这台电脑。$\r$\n$\r$\n星坞是本地大模型启动器：一键检测硬件性能、推荐适配模型、从国内外模型商店下载 GGUF 模型，并调用 llama.cpp 在本机启动推理服务。$\r$\n$\r$\n安装范围默认为当前用户；安装程序会请求管理员权限，以便自动关闭正在运行的星坞并替换程序文件。"
LangString WelcomeText   1033 "This wizard will install Xingwu on your computer.$\r$\n$\r$\nXingwu (Local Model Hub) detects your hardware, recommends models that fit, downloads GGUF models from ModelScope and Hugging Face, and serves inference locally with llama.cpp.$\r$\n$\r$\nThe app is installed for the current user. Setup requests administrator rights so it can close a running Xingwu instance and replace program files."

LangString FinishTitle   2052 "星坞安装完成"
LangString FinishTitle   1033 "Xingwu has been installed"
LangString RunAppText    2052 "立即启动星坞"
LangString RunAppText    1033 "Launch Xingwu now"

LangString SecMain       2052 "星坞主程序"
LangString SecMain       1033 "Xingwu application"
LangString SecDesktop    2052 "创建桌面快捷方式"
LangString SecDesktop    1033 "Create a desktop shortcut"
LangString DescMain      2052 "安装程序文件、开始菜单快捷方式、卸载程序，并注册到“应用和功能”。"
LangString DescMain      1033 "Installs the application files, Start Menu shortcuts, uninstaller and registers the app in Apps & features."
LangString DescDesktop   2052 "在桌面创建星坞快捷方式。"
LangString DescDesktop   1033 "Creates a Xingwu shortcut on the Desktop."

LangString UninstallShortcut 2052 "卸载星坞"
LangString UninstallShortcut 1033 "Uninstall Xingwu"
LangString AppShortcutDesc   2052 "本地模型启动器"
LangString AppShortcutDesc   1033 "Local model launcher"

LangString MsgNeedWin10  2052 "星坞需要 Windows 10 或更高版本。"
LangString MsgNeedWin10  1033 "Xingwu requires Windows 10 or later."
LangString MsgNeedX64    2052 "星坞仅支持 64 位 Windows。"
LangString MsgNeedX64    1033 "Xingwu requires 64-bit Windows."
LangString AskRemoveData 2052 "是否同时删除已下载的模型和设置？$\r$\n$\r$\n选择“是”将永久删除用户数据目录（$LOCALAPPDATA\LocalModelHub）。$\r$\n选择“否”将保留模型与设置，重新安装后可继续使用。"
LangString AskRemoveData 1033 "Also delete downloaded models and settings?$\r$\n$\r$\nYes permanently removes the user data folder ($LOCALAPPDATA\LocalModelHub).$\r$\nNo keeps your models and settings for a future reinstall."

LangString MsgAppRunning 2052 "安装程序已请求最高权限并自动尝试关闭星坞，但进程仍被占用。$\r$\n$\r$\n请手动关闭星坞后点击“重试”；或右键安装包，选择“以管理员身份运行”。"
LangString MsgAppRunning 1033 "Setup requested the highest available privileges and tried to close Xingwu automatically, but the process is still locked.$\r$\n$\r$\nClose Xingwu manually and click Retry, or right-click the installer and choose Run as administrator."

; ---------------------------- 初始化 ----------------------------
Function .onInit
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP|MB_OK "$(MsgNeedWin10)"
    Abort
  ${EndIf}

  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "$(MsgNeedX64)"
    Abort
  ${EndIf}

  ; 按系统语言选择界面语言（中文系统默认简体中文）
  ReadRegStr $0 HKLM "SYSTEM\CurrentControlSet\Control\Nls\Language" "InstallLanguage"
  ${If} $0 == "0804"
    StrCpy $LANGUAGE 2052
  ${Else}
    StrCpy $LANGUAGE 1033
  ${EndIf}
FunctionEnd

; 先请求正常退出（让应用停止推理进程并保存设置/会话），超时后再强制结束进程树
!macro EnsureAppClosedBody
  DetailPrint "正在关闭正在运行的星坞…"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /T /IM "${APP_EXE}"'
  Pop $0
  Sleep 3000

  StrCpy $1 0
  EnsureAppClosed_retry:
    nsExec::Exec 'cmd /c tasklist /FI "IMAGENAME eq ${APP_EXE}" /NH | find /I "${APP_EXE}" >nul'
    Pop $0
    ${If} $0 != 0
      Return
    ${EndIf}

    ; 已请求正常退出但仍在运行：强制结束进程树
    nsExec::Exec '"$SYSDIR\taskkill.exe" /F /T /IM "${APP_EXE}"'
    Pop $0
    Sleep 800

    nsExec::Exec 'cmd /c tasklist /FI "IMAGENAME eq ${APP_EXE}" /NH | find /I "${APP_EXE}" >nul'
    Pop $0
    ${If} $0 != 0
      Return
    ${EndIf}

    IntOp $1 $1 + 1
    ${If} $1 >= 8
      IfSilent silentAbort
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(MsgAppRunning)" IDRETRY EnsureAppClosed_retry
      Abort
      silentAbort:
      Abort
    ${EndIf}
  Goto EnsureAppClosed_retry
!macroend

Function EnsureAppClosed
  !insertmacro EnsureAppClosedBody
FunctionEnd

Function un.EnsureAppClosed
  !insertmacro EnsureAppClosedBody
FunctionEnd

; ---------------------------- 安装 ----------------------------
Section "$(SecMain)" SEC_MAIN
  SectionIn RO
  SetShellVarContext current
  SetOverwrite on

  ; 升级安装前关闭正在运行的实例，避免文件占用；随后删除旧主程序以便覆盖
  Call EnsureAppClosed
  Delete "$INSTDIR\${APP_EXE}"

  SetOutPath "$INSTDIR"
  File /r "${BUILD_DIR}/*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 清理旧品牌快捷方式（0.16.0 及更早版本显示为 Local Model Hub）
  Delete "$DESKTOP\Local Model Hub.lnk"
  RMDir /r "$SMPROGRAMS\Local Model Hub"

  ; 开始菜单
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(AppShortcutDesc)"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\$(UninstallShortcut).lnk" "$INSTDIR\Uninstall.exe"

  ; 安装位置记录
  WriteRegStr HKCU "Software\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\${APP_ID}" "Version" "${APP_VERSION}"

  ; “应用和功能”卸载信息
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME_CN}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" $0

  ; 命令行启动支持（Win+R 输入 LocalModelHub.exe）
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\${APP_EXE}" "" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\${APP_EXE}" "Path" "$INSTDIR"
SectionEnd

Section "$(SecDesktop)" SEC_DESKTOP
  SetShellVarContext current
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(AppShortcutDesc)"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
!insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    "$(DescMain)"
!insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "$(DescDesktop)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------------------- 卸载 ----------------------------
Section "Uninstall"
  SetShellVarContext current

  Call un.EnsureAppClosed

  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$DESKTOP\Local Model Hub.lnk"
  RMDir /r "$SMPROGRAMS\${APP_NAME}"
  RMDir /r "$SMPROGRAMS\Local Model Hub"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP_ID}"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\${APP_EXE}"

  ; 用户数据（模型/设置）默认保留：静默卸载或明确选择“是”时才删除
  IfSilent keepData
  MessageBox MB_YESNO|MB_DEFBUTTON2 "$(AskRemoveData)" IDNO keepData
  RMDir /r "$LOCALAPPDATA\${APP_ID}"
  keepData:
SectionEnd

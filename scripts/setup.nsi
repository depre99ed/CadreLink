Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WordFunc.nsh"
!insertmacro VersionCompare
!addplugindir "${TAURI_PLUGIN_DIR}"

!define PRODUCT "CadreLink"
!define UNINSTALL_ENTRY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CadreLink"
!define CLSID "{14E7C1D2-A787-4C88-9817-F2F49F633B8E}"
!define RULE_NAME "CadreLink - lien local"

Name "${PRODUCT}"
BrandingText "CadreLink"
OutFile "${ARTIFACT}"
InstallDir "$PROGRAMFILES64\CadreLink"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1036 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installation de CadreLink"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "CompanyName" "${PRODUCT}"
VIAddVersionKey /LANG=1036 "OriginalFilename" "CadreLink-setup.exe"
VIAddVersionKey /LANG=1036 "LegalCopyright" "CadreLink"

!define MUI_ICON "${WORKSPACE}\windows\Assets\CadreLink.ico"
!define MUI_UNICON "${WORKSPACE}\windows\Assets\CadreLink.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Installer CadreLink"
!define MUI_WELCOMEPAGE_TEXT "Installe le studio, la caméra virtuelle Windows et le compagnon Android. Ferme CadreLink (ou Focale) et libère la caméra dans OBS ou Discord avant l’installation.$\r$\n$\r$\nLes droits administrateur servent à enregistrer la caméra et à autoriser le réseau local."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Ouvrir CadreLink"
!define MUI_FINISHPAGE_RUN_FUNCTION RunStudio
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Créer un raccourci sur le Bureau"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION DesktopShortcut
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "French"

Var PreviousDirectory
Var Failure

Function .onInit
    ${IfNot} ${IsNativeAMD64}
        MessageBox MB_ICONSTOP "CadreLink nécessite Windows 11 sur un processeur x64."
        Abort
    ${EndIf}
    SetRegView 64
    ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
    ${If} $0 < 22000
        MessageBox MB_ICONSTOP "CadreLink nécessite Windows 11."
        Abort
    ${EndIf}
    ReadRegStr $PreviousDirectory HKLM "${UNINSTALL_ENTRY}" "InstallLocation"
    ReadRegStr $0 HKLM "${UNINSTALL_ENTRY}" "DisplayVersion"
    ${If} $0 != ""
        ${VersionCompare} "$0" "${VERSION}" $1
        ${If} $1 == 1
            MessageBox MB_ICONSTOP "Une version plus récente est déjà installée."
            Abort
        ${EndIf}
    ${EndIf}
    ${If} $PreviousDirectory != ""
        StrCpy $INSTDIR $PreviousDirectory
    ${EndIf}
FunctionEnd

Function RunStudio
    nsis_tauri_utils::RunAsUser "$INSTDIR\CadreLink.exe" ""
FunctionEnd

Function DesktopShortcut
    SetShellVarContext current
    CreateShortCut "$DESKTOP\CadreLink.lnk" "$INSTDIR\CadreLink.exe"
FunctionEnd

Section "CadreLink"
    SetRegView 64
    SetShellVarContext all
    FindWindow $0 "" "CadreLink"
    FindWindow $1 "" "Focale"
    ${If} $0 != 0
    ${OrIf} $1 != 0
        MessageBox MB_ICONEXCLAMATION "Ferme la fenêtre CadreLink ou Focale, puis relance l’installation."
        Abort
    ${EndIf}
    ${If} $PreviousDirectory != ""
    ${AndIf} $INSTDIR != $PreviousDirectory
        MessageBox MB_ICONEXCLAMATION "Pour déplacer CadreLink, désinstalle d’abord la version présente."
        Abort
    ${EndIf}

    SetOutPath "$INSTDIR"
    File "${WORKSPACE}\build\studio\CadreLink.exe"
    File "${WORKSPACE}\build\native\CadreLinkCamera.dll"
    File "${WORKSPACE}\build\pose-lightning.onnx"
    File "${WORKSPACE}\dist\CadreLink-Android.apk"
    File "${WORKSPACE}\NOTICE.txt"
    File "${WORKSPACE}\build\ONNX-THIRD-PARTY-NOTICES.txt"
    File "${WORKSPACE}\README.md"

    DetailPrint "Enregistrement de la caméra CadreLink…"
    WriteRegStr HKLM "Software\Classes\CLSID\${CLSID}\InprocServer32" "" "$INSTDIR\CadreLinkCamera.dll"
    WriteRegStr HKLM "Software\Classes\CLSID\${CLSID}\InprocServer32" "ThreadingModel" "Both"
    ExecWait '"$INSTDIR\CadreLink.exe" --install-camera' $0
    ${If} $0 != 0
        StrCpy $Failure "Windows n’a pas enregistré la caméra (code $0). Vérifie les réglages de confidentialité Caméra de Windows."
        Goto failed
    ${EndIf}

    DetailPrint "Autorisation du lien local sur le pare-feu…"
    nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall set rule name="${RULE_NAME}" new program="$INSTDIR\CadreLink.exe"'
    Pop $0
    ${If} $0 != 0
        nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="${RULE_NAME}" dir=in action=allow protocol=TCP localport=47472-47473 remoteip=LocalSubnet program="$INSTDIR\CadreLink.exe" profile=any'
        Pop $0
        ${If} $0 != 0
            StrCpy $Failure "La règle du pare-feu pour Android n’a pas été créée (code $0)."
            Goto failed
        ${EndIf}
    ${EndIf}
    WriteUninstaller "$INSTDIR\uninstall.exe"
    CreateShortCut "$SMPROGRAMS\CadreLink.lnk" "$INSTDIR\CadreLink.exe"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "DisplayName" "CadreLink"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "Publisher" "CadreLink"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "DisplayIcon" "$INSTDIR\CadreLink.exe,0"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "URLInfoAbout" "https://github.com/depre99ed/CadreLink"
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
    WriteRegStr HKLM "${UNINSTALL_ENTRY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
    WriteRegDWORD HKLM "${UNINSTALL_ENTRY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINSTALL_ENTRY}" "NoRepair" 1
    Goto finished

failed:
    ${If} $PreviousDirectory == ""
        ExecWait '"$INSTDIR\CadreLink.exe" --remove-camera'
        DeleteRegKey HKLM "Software\Classes\CLSID\${CLSID}"
        Delete "$INSTDIR\CadreLink.exe"
        Delete "$INSTDIR\CadreLinkCamera.dll"
        Delete "$INSTDIR\pose-lightning.onnx"
        Delete "$INSTDIR\CadreLink-Android.apk"
        Delete "$INSTDIR\NOTICE.txt"
        Delete "$INSTDIR\ONNX-THIRD-PARTY-NOTICES.txt"
        Delete "$INSTDIR\README.md"
        RMDir "$INSTDIR"
    ${EndIf}
    DetailPrint "$Failure"
    MessageBox MB_ICONSTOP "$Failure"
    SetErrorLevel 2
    Abort
finished:
SectionEnd

Function un.onInit
    SetRegView 64
    FindWindow $0 "" "CadreLink"
    ${If} $0 != 0
        MessageBox MB_ICONEXCLAMATION "Ferme CadreLink avant de le désinstaller."
        Abort
    ${EndIf}
FunctionEnd

Section "Uninstall"
    SetRegView 64
    SetShellVarContext all
    ExecWait '"$INSTDIR\CadreLink.exe" --remove-camera'
    nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${RULE_NAME}" program="$INSTDIR\CadreLink.exe"'
    Pop $0
    DeleteRegKey HKLM "Software\Classes\CLSID\${CLSID}"
    DeleteRegKey HKLM "${UNINSTALL_ENTRY}"
    Delete "$SMPROGRAMS\CadreLink.lnk"
    SetShellVarContext current
    Delete "$DESKTOP\CadreLink.lnk"
    Delete /REBOOTOK "$INSTDIR\CadreLink.exe"
    Delete /REBOOTOK "$INSTDIR\CadreLinkCamera.dll"
    Delete "$INSTDIR\pose-lightning.onnx"
    Delete "$INSTDIR\CadreLink-Android.apk"
    Delete "$INSTDIR\NOTICE.txt"
    Delete "$INSTDIR\ONNX-THIRD-PARTY-NOTICES.txt"
    Delete "$INSTDIR\README.md"
    Delete "$INSTDIR\uninstall.exe"
    RMDir /REBOOTOK "$INSTDIR"
SectionEnd

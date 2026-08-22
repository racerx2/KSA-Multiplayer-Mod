; KSA Multiplayer Mod client installer

Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"

Name "KSA Multiplayer Mod 0.4.0"
OutFile "KSA-Multiplayer-Setup-v0.4.0.exe"
InstallDir "$PROGRAMFILES64\Kitten Space Agency"
InstallDirRegKey HKLM "Software\Kitten Space Agency" "InstallPath"
RequestExecutionLevel admin

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Section "Install multiplayer client"
    IfFileExists "$INSTDIR\KSA.dll" ksa_found
        MessageBox MB_OK|MB_ICONSTOP "KSA.dll was not found in $INSTDIR.$\r$\nSelect your Kitten Space Agency installation folder."
        Abort
    ksa_found:

    ; The managed updater installs only the client mod, its pinned StarMap loader,
    ; and a private .NET runtime. It never starts a dedicated server on this PC.
    SetOutPath "$PLUGINSDIR\bundle\scripts"
    File "..\scripts\Update-KSAMultiplayer.ps1"
    SetOutPath "$PLUGINSDIR\bundle\KSA-Multiplayer-Package\Content\Multiplayer"
    File "Content\Multiplayer\Multiplayer.dll"
    File "Content\Multiplayer\mod.toml"

    DetailPrint "Installing the multiplayer client and managed dependencies..."
    ExecWait '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\bundle\scripts\Update-KSAMultiplayer.ps1" -GameDirectory "$INSTDIR"' $0
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONSTOP "KSA Multiplayer installation failed with exit code $0."
        Abort
    ${EndIf}
SectionEnd

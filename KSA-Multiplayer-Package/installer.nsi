; KSA Multiplayer Mod Installer
; NSIS Script

!include "MUI2.nsh"

; Installer Info
Name "KSA Multiplayer Mod"
OutFile "KSA-Multiplayer-Setup.exe"
InstallDir "$PROGRAMFILES64\Kitten Space Agency"
InstallDirRegKey HKLM "Software\Kitten Space Agency" "InstallPath"
RequestExecutionLevel admin

; UI Settings
!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Language
!insertmacro MUI_LANGUAGE "English"

; Installer Section
Section "Install"
    SetOutPath "$INSTDIR"
    
    ; Check KSA.exe exists
    IfFileExists "$INSTDIR\KSA.exe" found_ksa
        MessageBox MB_OK|MB_ICONSTOP "KSA.exe not found in $INSTDIR$\r$\nPlease select your KSA installation folder."
        Abort
    found_ksa:
    
    ; Create and install to Launcher folder
    CreateDirectory "$INSTDIR\Launcher"
    SetOutPath "$INSTDIR\Launcher"
    File "Launcher\KSA.ModLoader.exe"
    File "Launcher\KSA.ModLoader.dll"
    File "Launcher\KSA.ModLoader.deps.json"
    File "Launcher\KSA.ModLoader.runtimeconfig.json"
    File "Launcher\0Harmony.dll"
    File "Launcher\KSA.ModLoader.cmd"
    
    ; Install Multiplayer mod
    CreateDirectory "$INSTDIR\Content\Multiplayer"
    SetOutPath "$INSTDIR\Content\Multiplayer"
    File "Content\Multiplayer\KSA.Mods.Multiplayer.dll"
    File "Content\Multiplayer\mod.toml"
    
    ; Create logs directory
    CreateDirectory "$INSTDIR\Content\Multiplayer\logs"
    
    ; Update manifest.toml
    SetOutPath "$INSTDIR\Content"
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "$$m = Get-Content \"$INSTDIR\Content\manifest.toml\" -Raw; if ($$m -notmatch \"id\s*=\s*\`\"Multiplayer\`\"\") { Add-Content \"$INSTDIR\Content\manifest.toml\" \"`n`n[[mods]]`nid = `\"Multiplayer`\"`nenabled = true\" }"'
    
    ; Create desktop shortcut pointing to EXE in Launcher folder
    SetOutPath "$INSTDIR\Launcher"
    Delete "$DESKTOP\KSA with Mods.lnk"
    CreateShortcut "$DESKTOP\KSA with Mods.lnk" "$INSTDIR\Launcher\KSA.ModLoader.exe" "" "$INSTDIR\KSA.exe" 0
    
    ; Set "Run as Administrator" flag on the shortcut
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "$$lnk = \"$DESKTOP\KSA with Mods.lnk\"; $$bytes = [System.IO.File]::ReadAllBytes($$lnk); $$bytes[0x15] = $$bytes[0x15] -bor 0x20; [System.IO.File]::WriteAllBytes($$lnk, $$bytes)"'
    
SectionEnd

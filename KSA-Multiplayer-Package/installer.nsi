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
    
    ; === ICONS ===
    ; Install icons to KSA root for shortcut use
    SetOutPath "$INSTDIR"
    File "Client.ico"
    File "Server.ico"
    
    ; === MOD LOADER ===
    ; Note: Using StarMap mod loader (installed separately)
    ; Keeping Launcher folder files for backwards compatibility
    CreateDirectory "$INSTDIR\Launcher"
    SetOutPath "$INSTDIR\Launcher"
    File "Launcher\KSA.ModLoader.exe"
    File "Launcher\KSA.ModLoader.dll"
    File "Launcher\KSA.ModLoader.deps.json"
    File "Launcher\KSA.ModLoader.runtimeconfig.json"
    File "Launcher\0Harmony.dll"
    File "Launcher\KSA.ModLoader.cmd"
    
    ; === CLIENT MOD ===
    ; Install Multiplayer mod
    CreateDirectory "$INSTDIR\Content\Multiplayer"
    SetOutPath "$INSTDIR\Content\Multiplayer"
    File "Content\Multiplayer\Multiplayer.dll"
    File "Content\Multiplayer\mod.toml"
    File "LICENSE"
    
    ; Create logs directory
    CreateDirectory "$INSTDIR\Content\Multiplayer\logs"
    
    ; === DEDICATED SERVER ===
    ; Install server to KSA root (uses KSA's DLLs via system .NET)
    SetOutPath "$INSTDIR"
    File "Server\RunServer.cmd"
    File "Server\KSA-Dedicated-Server.dll"
    File "Server\KSA-Dedicated-Server.deps.json"
    File "Server\KSA-Dedicated-Server.runtimeconfig.json"
    File "LICENSE"
    
    ; Install server config (don't overwrite if exists)
    IfFileExists "$INSTDIR\server_config.json" skip_config
        File "Server\server_config.json"
    skip_config:
    
    ; === MANIFEST UPDATE ===
    ; Update manifest.toml to enable mod
    SetOutPath "$INSTDIR\Content"
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "$$m = Get-Content \"$INSTDIR\Content\manifest.toml\" -Raw; if ($$m -notmatch \"id\s*=\s*\`\"Multiplayer\`\"\") { Add-Content \"$INSTDIR\Content\manifest.toml\" \"`n`n[[mods]]`nid = `\"Multiplayer`\"`nenabled = true\" }"'
    
    ; === DESKTOP SHORTCUTS ===
    ; Delete old shortcuts first
    Delete "$DESKTOP\KSA with Mods.lnk"
    Delete "$DESKTOP\KSA Dedicated Server.lnk"
    
    ; Create "KSA with Mods" shortcut pointing to StarMap with Client.ico
    CreateShortcut "$DESKTOP\KSA with Mods.lnk" "$PROGRAMFILES32\StarMap\StarMap.exe" "" "$INSTDIR\Client.ico" 0
    
    ; Create "KSA Dedicated Server" shortcut with Server.ico  
    CreateShortcut "$DESKTOP\KSA Dedicated Server.lnk" "$INSTDIR\RunServer.cmd" "" "$INSTDIR\Server.ico" 0
    
    ; Set "Run as Administrator" flag on shortcuts
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "$$lnk = \"$DESKTOP\KSA with Mods.lnk\"; $$bytes = [System.IO.File]::ReadAllBytes($$lnk); $$bytes[0x15] = $$bytes[0x15] -bor 0x20; [System.IO.File]::WriteAllBytes($$lnk, $$bytes)"'
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "$$lnk = \"$DESKTOP\KSA Dedicated Server.lnk\"; $$bytes = [System.IO.File]::ReadAllBytes($$lnk); $$bytes[0x15] = $$bytes[0x15] -bor 0x20; [System.IO.File]::WriteAllBytes($$lnk, $$bytes)"'
    
SectionEnd

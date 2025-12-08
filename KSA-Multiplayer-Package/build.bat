@echo off
setlocal

echo ========================================
echo   KSA Multiplayer Mod - Build ^& Package
echo ========================================
echo.

set "MOD_PATH=C:\temp\KSA-Multiplayer-Mod"
set "MODLOADER_PATH=C:\temp\KSA-ModLoader"
set "PACKAGE_PATH=%~dp0"

echo Building mod from: %MOD_PATH%
echo.

:: Build the mod
pushd "%MOD_PATH%"
dotnet build --no-incremental
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Mod build failed!
    popd
    pause
    exit /b 1
)
popd

echo.
echo Building ModLoader from: %MODLOADER_PATH%
echo.

:: Build the ModLoader
pushd "%MODLOADER_PATH%"
dotnet publish -c Release -r win-x64 --self-contained false -o "%MODLOADER_PATH%\bin\Publish"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: ModLoader build failed!
    popd
    pause
    exit /b 1
)
popd

echo.
echo Copying files to package...

:: Copy mod DLL to Content\Multiplayer
copy /Y "%MOD_PATH%\bin\Debug\KSA.Mods.Multiplayer.dll" "%PACKAGE_PATH%Content\Multiplayer\"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to copy mod DLL!
    pause
    exit /b 1
)

:: Copy ModLoader files to Launcher folder
copy /Y "%MODLOADER_PATH%\bin\Publish\KSA.ModLoader.exe" "%PACKAGE_PATH%Launcher\"
copy /Y "%MODLOADER_PATH%\bin\Publish\KSA.ModLoader.dll" "%PACKAGE_PATH%Launcher\"
copy /Y "%MODLOADER_PATH%\bin\Publish\KSA.ModLoader.deps.json" "%PACKAGE_PATH%Launcher\"
copy /Y "%MODLOADER_PATH%\bin\Publish\KSA.ModLoader.runtimeconfig.json" "%PACKAGE_PATH%Launcher\"

echo.
echo ========================================
echo   Build Complete!
echo ========================================
echo.
echo Package updated:
echo   %PACKAGE_PATH%Content\Multiplayer\KSA.Mods.Multiplayer.dll
echo   %PACKAGE_PATH%Launcher\KSA.ModLoader.*
echo.

:: Build installer
echo Building installer...
echo.

set "NSIS_PATH="
if exist "C:\Program Files (x86)\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files (x86)\NSIS\makensis.exe"
if exist "C:\Program Files\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files\NSIS\makensis.exe"

if "%NSIS_PATH%"=="" (
    echo WARNING: NSIS not found, skipping installer build
    echo Install NSIS from: https://nsis.sourceforge.io/Download
) else (
    "%NSIS_PATH%" "%PACKAGE_PATH%installer.nsi"
    if %ERRORLEVEL% EQU 0 (
        echo.
        echo Installer built: %PACKAGE_PATH%KSA-Multiplayer-Setup.exe
    ) else (
        echo ERROR: Installer build failed!
    )
)

echo.
pause

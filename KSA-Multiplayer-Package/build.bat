@echo off
setlocal

echo ========================================
echo   KSA Multiplayer Mod - Build ^& Package
echo ========================================
echo.

set "CLIENT_PATH=C:\temp\KSA_Dedicated_Server\Client"
set "SERVER_PATH=C:\temp\KSA_Dedicated_Server\Server"
set "PACKAGE_PATH=%~dp0"

:: Build the client mod
echo Building client mod from: %CLIENT_PATH%
echo.

pushd "%CLIENT_PATH%"
dotnet build -c Release --no-incremental
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Client mod build failed!
    popd
    pause
    exit /b 1
)
popd

echo.
echo Building server from: %SERVER_PATH%
echo.

:: Build the server
pushd "%SERVER_PATH%"
dotnet build -c Release --no-incremental
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Server build failed!
    popd
    pause
    exit /b 1
)
popd

echo.
echo Copying files to package...

:: Copy client mod DLL
copy /Y "%CLIENT_PATH%\bin\Release\KSA.Mods.Multiplayer.dll" "%PACKAGE_PATH%Content\Multiplayer\"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to copy client mod DLL!
    pause
    exit /b 1
)

:: Copy server files
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.exe" "%PACKAGE_PATH%Server\"
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.dll" "%PACKAGE_PATH%Server\"
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.deps.json" "%PACKAGE_PATH%Server\"
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.runtimeconfig.json" "%PACKAGE_PATH%Server\"

echo.
echo ========================================
echo   Build Complete!
echo ========================================
echo.
echo Package updated:
echo   %PACKAGE_PATH%Content\Multiplayer\KSA.Mods.Multiplayer.dll
echo   %PACKAGE_PATH%Server\KSA-Dedicated-Server.*
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

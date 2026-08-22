@echo off
setlocal

set "PACKAGE_PATH=%~dp0"
for %%I in ("%PACKAGE_PATH%..") do set "REPO_ROOT=%%~fI"
set "CLIENT_PATH=%REPO_ROOT%\Client"
set "SERVER_PATH=%REPO_ROOT%\Server"
set "DOTNET_EXE=dotnet"

if exist "%REPO_ROOT%\Server_Deploy\.dotnet-sdk\dotnet.exe" (
    set "DOTNET_EXE=%REPO_ROOT%\Server_Deploy\.dotnet-sdk\dotnet.exe"
)

echo Building KSA Multiplayer from %REPO_ROOT%

pushd "%CLIENT_PATH%"
"%DOTNET_EXE%" build -c Release --no-incremental
if errorlevel 1 (
    popd
    exit /b 1
)
popd

pushd "%SERVER_PATH%"
"%DOTNET_EXE%" build -c Release --no-incremental
if errorlevel 1 (
    popd
    exit /b 1
)
popd

copy /Y "%CLIENT_PATH%\bin\Release\Multiplayer.dll" "%PACKAGE_PATH%Content\Multiplayer\Multiplayer.dll" >nul
copy /Y "%CLIENT_PATH%\mod.toml" "%PACKAGE_PATH%Content\Multiplayer\mod.toml" >nul
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.dll" "%PACKAGE_PATH%Server\KSA-Dedicated-Server.dll" >nul
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.deps.json" "%PACKAGE_PATH%Server\KSA-Dedicated-Server.deps.json" >nul
copy /Y "%SERVER_PATH%\bin\Release\net10.0\KSA-Dedicated-Server.runtimeconfig.json" "%PACKAGE_PATH%Server\KSA-Dedicated-Server.runtimeconfig.json" >nul

set "NSIS_PATH="
if exist "C:\Program Files (x86)\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files (x86)\NSIS\makensis.exe"
if exist "C:\Program Files\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files\NSIS\makensis.exe"

if not defined NSIS_PATH (
    echo NSIS was not found. Binaries were packaged, but the installer was not built.
    exit /b 2
)

"%NSIS_PATH%" "%PACKAGE_PATH%installer.nsi"
exit /b %ERRORLEVEL%

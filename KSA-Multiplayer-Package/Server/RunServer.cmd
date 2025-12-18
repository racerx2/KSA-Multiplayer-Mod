@echo off
setlocal

REM KSA Dedicated Server Launcher
REM Forces use of system .NET runtime instead of KSA's bundled runtime

set DOTNET_ROOT=C:\Program Files\dotnet
set KSA_DIR=%~dp0
set KSA_DIR=%KSA_DIR:~0,-1%

REM Change to KSA directory so server can find game DLLs
cd /d "%KSA_DIR%"

REM Run the server DLL using system dotnet
"%DOTNET_ROOT%\dotnet.exe" "%KSA_DIR%\KSA-Dedicated-Server.dll" %*

if errorlevel 1 pause

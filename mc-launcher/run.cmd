@echo off
REM Optional machine configuration is private and ignored by Git.
setlocal
if exist "%~dp0launcher.local.cmd" call "%~dp0launcher.local.cmd"
set "DN=dotnet"
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DN=%DOTNET_ROOT%\dotnet.exe"
set "EXE=%~dp0src\Launcher.App\bin\Release\net10.0-windows\MCLauncher.exe"
if not exist "%EXE%" (
  "%DN%" build "%~dp0MinecraftLauncher.sln" -c Release -m:1 -v q --nologo || exit /b 1
)
start "" "%EXE%"

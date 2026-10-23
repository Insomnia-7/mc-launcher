@echo off
REM Optional machine configuration is private and ignored by Git.
setlocal
if exist "%~dp0launcher.local.cmd" call "%~dp0launcher.local.cmd"
set "DN=dotnet"
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DN=%DOTNET_ROOT%\dotnet.exe"
set "DLL=%~dp0tools\Launcher.Cli\bin\Release\net10.0\mcl.dll"
if not exist "%DLL%" (
  "%DN%" build "%~dp0MinecraftLauncher.sln" -c Release -m:1 -v q --nologo || exit /b 1
)
"%DN%" "%DLL%" %*
exit /b %errorlevel%

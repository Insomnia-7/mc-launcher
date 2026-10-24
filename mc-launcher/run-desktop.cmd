@echo off
setlocal
cd /d "%~dp0"
if exist "launcher.local.cmd" call "launcher.local.cmd"
set "desktop_dotnet=dotnet"
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "desktop_dotnet=%DOTNET_ROOT%\dotnet.exe"
if not exist "src\Launcher.Desktop\bin\Release\net10.0\MCLauncher.Desktop.dll" (
  "%desktop_dotnet%" build "src\Launcher.Desktop\Launcher.Desktop.csproj" -c Release
  if errorlevel 1 exit /b 1
)
"%desktop_dotnet%" "src\Launcher.Desktop\bin\Release\net10.0\MCLauncher.Desktop.dll" %*

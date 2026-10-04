@echo off
setlocal
title Make Dave installer
cd /d "%~dp0"
set "LOG=%~dp0installer-log.txt"
echo Make installer - %date% %time% > "%LOG%"
echo.
echo  ==== Making DaveSetup.exe ====
echo  (for sharing Dave: your friends only need that one file)
echo.

rem 1. Find a .NET SDK (8 or newer)
set "DOTNET="
for %%d in ("%USERPROFILE%\AndroidTools\dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe") do if not defined DOTNET if exist %%d call :check_sdk %%d
if not defined DOTNET for /f "delims=" %%d in ('where dotnet 2^>nul') do if not defined DOTNET call :check_sdk "%%d"
if not defined DOTNET (
    echo  No .NET 8 SDK found. Run "Build Dave.bat" once first: it installs it.
    pause
    exit /b 1
)
echo Using %DOTNET% >> "%LOG%"

rem 2. Find Inno Setup (makes the installer), install it if needed
call :find_inno
if not defined ISCC (
    echo  Installing Inno Setup, the free tool that makes the installer...
    winget install --id JRSoftware.InnoSetup -e --scope user --accept-package-agreements --accept-source-agreements >> "%LOG%" 2>&1
    call :find_inno
)
if not defined ISCC (
    echo  Couldn't install Inno Setup. Get it from https://jrsoftware.org/isdl.php and run this again.
    start "" "https://jrsoftware.org/isdl.php"
    pause
    exit /b 1
)
echo Using %ISCC% >> "%LOG%"

rem 3. Build Dave with .NET included, so it runs on any Windows 10/11 PC
echo  Building Dave (takes a minute)...
if exist "installer\app" rmdir /s /q "installer\app"
"%DOTNET%" publish windows\DaveWindows.csproj -c Release -r win-x64 --self-contained true -o installer\app -nologo >> "%LOG%" 2>&1
if errorlevel 1 goto failed
if not exist "installer\app\Dave.exe" goto failed

rem 4. Pack it into DaveSetup.exe (version from windows\DaveWindows.csproj)
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content windows\DaveWindows.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"') do set "VERSION=%%v"
if not defined VERSION set "VERSION=1.0.0"
echo  Packing the installer (version %VERSION%, takes a few minutes)...
"%ISCC%" /Q /DAppVersion=%VERSION% installer\Dave.iss >> "%LOG%" 2>&1
if errorlevel 1 goto failed
rmdir /s /q "installer\app"

echo.
echo  Done! The installer is installer\Output\DaveSetup.exe
echo  Share that file (for example as a GitHub release). Your own keys are NOT in it:
echo  they stay in your settings, and everyone enters their own.
echo OK >> "%LOG%"
explorer /select,"%~dp0installer\Output\DaveSetup.exe"
pause
exit /b 0

:failed
echo.
echo  Making the installer failed. Details are in installer-log.txt.
start "" notepad "%LOG%"
pause
exit /b 1

:check_sdk
for /f "tokens=1 delims=." %%v in ('"%~1" --list-sdks 2^>nul') do if %%v GEQ 8 set "DOTNET=%~1"
exit /b 0

:find_inno
set "ISCC="
for %%p in ("%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe") do if not defined ISCC if exist %%p set "ISCC=%%~p"
exit /b 0

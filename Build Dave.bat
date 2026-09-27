@echo off
setlocal
title Build Dave
cd /d "%~dp0"
echo.
echo  ==== Building Dave for Windows ====
echo.

rem 1. Is a .NET SDK (version 8 or newer) installed?
set "HAVE_SDK="
for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do if %%v GEQ 8 set "HAVE_SDK=1"
if defined HAVE_SDK goto build

echo  The .NET 8 SDK is needed to build Dave. Installing it now...
where winget >nul 2>nul
if errorlevel 1 goto manual_sdk
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
if errorlevel 1 goto manual_sdk
rem Pick up the new install without reopening this window.
set "PATH=%PATH%;%ProgramFiles%\dotnet"
goto build

:manual_sdk
echo.
echo  Couldn't install it automatically. Opening the download page:
echo  choose "SDK 8.0" for Windows x64, install it, then run this file again.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1

:build
rem Dave has to be closed, or his files can't be replaced (e.g. when updating)
tasklist /fi "imagename eq Dave.exe" | find /i "Dave.exe" >nul && (echo  Closing Dave so he can be updated... & taskkill /im Dave.exe /f >nul & timeout /t 2 >nul)

rem 2. Build
echo  Building (the first time takes a minute)...
cd windows
dotnet build -c Release -nologo -v q
if errorlevel 1 (
    echo.
    echo  Building failed. Scroll up to see why, or send a screenshot to whoever shared Dave with you.
    pause
    exit /b 1
)
set "DAVE=%CD%\bin\Release\net8.0-windows10.0.19041.0\win-x64\Dave.exe"

rem 3. Desktop shortcut
powershell -NoProfile -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Dave.lnk'); $s.TargetPath = '%DAVE%'; $s.WorkingDirectory = [IO.Path]::GetDirectoryName('%DAVE%'); $s.Description = 'Dave voice assistant'; $s.Save()"

rem 4. Start
echo.
echo  Done! Dave is starting. There's now a "Dave" shortcut on your desktop.
echo  First time: enter your own free Groq key in the settings window (console.groq.com/keys).
echo  Then press Ctrl+Alt+D and talk.
start "" "%DAVE%"
timeout /t 8 >nul

@echo off
setlocal
title Build Dave
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
echo Build Dave - %date% %time% > "%LOG%"
echo.
echo  ==== Building Dave for Windows ====
echo.

rem 1. Is a .NET SDK (version 8 or newer) installed?
call :find_sdk
if defined HAVE_SDK goto close_dave

echo  The .NET 8 SDK is needed to build Dave. Installing it now...
echo  (Windows may ask for permission: click Yes.)
echo No .NET SDK found, trying winget >> "%LOG%"
where winget >nul 2>nul
if errorlevel 1 goto manual_sdk
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements >> "%LOG%" 2>&1
rem winget can report an error even when it worked (e.g. "already installed"), so just check again.
set "PATH=%ProgramFiles%\dotnet;%PATH%"
call :find_sdk
if defined HAVE_SDK goto close_dave

:manual_sdk
echo.
echo  Couldn't install the .NET SDK automatically.
echo  The download page is opening: choose "SDK 8.0" for Windows x64, install it,
echo  then run this file again.
echo Manual SDK install needed >> "%LOG%"
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1

:close_dave
rem Dave has to be closed, or his files can't be replaced (e.g. when updating)
tasklist /fi "imagename eq Dave.exe" | find /i "Dave.exe" >nul && (echo  Closing Dave so he can be updated... & taskkill /im Dave.exe /f >nul & timeout /t 2 >nul)

rem 2. Build
echo  Building (the first time takes a minute)...
cd windows
dotnet build -c Release -nologo >> "%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo  Building failed. Details are in build-log.txt next to this file.
    echo  Send that file to whoever shared Dave with you.
    start "" notepad "%LOG%"
    pause
    exit /b 1
)
set "DAVE=%CD%\bin\Release\net8.0-windows10.0.19041.0\win-x64\Dave.exe"
if not exist "%DAVE%" (
    echo  Build said OK, but Dave.exe is missing. See build-log.txt.
    echo Dave.exe missing after build >> "%LOG%"
    pause
    exit /b 1
)

rem 3. Desktop shortcut
powershell -NoProfile -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Dave.lnk'); $s.TargetPath = '%DAVE%'; $s.WorkingDirectory = [IO.Path]::GetDirectoryName('%DAVE%'); $s.Description = 'Dave voice assistant'; $s.Save()"

rem 4. Start
echo.
echo  Done! Dave is starting. There's now a "Dave" shortcut on your desktop.
echo  Dave lives as a small icon next to the clock (bottom right).
echo  First time: enter your own free Groq key in the settings window (console.groq.com/keys).
echo  Then press Ctrl+Alt+D and talk.
echo Build OK >> "%LOG%"
start "" "%DAVE%"
pause
exit /b 0

:find_sdk
set "HAVE_SDK="
for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do if %%v GEQ 8 set "HAVE_SDK=1"
if defined HAVE_SDK (echo .NET SDK found >> "%LOG%") else (echo .NET SDK not found >> "%LOG%")
exit /b 0

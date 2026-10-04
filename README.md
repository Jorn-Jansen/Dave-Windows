# Dave Windows

Dave as a Windows program, a copy of the car version (the car version lives in `Desktop\Dave` and is untouched).
The `android/` and `backend/` folders here are just the copied car version, for reference.
The Windows program is in `windows/`.

## Install the easy way
Download **`DaveSetup.exe`** from the [Releases](../../releases) page and run it. It installs Dave for you
(no admin needed, nothing else to install), adds him to the Start menu and your desktop, and starts him.
In the settings window that opens, enter your own free Groq key (console.groq.com/keys) and click Save.
Windows may show "Windows protected your PC" because the installer isn't signed: click **More info** → **Run anyway**.
To update, run the newer `DaveSetup.exe`; to remove Dave, uninstall him from Settings → Apps.

### Making the installer (for whoever shares Dave)
Double-click **`Make Installer.bat`**. It builds Dave with .NET included and packs him into
`installer\Output\DaveSetup.exe` (about 90 MB). Raise `<Version>` in `windows\DaveWindows.csproj` for each new one.
Your keys are never in it: they live in `%APPDATA%\Dave Windows\settings.json`, not in the program.

**Automatic updates:** installed Daves check this repo's newest release every 6 hours and update themselves.
To ship an update: raise `<Version>`, run `Make Installer.bat`, then make a GitHub release with the tag
`v` + that version (e.g. `v1.1.0`) and attach `DaveSetup.exe` (keep that exact name).
This only works while the repo (or the one in `Updater.Repo` in `windows\Updater.cs`) is **public**.

## Install from the source (first time, or to update)
Double-click **`Build Dave.bat`**. It installs the .NET 8 SDK if it's missing (via winget),
builds Dave, puts a **Dave** shortcut on your desktop and starts him.
In the settings window that opens, enter your own free Groq key (console.groq.com/keys) and click Save.

To update after pulling new changes: close nothing, just run `Build Dave.bat` again.

## Start
Double-click **Dave** on the desktop. A Dave icon appears next to the clock.

- **Ctrl+Alt+D** (from any program, also games), or **"Hey Dave"** if turned on, or left-click the tray icon
- Speak after the beep; Dave stops recording when you stop talking
- Press the shortcut again (or click the bubble) to cancel
- Right-click the tray icon for **Settings** and **Quit**

## What it can do
Everything from the car version (questions with web search, memory, reminders, conversation mode,
Spotify, music quiz, DJ mode, Dutch + English), plus:
- Windows volume: "harder", "zachter", "zet het volume op 30 procent", "zet het geluid uit"
- Open programs: "open Roblox Studio", "open Discord"
- Websites: "open YouTube", "zoek naar pizza recepten"
- "vergrendel mijn pc"
- Other sound goes quieter while Dave talks

## Spotify
In Settings: paste the Client ID, and add `http://127.0.0.1:8765/callback` as an extra
Redirect URI in your app at developer.spotify.com/dashboard. Then click **Connect Spotify**.

## Build
`windows/`: `dotnet build -c Release` (needs the .NET 8 SDK).
Settings and log: `%APPDATA%\Dave Windows\` (`settings.json`, `dave.log`).

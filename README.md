# Dave Windows

Dave as a Windows program, a copy of the car version (the car version lives in `Desktop\Dave` and is untouched).
The `android/` and `backend/` folders here are just the copied car version, for reference.
The Windows program is in `windows/`.

## Start
Double-click **Dave Windows** on the desktop. A Dave icon appears next to the clock.

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

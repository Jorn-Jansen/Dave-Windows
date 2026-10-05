# Dave

A voice assistant for Windows. Talk to him with a shortcut or a wake word, in English or Dutch.

## Install
1. Download **`DaveSetup.exe`** from [Releases](../../releases/latest) and run it.
   If Windows says "Windows protected your PC", click **More info** → **Run anyway** (the installer isn't signed).
2. In the settings window that opens, paste your own free Groq key from [console.groq.com/keys](https://console.groq.com/keys) and click **Save**.

Dave updates himself when a new version is released. To remove him: Settings → Apps → Dave.

## Use
- Press **Ctrl+Alt+D** (works in games too), say **"Hey Dave"** (if turned on), or click the tray icon.
- Talk after the beep. Press the shortcut again or click the bubble to cancel.
- Right-click the tray icon for **Settings**, **Check for updates** and **Quit**.

## What he can do
- **Questions:** anything, with live web search; remembers your conversations for 30 days
- **Music (Spotify Premium):** play songs, artists, your playlists or liked songs, random songs, DJ mixes, a music quiz;
  "what's this song about?", "play something like this", "play X next", "I don't like this song" (never plays it again), shuffle, repeat, skip ahead
- **PC control:** volume, open and close programs, websites, lock the PC, find files, do things inside apps
- **Your screen:** "what's this error?", "read this out"
- **Typing and clipboard:** dictate into any window; read, translate or summarise what you copied
- **PC stats:** "how hot is my GPU?", "what's using my memory?"
- **Reminders and timers**, and **heads-ups:** "tell me when Roblox closes", "let me know when my download is done"
- **Windows:** "put this on my other screen", "Chrome and Discord side by side", "minimise everything"
- **Screenshots:** "screenshot this and copy it", "save a screenshot on my desktop"
- **Calendar:** "what do I have tomorrow?", "put football on Saturday at 2 in my calendar" (paste your calendar's iCal link in Settings)
- **Settings:** custom name and wake phrase, voices (Windows or Azure), language, AI provider (Groq, OpenAI, OpenRouter or your own)

### Spotify
In Settings, paste your app's Client ID from [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard),
add `http://127.0.0.1:8765/callback` as a Redirect URI in that app, then click **Connect Spotify**.

## For developers
- **Run from source:** double-click `Build Dave.bat` (installs the .NET 8 SDK if needed, builds, adds a desktop shortcut).
- **Release an update:**
  1. Raise `<Version>` in `windows/DaveWindows.csproj`.
  2. Run `Make Installer.bat` → `installer/Output/DaveSetup.exe`.
  3. Create a GitHub release tagged `v` + that version (e.g. `v1.1.1`) and attach `DaveSetup.exe` with that exact name.
  Installed copies pick it up within 6 hours. The repo must be public for this.
- **Settings, keys, logs:** `%APPDATA%\Dave Windows\`. Nothing personal is stored in the repo or the installer.

## Licence
[MIT](LICENSE). Uses [NAudio](https://github.com/naudio/NAudio) (MIT) and [Vosk](https://alphacephei.com/vosk/) with its small English model (Apache 2.0).

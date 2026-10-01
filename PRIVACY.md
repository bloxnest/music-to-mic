# Privacy

**Music to Mic collects nothing and sends nothing.**

- The app makes **no internet connections** of any kind. No accounts, analytics, telemetry, ads or update checks.
- It **never records or saves audio**. Sound only passes through it, from the app you pick and your microphone
  to the virtual cable, while routing is on.
- It only captures the app you choose (or, if you pick "All apps", everything except itself).
- Push-to-music only checks whether your key is held down. It never sees what you type and never blocks a key.
- It doesn't read, change or inject anything into Roblox's files, memory or process.

## What it stores on your PC

Only two things, in `%APPDATA%\MusicToMic`:

| File | What's in it |
| --- | --- |
| `settings.ini` | Your choices (default source, mic and output, mode, volumes, presets, hotkey, switches) |
| `log.txt` | Times and events: devices found, source chosen, routing on/off, devices unplugged, errors. Never audio. It never grows past about 256 KB (older entries move to `log.old.txt` and then are dropped). |

If you turn on **Start with Windows**, it also adds one entry named `Music to Mic` to your user's `Run` list in
the registry. Turning the switch off removes it.

## Removing everything

Right-click the icon in the hidden icons and choose **Exit**, then delete `MusicToMic.exe` and the
`%APPDATA%\MusicToMic` folder. Turn off **Start with Windows** first if you turned it on. VB-CABLE is a separate
driver by VB-Audio: run its setup as administrator and choose **Remove Driver** to uninstall it.

## The website

[bloxnest.github.io/music-to-mic](https://bloxnest.github.io/music-to-mic/) is a static site hosted by GitHub
Pages. It has no cookies, trackers or outside scripts. GitHub may keep standard server logs; see
[GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

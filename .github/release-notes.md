Built by GitHub Actions from the source code in this repository. Nobody uploaded this file by hand.

**SHA-256:** `{{SHA256}}`

### Check your download (optional)

In PowerShell, in the folder you downloaded it to:

```powershell
Get-FileHash .\MusicToMic.exe -Algorithm SHA256
```

The hash should match the one above. To confirm GitHub built it from this repository, with the [GitHub CLI](https://cli.github.com):

```
gh attestation verify MusicToMic.exe --repo {{REPO}}
```

### First start

Windows may say **"Windows protected your PC"** because the app isn't code-signed yet. Choose **More info**, then **Run anyway**.

### Setup

Music to Mic needs **VB-CABLE** (free, from [vb-audio.com/Cable](https://vb-audio.com/Cable/)). Step-by-step guide:
[bloxnest.github.io/music-to-mic/guide.html](https://bloxnest.github.io/music-to-mic/guide.html)

Made by xRed1. Not affiliated with or endorsed by Roblox Corporation or VB-Audio.

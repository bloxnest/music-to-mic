# Security

## Only download from here

The only official downloads are on this repository's
[Releases page](https://github.com/bloxnest/music-to-mic/releases), linked from
[bloxnest.github.io/music-to-mic](https://bloxnest.github.io/music-to-mic/). If someone sends you Music to Mic
from anywhere else, don't run it. Get VB-CABLE only from [vb-audio.com](https://vb-audio.com/Cable/).

## Check a download

Every release is built by [GitHub Actions](.github/workflows/release.yml) from the code in this repository and
published with a SHA-256 checksum and a signed build-provenance attestation.

```powershell
Get-FileHash .\MusicToMic.exe -Algorithm SHA256                    # must match the release page
gh attestation verify MusicToMic.exe --repo bloxnest/music-to-mic  # needs the GitHub CLI
```

## What the app can and can't do

- It uses the standard Windows audio API (WASAPI) to capture the app you pick (process loopback) and your
  microphone, and plays the mix into a virtual cable such as VB-CABLE.
- It never opens a network connection, never needs administrator rights, never records audio to disk, and
  never reads or writes Roblox's files, memory or process.

## Reporting a problem

If you find a security problem, please report it privately through GitHub's **Report a vulnerability** button
on the [Security tab](https://github.com/bloxnest/music-to-mic/security) instead of opening a public issue.
Please include the version (shown in Settings) and what you found.

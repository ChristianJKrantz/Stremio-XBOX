<div align="center">

# Stremio-XBOX

### Stremio on Xbox, the way your TV and soundbar were meant to play it.

**4K · HDR10 · Dolby Vision · Dolby Atmos · 100+ GB remuxes without a stutter**

[![Xbox Series X|S](https://img.shields.io/badge/Xbox-Series%20X%7CS-107C10?style=for-the-badge&logo=xbox&logoColor=white)](#-install)
[![Dev Mode](https://img.shields.io/badge/Dev%20Mode-UWP-5C2D91?style=for-the-badge)](#-install)
[![Dolby Vision](https://img.shields.io/badge/Dolby%20Vision-P5-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![Dolby Atmos](https://img.shields.io/badge/Dolby-Atmos-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![License](https://img.shields.io/badge/License-GPL--2.0-blue?style=for-the-badge)](LICENSE)

[Features](#-features) · [Controls](#-controls) · [Install](#-install) · [Limitations](#%EF%B8%8F-known-limitations)

**No streaming server. No PC. Just the XBOX.**

</div>

---

Stremio Web in a controller-first Xbox app. Direct streams (debrid links like TorBox via AIOStreams)
skip the browser player and go straight to the Xbox's own media pipeline: real HDR, real Atmos, and
huge files that just play.

> Unofficial. Not affiliated with or endorsed by Stremio.

<div align="center">

## ✨ Features

</div>

<table>
<tr>
<td width="50%" valign="top">

**🎬 Native player**<br>
Hardware decoding through the Xbox's own media pipeline. 4K at the file's real frame rate.

</td>
<td width="50%" valign="top">

**🌈 HDR10 & Dolby Vision (Profile 5)**<br>
The TV switches into the right mode per file, automatically. DoVi 7/8 play as HDR10.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**🔊 Dolby Atmos, DD+, AC3**<br>
Passed through untouched to your soundbar or AVR.

</td>
<td width="50%" valign="top">

**🚀 Custom read buffer**<br>
6 parallel connections and up to 768 MB of RAM read-ahead that follows the playhead. Built for remuxes that make other players choke.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**💬 Subtitles & audio tracks**<br>
Embedded and addon subtitles. Switch language or track mid-play.

</td>
<td width="50%" valign="top">

**🎮 Made for the controller**<br>
Full D-pad navigation, B to go back, a built-in cursor for the hard-to-reach bits, and the Xbox keyboard for sign-in and search.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**⏯️ Continue Watching & resume**<br>
The native player reports to Stremio like its own player: picks up where you left off, updates Continue Watching and watched marks, and syncs them to your Stremio account.

</td>
<td width="50%" valign="top">

**⏭️ Next episode**<br>
An Up Next card near the end of an episode. With Stremio's binge watching on, the next episode starts by itself on the same addon's stream.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**📺 Feels like an app**<br>
Show and episode titles over the title's backdrop, a loading screen the moment you pick a stream, audio and subtitle menus, the best audio track in English (or the original language with English subtitles for foreign titles), subtitles in your Stremio language, and the Xbox media remote. Browsing glides: the page eases along with the highlight and keeps the current row at the same height, and the highlighted poster grows slightly. The player fades its bar, Skip Intro / Up Next cards and Audio & Subtitles overlay in and out.

</td>
<td width="50%" valign="top">

**🛟 Recovers by itself**<br>
A friendly screen with retry when the Xbox is offline, an automatic reload if the page crashes, and a message (not a debug log) when a stream won't play.

</td>
</tr>
</table>

**Formats:** MKV, MP4 · HEVC, H.264 · E-AC3 (Atmos), AC3, AAC and more.

**🔥 How the read buffer works**

Windows' media player reads a network stream one request at a time, and on large MKVs it keeps
jumping to a position far ahead of playback that the download can't keep up with. Every wait
freezes the picture. Stremio-XBOX puts its own cache in between:

- 6 parallel HTTP range connections in 4 MB chunks
- up to 768 MB of read-ahead in RAM, anchored on where playback actually is
- a quick seek right after playback starts that stops the player's far-ahead reads, which was the
  real cause of stutter on 80+ GB files


## ⚡ Why not just use Edge?

| | Edge / web player | **Stremio-XBOX** |
| --- | :---: | :---: |
| MKV · HEVC (x265) | ❌ "Stream is not supported" | ✅ Plays natively |
| 4K HDR10 · Dolby Vision | ❌ No mode switching | ✅ Auto resolution, refresh rate, HDR |
| Dolby Atmos · DD+ | ❌ Downmixed or silent | ✅ Bitstream passthrough |
| 100+ GB remuxes | ❌ Buffering, stutter | ✅ Smooth |
| Controller | ❌ Mouse-mode cursor | ✅ D-pad, back, cursor, keyboard |

## ⚠️ Known limitations

- **Dolby Vision Profile 7 / 8 play as HDR10.** The Xbox's decoder only outputs DV for Profile 5, and
  turning 7/8 into 5 would mean re-encoding the video, so an app can't change this. The player says so
  on screen when a DV release plays as HDR10. Pick a Profile 5 (often WEB-DL) stream for real DV.
- **Dolby TrueHD can be silent on Xbox.** When a file also has another track in the same language, the
  player starts on that one instead (Y still offers TrueHD). When TrueHD is the only track, it tells you,
  so you can pick another stream if there's no sound.
- **Which audio track plays first.** English when the file has it. A foreign title (judged by its main
  country of production on Cinemeta, such as Japan or South Korea) plays in its original language with
  English subtitles when the file has that language, so you get the sub, not the dub. Within the language
  the best track wins: Atmos, then more channels, then Dolby Digital Plus, DTS, Dolby Digital, AAC.
  Commentary and audio description are never picked first. A co-production listed under the wrong
  country can pick the other language; Y and X change it.
- **Subtitles inside the video file can stutter 4K playback.** With one on, the Xbox player reads ahead
  through the file for the next line; on a track with lines only for some scenes it gets so far ahead that
  the picture can't keep up. So the player turns on a downloaded subtitle (from a subtitle addon) first, and
  one inside the file only when no downloaded one in that language arrives. If a video stutters with
  subtitles on, pick a downloaded one with X.
- Built for direct / debrid links. Torrent streams need a streaming server.

## 🎮 Controls

<table>
<tr>
<td valign="top">

**Browsing**

| Button | Action |
| --- | --- |
| D-pad / Left stick | Move |
| A | Select |
| B | Back |
| Menu (☰) on a poster | Options: play / resume, play from beginning (when there is saved progress), details, add to or remove from Library, mark as watched, remove from Continue Watching |
| Left stick click | Cursor mode |
| View | Debug log |

<sub>Cursor mode: stick moves, A clicks, right stick scrolls.</sub>

</td>
<td valign="top">

**Player**

| Button | Action |
| --- | --- |
| A / Menu | Play / Pause |
| ◀ / ▶ | −10 s / +10 s (hold to go faster) |
| LT / RT | Rewind / fast-forward (hold to go faster) |
| LB / RB | Jump −10 s / +10 s |
| Y / X | Audio & Subtitles (opens on audio / on subtitles) |
| ▲ / ▼ | Show the progress bar |
| A on Skip intro | Skip the intro |
| A on Up Next | Play the next episode |
| B | Close panel / hide card / stop |

<sub>Same layout as the big console streaming apps. D-pad and trigger presses add up and apply once you stop, so one jump instead of many downloads. In the Audio & Subtitles panel ◀ ▶ switch columns.</sub>

<sub>Testing on a Windows PC without a controller: arrows move, Enter selects (play/pause in the player), Esc or Backspace goes back, Home/End = LT/RT, PageUp/PageDown = LB/RB, Y and X open Audio & Subtitles, F1 shows the debug log, F4 plays a public sample video in the native player (with a pretend intro at 0:05–0:40) to try the player without a streaming server.</sub>

</td>
</tr>
</table>

## 📦 Install

1. Put your Xbox in Dev Mode and learn the Device Portal install:
   **[How to activate Dev Mode and install UWP apps](https://www.reddit.com/r/XboxRetailHomebrew/comments/1ktgw97/guide_how_to_activate_dev_mode_and_install_uwp/)**
2. Download the latest zip from **[Releases](../../releases)**.
3. Device Portal → **Add** → pick the `.msix`, add every file from `Dependencies`, install.
4. Launch **Stremio-XBOX**, sign in, add your addons as usual.

<details>
<summary><b>Build it yourself (Windows)</b></summary>

<br>

Visual Studio 2022 with the UWP workload and Windows SDK 10.0.22621.

1. `powershell -ExecutionPolicy Bypass -File scripts\New-DevCert.ps1` (once, creates the signing cert)
2. Open `StremioXbox.sln`, **Release | x64**.
3. Deploy to the Xbox as a Remote Machine, or Publish → Create App Packages → Sideloading and install
   through Device Portal.

Or from a terminal, once this PC trusts the console's Device Portal certificate (download
`https://<xbox>:11443/config/rootcertificate`, then `certutil -user -addstore Root <file>`):

- `powershell -ExecutionPolicy Bypass -File scripts\Deploy-Xbox.ps1 -Xbox <ip>` installs the newest built
  package with its dependencies and starts it (asks for the Device Portal login if one is set).
- `powershell -ExecutionPolicy Bypass -File scripts\Get-XboxDiagnostics.ps1 -Xbox <ip>` saves a screenshot of the
  console and the app's log into `diagnostics\`, to capture a problem while it is on screen.

Two checks run without Visual Studio or an Xbox:

- `node tests/shell-session.test.js` (Node 18+): the shell script's Stremio session logic.
- `dotnet run -c Release --project tests/SeekBenchmark` (Windows, .NET SDK): the read buffer against a
  simulated debrid server, timing seeks and checking every byte. Set `MBPS` and `COLD_MS` to change the
  server's speed and first-byte delay.

</details>

## Credits

- [Jellyfin for Xbox](https://github.com/jellyfin/jellyfin-xbox): display mode switching and the
  WebView2 hosting approach.
- [Stremio](https://www.stremio.com): the app this wraps.

<div align="center">
<sub>GPL-2.0 · see <a href="LICENSE">LICENSE</a> and <a href="NOTICE.md">NOTICE.md</a></sub>
</div>

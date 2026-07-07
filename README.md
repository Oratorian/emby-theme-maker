# Emby Theme Maker

An Emby Server plugin that turns Emby's **Intro Detection** markers into per-series **theme
videos** (`backdrops/theme.mp4`) and **theme music** (`theme.mp3`) — using the server's own
built-in ffmpeg. No external tools, no API key.

It's the native-plugin evolution of an earlier command-line script: same idea, but it runs
inside Emby and is driven from **Scheduled Tasks**.

## What it does

For each TV series, the plugin:

1. Reads the intro markers (`IntroStart` / `IntroEnd`) straight from Emby's item database.
2. Picks a representative episode (median intro length — robust against cold opens / specials).
3. Cuts `[IntroStart → IntroEnd]` from that episode with Emby's bundled ffmpeg, re-encodes with
   fade in/out, and writes the result into the series folder.
4. Triggers a library scan so Emby registers the new theme media.

Output depends on the **Mode** setting:

| Mode | Writes |
|---|---|
| `Video` | `<SeriesFolder>/backdrops/theme.mp4` (h264/aac video backdrop) |
| `Audio` | `<SeriesFolder>/theme.mp3` (theme music, series root) |
| `Both` | both of the above, in one pass |

## Requirements

- Emby Server **4.9.x** (built/tested against 4.9.5.0, Windows & Linux).
- Intro Detection already run on your shows (that's where the markers come from).
- A **bare-metal / VM** install where Emby can write into your series folders. (In container/NAS
  setups where the series folders aren't writable by the server, it can't drop the theme files.)

No API key, no external ffmpeg — the plugin uses Emby's own item database and ffmpeg.

## Install

1. Download `EmbyThemeMaker.dll` from the [latest release](../../releases/latest) (or build it —
   see below).
2. Drop it into your Emby **plugins** folder and restart the server:
   - Windows: `…\Emby-Server\programdata\plugins\`
   - Linux (deb/rpm): `/var/lib/emby/plugins/`
3. Open **Dashboard → Theme Maker** and configure it.

## Usage

Everything runs from **Dashboard → Scheduled Tasks**:

- **Theme Maker: Preview (read-only)** — logs exactly what it *would* generate for each series
  (source episode, intro span, whether a theme already exists) without writing anything. Run this
  first.
- **Theme Maker: Generate** — actually encodes the theme files, per your settings.

Add a trigger to either (daily / interval / on startup) to run it on a schedule, or run on demand.
All activity is logged to the Emby log with a `[ThemeMaker]` prefix.

## Settings (Dashboard → Theme Maker)

| Setting | Meaning |
|---|---|
| **Output mode** | video / audio / both |
| **Overwrite existing themes** | Regenerate even when a theme already exists (off = skip existing) |
| **Only under this folder** | Restrict to series whose folder is under this path (blank = all) |
| **Limit** | Process at most N series per run (0 = no limit) |
| **Source episode** | Median (robust) or First episode |
| **Min / Max intro length** | Ignore intros outside this range (seconds) |
| **Pad start / end** | Extend the cut before/after the markers (seconds) |
| **Preferred audio language** | e.g. `jpn` — picks that stream if present (blank = first) |
| **Max video height / CRF / Preset / Peak bitrate** | libx264 quality controls |
| **Audio bitrate / Fade in / Fade out** | audio + fade controls |
| **Output filenames / backdrop subfolder** | where and what to write |
| **Parallel encodes** | how many ffmpeg jobs at once |
| **Scan library after generating** | trigger one Emby scan at the end so new themes register |

Safe by default: it never overwrites an existing theme unless **Overwrite** is on, and it never
deletes a song out of a curated `theme-music/` folder.

## Building

Requires the .NET SDK. Targets `netstandard2.0`, references `mediabrowser.server.core` (Emby's
NuGet); the Emby assemblies are provided by the host and are not bundled — the output is a single
self-contained `EmbyThemeMaker.dll`.

```bash
dotnet publish EmbyThemeMaker/EmbyThemeMaker.csproj -c Release -o out
# -> out/EmbyThemeMaker.dll
```

To stamp a version, pass it in (CI does this from the git tag):

```bash
dotnet publish EmbyThemeMaker/EmbyThemeMaker.csproj -c Release -o out -p:Version=1.2.3
```

## Releases

Pushing a tag like `v1.2.3` triggers the GitHub Actions workflow, which builds the DLL, stamps it
with the tag version, publishes a GitHub Release with `EmbyThemeMaker.dll` attached, and generates
a **build provenance attestation** for the artifact (verifiable with
`gh attestation verify EmbyThemeMaker.dll --repo <owner>/<repo>`).

## License

**GPL-3.0** — see [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).

You may use, modify, and redistribute it under the GPL-3.0. As the sole copyright holder, the
author also reserves the right to offer it under separate (e.g. commercial) terms — see the
dual-licensing note in [COPYRIGHT](COPYRIGHT).

# WMPL Wrap

**A local listening-history companion for Windows Media Player Legacy**

**Current version:** `v1.2.2` · [Releases](https://github.com/zaynedoc/WMPL-Wrap/releases) · [Privacy](PRIVACY.md) · [Terms](TERMS.md) · [MIT License](LICENSE)

WMPL Wrap turns Windows Media Player's cumulative play counts into a personal
listening history. It captures local snapshots, compares them over time, and presents
your top songs, albums, artists, and latest listening changes in a native Windows app.

## What it does

- Captures read-only snapshots of the local Windows Media Player Legacy library
- Calculates listens from play-count increases between snapshots
- Matches recurring tracks by local source URL, so tag edits keep their history
- Shows overview, snapshot history, top-song, top-album, and top-artist views
- Can schedule one daily local snapshot through Windows Task Scheduler
- Lets users manually check GitHub Releases for a newer signed version
- Includes optional Discord Rich Presence for the currently playing WMP track

## How the history works

Windows Media Player does not keep a dated play history; it exposes a cumulative
`UserPlayCount` for each library item. Wrap calculates later listens from the
change across consecutive snapshots:

```text
listens in a period = max(0, end.UserPlayCount - start.UserPlayCount)
```

By default, Wrap also includes a track's current WMP count when it first sees that
track in a snapshot. This keeps newly added albums visible in the current report;
the UI labels those values as **first-seen WMP counts**. Turn off **Include first-seen
track counts in listening totals** in Settings for strict, delta-only reporting.

## Data recording

Snapshots are JSON files in `data/snapshots` (or the directory provided with `--data`).
Each one stores its capture time, the WMP total count, and the metadata needed to label
results:

- Source URL
- Title
- Artist
- Album
- Duration

The `data` directory is ignored by Git. Artwork is read locally, from WMP artwork
references, and is never looked up online. See [PRIVACY.md](PRIVACY.md) for the full local-data notice.

## Run the desktop app

### Development:

Requires the .NET 10 SDK and Windows Media Player Legacy. In project dir, verify the build and run with:

```powershell
dotnet run --project src/WmplWrap.Desktop
```

### Standalone build:

Create a self-contained, single-file desktop build:

```powershell
.\scripts\publish-desktop.ps1
```

The result is `publish\desktop\WmplWrap.Desktop.exe`. It does not need VS Code,
PowerShell, or a separately installed .NET runtime. An unsigned build is useful for
local packaging checks; however Windows security features can block it from launching.

### Signed public release

The public release workflow publishes, signs, timestamps, and verifies the final EXE in
one command. The signing certificate's private key stays in Azure; it is never stored in
this repository.

```powershell
winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
.\scripts\publish-desktop.ps1 -Sign
```

The first signed release can open a browser for Azure authentication. The default profile
is `wmplwrapdesktop` / `wmplwrapdesktop-public` in East US.

Fork maintainers can use their own Azure Artifact Signing account and profile:

```powershell
.\scripts\publish-desktop.ps1 -Sign `
  -CodeSigningAccountName "myappsigning" `
  -CertificateProfileName "myapp-public" `
  -Endpoint "https://cus.codesigning.azure.net"
```

## Snapshot logger commands

The command-line logger requires the .NET 10 SDK or runtime and Windows Media Player's
legacy COM library. Run these from the repository root:

```powershell
dotnet run --project src/WmplWrap -- snapshot
dotnet run --project src/WmplWrap -- status
dotnet run --project src/WmplWrap -- report --from 2026-09-01 --to 2026-09-30 --top 20
```

`snapshot` is safe to run more than once per day. `status` reports the first and latest
local snapshot. `--from` and `--to` accept Eastern calendar dates (`yyyy-MM-dd`) or
ISO-8601 timestamps; ranges are inclusive and the report prints the snapshots it used.

### Automatic daily snapshots:

First publish the command-line logger. This replaces the local publish output; it does
not create duplicate scheduled tasks.

```powershell
dotnet publish src/WmplWrap -c Release -o publish
.\scripts\setup-scheduled-snapshot.ps1 -PublishDirectory .\publish
```

The setup script creates one local task, **WMPL Wrap Daily Snapshot**, scheduled for
12:05 AM. `StartWhenAvailable` lets Windows catch up after the next sign-in or startup
if the PC was off at midnight.

To stop automatic snapshots without deleting recorded history:

```powershell
Unregister-ScheduledTask -TaskName "WMPL Wrap Daily Snapshot" -Confirm:$false
```

## Discord Rich Presence

- **Default:** Off. Enable it in **Settings** to share the active WMP track through the
  locally running Discord desktop client.
- **Presence:** Shows the title, by artist, playback state, and the selected Discord
  artwork key. Snapshot history and media files are never sent to WMPL Wrap or Discord.
- **Elapsed time:** Starts from WMP's current position, recalibrates after a seek, and is
  hidden while paused.
- **Google This Song:** The active presence includes a Discord button that opens a Google
  search for the displayed track and artist.
- **Track changes:** Keeps the last activity for up to five seconds during WMP's brief
  no-track handoff. Disable that option in Settings to clear Discord immediately.
- **Default application:** `1553580075688009838`, named **WMPL Wrap Rich Presence**, with
  `wmp_icon` and `wmp_empty` uploaded as Rich Presence assets.
- **Album art:** Use **Settings → Discord Rich Presence → Album art** to map an exact
  album artist and album title to an uploaded Discord asset key. The preview is local;
  Discord resolves whether an uploaded asset exists.
- **Custom application ID:** Upload `wmp_icon`, `wmp_empty`, and each configured album-art
  key to that Discord application. Its Developer Portal name is what Discord displays.

### RPC logic credit:

The rich presence logic and user experience were referenced from [Windows Media Player Discord RPC](https://github.com/T0biasCZe/Windows-Media-Player-Discord-RPC) by [@T0biasCZe](https://github.com/T0biasCZe). As per MPL-2.0 licensing, WMPL Wrap contains an independent implementation, and does not include any source code from T0biasCZe's project.

## Project notes

- Cumulative counts from before the first snapshot cannot be timestamped retroactively.
- More snapshots create a more useful history; daily collection is a good default.
- Windows Media Player documents `UserPlayCount` (also called `PlayCount`) as a
  library-only value. WMPL Wrap uses its `getAll()` API to enumerate items. See
  [UserPlayCount](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/userplaycount-attribute)
  and [getAll](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/wmplibiwmpmediacollection-iwmpmediacollection-getall--vb-and-c).

# Macstream

See and control your MacBook's own screen from a Windows PC over one Ethernet cable, at
the Mac panel's native resolution and with very low latency.

- **Video Mac → PC:** the Mac encodes its screen with VideoToolbox (H.264); the PC decodes
  it on the GPU (FFmpeg D3D11VA) and shows it in a native swap chain. Measured on an M2
  MacBook Air and an RTX 5060 Ti: 2560×1664 at ~60 fps, ~1 ms network round trip,
  ~16 ms from capture to encoded frame.
- **Mouse and keyboard PC → Mac:** move, click, drag, scroll, every key by position, with
  Mac-style modifiers (Ctrl or Windows key = Command, Alt = Option).
- **Clipboard sync (optional):** copy text on one computer, paste it on the other. Off
  until you switch it on in the Windows app.
- **Private:** the two machines pair once with a 6-digit code; after that every byte is
  encrypted (P-256 + AES-256-GCM). The Mac only listens on the wired link, never Wi-Fi.
  No cloud, no accounts, no telemetry.

Macstream is a fork of [grasscaograss/USB4-WiredMonitor](https://github.com/grasscaograss/USB4-WiredMonitor),
which streamed the other way (a Mac using a Windows screen as an extra display).

## What you need

- A Mac with macOS 13 or later and Xcode (or the Command Line Tools). Tested on an M2 MacBook Air.
- A Windows 10/11 PC with a GPU that can decode H.264 through D3D11VA (any recent NVIDIA,
  AMD or Intel GPU).
- A wired link between them. Tested: a USB-C Ethernet adapter on the Mac, cabled straight
  to the PC's Ethernet port. No router or switch is needed: both machines give themselves a
  `169.254.x.x` address. (A Thunderbolt/USB4 network link works too, if both machines have one.)

## Set up the PC (once)

Everything stays inside the project folder: no admin rights, no global installs, no PATH
or registry changes. In PowerShell, in the project folder:

```powershell
.\tools\setup-dotnet.ps1    # pinned .NET 10 SDK into .local\dotnet (~290 MB download)
.\tools\setup-ffmpeg.ps1    # pinned FFmpeg 8.1 LGPL build into third_party\ffmpeg (~76 MB)
```

Both scripts check the download's published checksum.

## Set up the Mac (once)

In Terminal, in the project folder:

```bash
./tools/mac/build-server.sh
```

This builds `build/mac/Macstream.app` (it shows up in Launchpad as **Macstream**) and signs it with a local certificate kept in
the project, so macOS remembers its permissions across rebuilds.

## Use it

1. **Mac:** click **Macstream** in Launchpad (or Spotlight, or drag it from Launchpad to
   the Dock). The first time, allow **Macstream** in System Settings → Privacy & Security →
   **Screen & System Audio Recording** and **Accessibility**, then start it again. A display
   icon appears in the menu bar; **Start at Login** is in its menu (off unless you tick it).
   It logs to `logs/mac-server.log`. To watch the log in Terminal instead, start it with
   `./tools/mac/run-server.sh`. If Launchpad doesn't show it, run
   `./tools/mac/add-to-launchpad.sh` once.
2. **PC:** double-click **`Macstream.cmd`**. It rebuilds the viewer when needed and opens
   it. The viewer finds the Mac on the cable by itself; click **Connect**.
3. **First connection only:** the PC and the Mac both show a 6-digit code. If the codes
   match, click **Yes** on the PC and **Allow** on the Mac.

### Controls in the viewer

| Keys | What they do |
|---|---|
| Ctrl or Windows key | Command on the Mac (Ctrl+C copies, Win+Space opens Spotlight) |
| Alt / Right Ctrl | Option / Control on the Mac |
| Alt+Tab | Switch Windows apps (Windows' own switcher) |
| Win+Tab | Switch Mac apps (Command+Tab) |
| Ctrl+Alt+F12 | Mac control on/off (mouse and keyboard stay on Windows when off) |
| Ctrl+Alt+F11 | Full screen on/off (Esc goes to the Mac while it has the keyboard) |
| Ctrl+Alt+Del | Always stays on Windows |

The keyboard only goes to the Mac while the viewer window has focus, and nothing stays
stuck: held keys and buttons are released when the window loses focus, control is switched
off, or the connection drops (the Mac also releases them on its own after 1 s of silence).

### Clipboard sync

Click **Clipboard: Off** in the viewer's toolbar (or tick *Clipboard sync* in the tray menu,
or in **Settings**) to switch it on; the choice is remembered. While it is on and connected, text you copy on
either computer can be pasted on the other: text and pictures (screenshots, copied images).

- Only copies made while it is on cross over. Nothing that was already on the clipboard is
  sent, and neither side keeps a history: each copy just replaces the last one.
- Copies that password managers mark as private are not sent.
- Up to 512 KB of text or a 12 MB picture per copy. If a copy has both (e.g. from Word), the
  text goes across. Copied files don't (yet).
- The Mac's menu shows whether it is on. To forbid it on the Mac entirely, start the server
  with `MACSTREAM_CLIPBOARD=0`.

### Tray icon and menu bar

- **PC, notification area:** status (green dot = connected), Show, Connect/Disconnect, Mac
  control, Clipboard sync, Full screen, Settings, Open log folder, and **Quit everything** (releases any keys held on
  the Mac and closes every Macstream program on the PC).
- **Mac, menu bar:** status and what the PC is seeing (this Mac's screen or an extra display), a tick to pause the PC's mouse and keyboard (video keeps
  running), whether clipboard sync is on, the paired PCs and this Mac's key with
  **Forget Paired PCs…**, **Start at Login**, Settings…, Open Log, and **Quit Macstream**
  (Ctrl+C in the Terminal window does the same).
- **Mac, Dock:** while Macstream runs it is in the Dock. Click it (or open Macstream again
  from Launchpad) for its settings window, which shows the same things as the menu bar plus
  *Show Macstream in the Dock*. Right-click it, or Cmd+Q, to quit.

### Settings window (PC)

**Settings** in the viewer's toolbar or the tray menu. Changes apply at once and are
remembered (`.local\settings\viewer.json`):

- **Mac screen**: show the Mac's own screen (mirror, the default), or **an extra Mac
  display for this PC**: a second Mac desktop at your monitor's size (or a size you pick),
  pixel for pixel. Drag Mac windows onto it, or close the lid (with Amphetamine) to use it on
  its own. *Text size*: Normal is sharpest with the most space; Larger is Retina (everything
  twice the size). Changing it reconnects; if the Mac can't make the display it shows its
  own screen instead. The Mac's menu bar says which one the PC is seeing. When the PC
  disconnects, the Mac removes the extra display after 5 seconds (a quick reconnect keeps
  it, with its windows), so nothing is left on a screen nobody can see.
- **MacBook screen** (extra display only): where your MacBook sits next to this monitor
  (left, right, above, below) and **which part** of that edge leads to it. Push the mouse
  against that edge of the picture area (full screen, maximized or a window) and it carries
  on onto the MacBook's own screen (you look at the MacBook, the PC's mouse and keyboard work
  there, dragging windows too); push it back to return. Use part of the edge to keep the rest
  for Windows, e.g. *Lower half* for the Mac when another PC monitor is beside the upper half.
  *Automatic* picks the part with no PC monitor beyond it when the viewer fills the monitor
  (so it also does the right thing on another monitor), otherwise the whole edge. Alt+Tab or
  Ctrl+Alt+F12 also gives the mouse back to Windows.
- Clipboard sync on/off.
- Scrolling: reverse the direction, and how far one wheel notch scrolls on the Mac.
- Keys: which Mac key Ctrl, Right Ctrl, Alt and the Windows key become (Command, Option,
  Control, Shift or nothing), with *Restore default keys and scrolling*.
- Pairing: shows the paired Mac's key (the Mac's menu bar shows the same) and
  **Forget this Mac…**, after which the next connection asks for the 6-digit code again.

## Advanced settings

Optional, as environment variables. Choices made in the PC's Settings window win over the
matching viewer variables. On the Mac, put them in front of the run command,
e.g. `MACSTREAM_CODEC=hevc ./tools/mac/run-server.sh`. On the PC, set them in
PowerShell before starting the viewer, e.g. `$env:MACSTREAM_SCROLL_INVERT=1; .\Macstream.cmd`. The original project's
`WIRED_MONITOR_*` names still work too.

**Mac server**

| Variable | Default | Meaning |
|---|---|---|
| `MACSTREAM_INPUT` | on (wired only) | `0` refuses the PC's mouse and keyboard entirely |
| `MACSTREAM_KEYBOARD` | on | `0` accepts the mouse but not the keyboard |
| `MACSTREAM_INTERFACE` | `wired` | Where the server listens: `wired`, `other` (e.g. Thunderbolt Bridge), `any` (includes Wi-Fi; PC control is then off unless `MACSTREAM_INPUT=1`) |
| `MACSTREAM_CODEC` | `h264` | `hevc` to try HEVC |
| `MACSTREAM_VIRTUAL_DISPLAY` | the PC's choice | `1` always an extra display, `0` always the Mac's own screen (Settings > Mac screen on the PC otherwise decides) |
| `MACSTREAM_BITRATE` | automatic | Bits per second |
| `MACSTREAM_MAX_QP` | `24` | Lower = sharper text, more data |
| `MACSTREAM_FPS` | PC refresh, max 60 | Frames per second (up to 120) |
| `MACSTREAM_CAPTURE_SIZE` | `panel` | `backing` captures the scaled backing store instead of the panel's pixels |
| `MACSTREAM_MENU_BAR` | on | `0` hides the menu-bar item |
| `MACSTREAM_CLIPBOARD` | allowed | `0` refuses clipboard sync whatever the PC asks |

**Windows viewer**

| Variable | Default | Meaning |
|---|---|---|
| `MACSTREAM_MAC_MODIFIERS` | `ctrl=cmd,alt=option,win=cmd` | Remap modifiers; targets `cmd`, `option`, `control`, `shift`, `none` |
| `MACSTREAM_ALT_TAB` | Windows | `mac` sends Alt+Tab to the Mac instead |
| `MACSTREAM_SCROLL_INVERT` | off | `1` flips the scroll direction |
| `MACSTREAM_SCROLL_PX` | `50` | Pixels per wheel notch |
| `MACSTREAM_SHOW_LOCAL_CURSOR` | off | `1` keeps the Windows pointer visible over the picture |
| `MACSTREAM_HWDEC` | automatic | Force `d3d11va` or `dxva2` |

More low-level switches (capture mode, renderer, encoder tuning) are in the source:
search for `MACSTREAM_`.

## Troubleshooting

- **Logs:** PC `Windows\MacstreamViewer\bin\Debug\net10.0-windows\diag.log` (one STATS
  line per second while streaming; also "Open log folder" in the tray). Mac `logs/mac-server.log`
  (also "Open Log" in the menu bar).
- **"Mac: not found on the cable":** check that the server is running on the Mac and the
  cable is in; both sides should have a `169.254.x.x` address on that adapter.
  `. .\tools\env.ps1; dotnet run --project NetworkTest -- discovery` lists the PC's network adapters.
- **Black picture or decoder error:** run `.\tools\setup-ffmpeg.ps1` again, then reopen
  `Macstream.cmd`.
- **Mouse or keyboard does nothing on the Mac:** allow Macstream under Accessibility,
  restart the server, and check that the menu-bar tick is on.
- **Using the Mac with the lid closed:** see [docs/closed-lid.md](docs/closed-lid.md).
- **Pair again from scratch:** **Forget Paired PCs…** in the Mac's menu bar (or
  `./tools/mac/unpair.sh`) and **Forget this Mac…** in the PC's Settings.

## Uninstall

- **PC:** `.\tools\uninstall-windows.ps1` removes the SDK, FFmpeg, builds, logs, settings
  and pairing key, plus Windows' own tray-icon entry and taskbar pin for Macstream
  (`-DryRun` to preview, `-All` to delete the whole folder).
- **Mac:** `./tools/mac/uninstall.sh` removes Start at Login and the Launchpad app, stops
  the server, revokes its permissions, deletes its Keychain pairing keys and the small files
  macOS keeps for it, and removes builds, caches and logs (`--dry-run`, `--all`). It only
  touches items with Macstream's own names.
- Rebuilding never piles up versions: each build replaces the previous app/exe in place.
  To free space on the Mac without uninstalling, `./tools/mac/tidy.sh` removes leftovers from
  older builds and old logs (`--deep` also the build cache, `--dry-run` to preview).

## For developers

```text
Mac/MacstreamServer/       Swift server: capture, encode, pairing, input, menu bar
Windows/MacstreamViewer/   C#/.NET 10 WPF viewer: receive, decode, render, input, tray
NetworkTest/                  Link throughput and latency tool
Protocol/PROTOCOL.md          Wire format and security handshake
docs/security-audit.md        Audit of the original code and what changed
tools/                        Pinned setup, build, run and uninstall scripts
```

PC build: `. .\tools\env.ps1`, then
`dotnet build Windows\MacstreamViewer\MacstreamViewer.csproj -c Debug`
(close the viewer first). Mac build: `./tools/mac/build-server.sh`.

## License

MIT (see [LICENSE](LICENSE)), as in the original project. FFmpeg is not stored in this
repository; `tools/setup-ffmpeg.ps1` downloads a pinned LGPL build, which keeps its own
license.

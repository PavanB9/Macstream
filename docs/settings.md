# Macstream features and settings

Everything you can switch and tune. For setup, see the [README](../README.md).

## Clipboard sync

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

## Tray icon and menu bar

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

## Settings window (PC)

**Settings** in the viewer's toolbar or the tray menu. Changes apply at once and are
remembered (`.local\settings\viewer.json`):

- **Mac screen**: show the Mac's own screen (mirror, the default), or **an extra Mac
  display for this PC**: a second Mac desktop at your monitor's size (or a size you pick),
  pixel for pixel. Drag Mac windows onto it, or close the lid (with Amphetamine) to use it on
  its own. *Text size*: Normal is sharpest with the most space; Larger is Retina (everything
  twice the size). *Frame rate*: 60 fps by default; *Up to 90* or *Up to 120 fps* for
  a monitor that runs that fast. It uses more of the Mac's power and battery, and how high
  it really gets depends on the Mac (its video encoder sets the limit: an M2 MacBook Air
  reached about 87 fps in testing). The mirror stays at 60. Changing any of these reconnects; if the Mac can't
  make the display it shows its
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

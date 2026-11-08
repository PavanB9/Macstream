# Macstream

**Use your MacBook from your Windows PC over one Ethernet cable.** The Mac's screen shows
up in a window on the PC, sharp (native resolution) and smooth (~60 fps, a few ms of delay),
and your PC's mouse and keyboard control the Mac.

- **Sharp and fast:** the Mac's real pixels, hardware-encoded on the Mac, hardware-decoded
  on the PC.
- **Full control:** mouse, scrolling, every key, with Mac shortcuts on PC keys
  (Ctrl or Windows key = Command, Alt = Option).
- **Extra display:** optionally give the PC its own Mac desktop at your monitor's size, and
  move the mouse straight across onto the MacBook's own screen.
- **Shared clipboard** (optional): copy text or pictures on one, paste on the other.
- **Private:** the two computers pair once with a 6-digit code, then everything is
  encrypted. It only works over the cable, never Wi-Fi. No cloud, no accounts, no tracking.

## What you need

- A **Mac** with macOS 13 or later.
- A **Windows 10 or 11 PC** with an Ethernet port and any recent NVIDIA, AMD or Intel graphics.
- An **Ethernet cable**, plus a **USB-C Ethernet adapter** for the Mac. Plug the cable
  straight from the Mac to the PC: no router, no network settings.

## Setup (once)

**1. Get the code on both computers.** Click the green **Code** button above →
**Download ZIP** and unzip it (or `git clone` it). Put it somewhere like your Documents
folder. Everything Macstream needs is kept inside that folder.

**2. Mac.** Open Terminal, go into the folder and run:

```bash
xcode-select --install          # only if you don't have Xcode or its command line tools yet
./tools/mac/build-server.sh
```

This makes the **Macstream** app (it shows up in Launchpad).

**3. PC.** Open PowerShell in the folder and run:

```powershell
.\tools\setup-dotnet.ps1
.\tools\setup-ffmpeg.ps1
```

This downloads the two things the viewer needs (about 370 MB, checked against their
published checksums) into the folder. No admin rights, nothing installed system-wide.

## Use it

1. **Mac:** open **Macstream** from Launchpad. The first time, macOS asks for
   **Screen Recording** and **Accessibility**: allow both for Macstream in System Settings →
   Privacy & Security, then open Macstream again.
2. **PC:** double-click **`Macstream.cmd`** in the folder. It finds the Mac on the cable;
   click **Connect**.
3. **First time only:** both screens show a 6-digit code. If they match, click **Yes** on
   the PC and **Allow** on the Mac.

That's it. Click into the window and you're using the Mac.

### Handy keys

| Keys | What they do |
|---|---|
| Ctrl or Windows key | Command (Ctrl+C copies, Win+Space opens Spotlight) |
| Alt / Right Ctrl | Option / Control |
| Win+Tab | Switch Mac apps (Alt+Tab still switches Windows apps) |
| Ctrl+Alt+F12 | Mac control on/off |
| Ctrl+Alt+F11 | Full screen on/off |

### Settings

Click **Settings** in the viewer (or the Macstream icon near the Windows clock) to choose
the Mac's own screen or an extra display, where your MacBook sits, clipboard sharing,
scrolling and key mapping. On the Mac, Macstream lives in the menu bar and the Dock.
Everything is explained in [docs/settings.md](docs/settings.md).

## If something doesn't work

- **"Looking for the Mac on the cable":** check that Macstream is running on the Mac and
  the cable is plugged in at both ends.
- **Black picture:** run `.\tools\setup-ffmpeg.ps1` again, then reopen `Macstream.cmd`.
- **Mouse or keyboard does nothing:** allow Macstream under Accessibility on the Mac and
  open it again.
- **Lid closed:** see [docs/closed-lid.md](docs/closed-lid.md).
- **Start pairing over:** *Forget Paired PCs…* in the Mac's menu bar and *Forget this Mac…*
  in the PC's Settings.
- **Logs:** *Open log folder* in the PC's tray menu, *Open Log* in the Mac's menu bar.

## Uninstall

- **PC:** `.\tools\uninstall-windows.ps1`
- **Mac:** `./tools/mac/uninstall.sh`

Each removes everything Macstream created on that computer, including its permissions and
pairing keys (add `-DryRun` / `--dry-run` to see the list first). Then delete the folder.

## More

- [docs/settings.md](docs/settings.md): every feature and setting
- [docs/developers.md](docs/developers.md): how it works, folders, building
- [Protocol/PROTOCOL.md](Protocol/PROTOCOL.md): wire format and security handshake

Macstream is a fork of [grasscaograss/USB4-WiredMonitor](https://github.com/grasscaograss/USB4-WiredMonitor),
which streamed the other way.

## License

MIT, see [LICENSE](LICENSE). FFmpeg isn't part of this repository: `tools/setup-ffmpeg.ps1`
downloads an unmodified LGPL build of FFmpeg ([ffmpeg.org](https://ffmpeg.org/), source at
[github.com/FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)), which keeps its own license.

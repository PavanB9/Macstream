# Security audit of upstream USB4-WiredMonitor (commit 15a1661)

Audited 2026-11-05 by static review of every file, before anything was built or run.
The hardening listed under "Recommendations" items 1-2 is applied in Macstream (see the "Fork status" sections below).


### 1. Repository overview
- MIT license. Created 2026‑06‑15. 17 commits, all by "JoJo-Remote", spread over 2026‑05‑24 to 06‑15. 0 stars, 0 forks. The owner account dates from 2018 and has 47 repos. **Very few people have looked at this code.**
- It's 115 KB of source in total. No binaries, no `.github/` workflows, no submodules, no git hooks, no build or install scripts (`.sh`/`.ps1`), and no checked-in DLLs.

### 2. What I inspected (all 38 files)
- **Mac:** `Package.swift`, `Main.swift`, `FrameServer.swift`, `Protocol.swift`, `ScreenCapture.swift`, `H264Encoder.swift`, `VirtualDisplay.swift`, `VirtualDisplayBridge.m/.h`, `WindowsControlForwarder.swift`, `CursorTracker.swift`, `DisplayDiagnostics.swift`, `ServerText.swift`
- **Windows:** `.csproj`, `app.manifest`, `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `FrameReceiver.cs`, `Usb4IpDetector.cs`, `PacketTypes.cs`, `WindowsInputInjector.cs`, `H264Decoder.cs`, `D3D11VideoRenderer.cs`, `D3DImageFramePresenter.cs`, `FrameRenderer.cs`, `WindowsDisplayInfo.cs`, `DiagLog.cs`, `AppText.cs`
- **Other:** `NetworkTest/Program.cs`, `PROTOCOL.md`, both READMEs, `AGENTS.md`/`CLAUDE.md` (the two are identical: a Chinese project summary with no agent instructions), `.gitattributes`, `.gitignore`
- I grepped every source file for process launching, HTTP, downloads, registry access, Base64, dynamic assembly or library loading, credentials and sockets.

### 3. Suspicious or unsafe code
**I found nothing malicious.** There's no telemetry, no outbound internet traffic, no downloader, no obfuscation and no persistence. These are the things worth knowing about:
- `Usb4IpDetector.cs` runs `arp -a`. It's harmless: it only reads the neighbour table.
- `VirtualDisplayBridge.m` calls Apple's **private** `CGVirtualDisplay` classes through `objc_msgSend`. Other virtual-display tools use the same well-known technique. It isn't harmful, but Apple doesn't support it and a macOS update could break it.
- The D3D11 renderer calls COM vtables by hand-numbered index. That's fragile, but not malicious.
- `H264Decoder` loads FFmpeg DLLs from `bin/.../ffmpeg/` and searches the bin tree recursively for any folder named `ffmpeg`. Whoever can write files into the build folder can plant DLLs there. That's low risk because everything is local.

### 4. Network communication and open ports
| Component | Listens / connects | Exposure |
|---|---|---|
| Mac server | `NWListener` on TCP **9802, all interfaces** (Wi‑Fi included), no TLS (`NWParameters(tls: nil)`) | **Anyone on any network the Mac is on** |
| Windows client | Connects out to 9802. Auto-discovery probes **every IP on the subnet of each candidate interface, plus ARP neighbours, plus all ~65,000 addresses in 169.254/16**, 384 at a time, over and over | Scans the LAN, including this PC's Hyper‑V `vEthernet` (reported as 10 Gbps, so it qualifies) |
| NetworkTest | `TcpListener(IPAddress.Any, 9800)` | Any network |
| Port 9801 | Reserved, not used | none |

### 5. Authentication and encryption problems (the critical part)
- **C1, critical:** There's no authentication or encryption at all. Anyone who can reach port 9802 gets a **live H.264 stream of your Mac screen**, and the server accepts several clients at once.
- **C2, critical:** When Mac→Windows Control Mode is on, the Mac's global event tap forwards **every keystroke (passwords included) to every connected client**, and that includes an attacker who connected without authenticating.
- **H1, high:** The Windows client trusts whatever server answers. Auto-discovery connects to the **first host that accepts a connection on 9802**. That host can then send `WINDOWS_CONTROL_MODE` followed by input events, and the client **injects keyboard and mouse input into Windows with `SendInput`**. A rogue device on the LAN can take control of Windows.
- **M1, medium:** A malicious server can also send crafted video to FFmpeg's parser and payloads of up to 128 MB.
- **M2, medium:** The README tells you to grant Accessibility and Input Monitoring to **Terminal**. Every program you launch from Terminal then gets those powers too.

The existing app **doesn't** let anyone control the Mac. The server ignores `INPUT_EVENT` (`FrameServer.handleControlPacket` falls through to `default: break`).

### 6. Dependency and licensing risks
| Dependency | Version | License / notes |
|---|---|---|
| FFmpeg.AutoGen (NuGet, Ruslan Balanukhin) | 8.1.0 (latest 9.0.1.1) | LGPL‑3.0 (to confirm when I fetch the package). Bindings only, no native code |
| Microsoft.Extensions.Logging(.Console) | 8.0.1 | MIT, Microsoft |
| FFmpeg native DLLs | not pinned (README links BtbN "master-latest **gpl**") | GPL builds bundle x264/x265, which a decoder doesn't need. **Use a pinned BtbN `n8.1` LGPL shared build and check it against its published SHA256** |
| Apple frameworks (VideoToolbox, ScreenCaptureKit, CoreGraphics) | system | platform license. `CGVirtualDisplay` is private API |
| Swift packages | none | n/a |

### 7. What shouldn't be trusted
- There are no binaries or scripts in the repo. The only external binary is FFmpeg, and the README points to an unpinned "latest" build. I'll replace that with a pinned version and checksum.
- Upstream's `AGENTS.md`/`CLAUDE.md` were out of date (they say .NET 8 and that input forwarding isn't done); Macstream doesn't ship them.

### 8. Overall risk
- **Code provenance:** low risk. Nothing malicious; it's safe to clone and read.
- **Running it as-is:** **high risk** on any Mac connected to Wi‑Fi (C1 and C2), and on any Windows PC that has auto-discovery or control mode enabled (H1).
- Work paused after the audit until these critical issues were reviewed and the hardening below was approved.

### 9. Recommendations before going further
1. Fork and clone are fine. Don't run the stock server on a Mac connected to a network.
2. Before the first test run, make two small hardening changes:
   - Bind the Mac listener to the cable interface only (`NWParameters.requiredInterface`, or interface type `.wiredEthernet`/`.other`).
   - Turn off the Mac→Windows Control Mode and Windows auto-discovery scanning by default.
3. Add pairing and encryption (Step 10) before any Windows→Mac input goes into daily use.
4. Sign the Mac server as its own `.app`, and grant permissions to that app rather than to Terminal.

## Fork status: Windows -> Mac mouse and keyboard control (added in Macstream)

- The Mac now accepts mouse and keyboard events (`INPUT_EVENT`) from the connected viewer and
  posts them with CoreGraphics. This needs the macOS Accessibility permission for `Macstream` (the Mac app; granted to
  the app, not to Terminal), which `tools/mac/uninstall.sh` revokes.
- Exposure is unchanged: the listener is wired-Ethernet only and single-client. Input is refused
  when the listener is widened (`MACSTREAM_INTERFACE=any`) unless `MACSTREAM_INPUT=1`,
  and can be turned off with `MACSTREAM_INPUT=0`.
- Every packet is length-checked, coordinates are clamped to the streamed display, and only
  buttons 0-2 and Mac key codes 0-127 are accepted. Held buttons and keys are released on
  disconnect, on a client ReleaseAll, or after 1 s without any packet, so a pulled cable can't
  leave a button or key (say, Command) stuck down.
- Windows side: keys are captured with a low-level keyboard hook (`WH_KEYBOARD_LL`), the same
  mechanism Remote Desktop uses to pass the Windows key and Alt+Tab to the remote machine. The
  hook is installed only while the viewer window is focused and removed when it loses focus;
  each key is also checked against the foreground window, so typing in other programs is never
  read or sent. Keys go to the Mac only while "Mac control" is on; Ctrl+Alt+F12 switches it on
  and off, Ctrl+Alt+Del always reaches Windows, and Alt+Tab stays Windows' own switcher (the
  viewer types it into Windows itself, marked so its own hook lets it through). Nothing is logged except the fact that
  keyboard input started (no key contents).

## Fork status: pairing and encryption (added in Macstream)

This closes C1, C2 and H1 above for Macstream's own client and server.

- **Pairing:** a PC must be allowed once, on both machines. Both screens show the same 6-digit
  code; the Mac's alert defaults to "Don't Allow" and the PC asks too (so a device posing as
  the Mac can't pair with the PC on its own). The PC keeps the Mac's key only after the Mac
  has proved it with its first encrypted packet. A commitment scheme stops a device in the middle
  of the cable from making the codes match (1 in a million per try, one alert at a time, 10 s
  cooldown after a refusal). Unpaired connections get no video and cannot send input.
- **Authentication:** every connection proves both long-term keys (static and ephemeral ECDH);
  the PC also checks it is talking to the Mac it paired with, so a rogue "Mac" on the cable
  cannot feed it video or commands.
- **Encryption:** everything after the handshake (video, mouse, keystrokes, stats) is
  AES-256-GCM with fresh per-connection keys (forward secret). Tampered or replayed data ends
  the connection.
- **Key storage:** Mac key and paired PCs in the login Keychain; the PC's key in a
  DPAPI-encrypted file inside the project folder. Nothing in plaintext; the uninstallers remove
  them, `tools/mac/unpair.sh` forgets paired PCs.
- **Local only:** the Mac still listens on wired Ethernet only; the viewer refuses host names
  and any non-local address, and its discovery skips Wi-Fi, Bluetooth, VPN and virtual adapters.
- Remaining limits: the pairing alert needs someone at the Mac; the stock upstream client and
  server (without these changes) cannot connect to Macstream at all.

## Fork clean-up (2026-11-06)

- The Mac -> Windows Control Mode (C2, H1) is gone from the code, not just switched off:
  `WindowsControlForwarder.swift` (the Mac's global event tap) and `WindowsInputInjector.cs`
  (`SendInput` on Windows) are deleted, and both sides no longer know the
  `WINDOWS_CONTROL_MODE` / `WINDOWS_INPUT_EVENT` packet types (0x0070/0x0071). An unknown
  packet type is ignored by the viewer and dropped by the Mac.
- The separate cursor channel (`CursorTracker.swift`, `CURSOR_POSITION` 0x0060) is removed too;
  by default macOS draws the cursor into the video frames.

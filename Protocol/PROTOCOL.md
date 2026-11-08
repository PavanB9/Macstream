# Macstream protocol

## Overview

The Mac (server) streams its screen to the Windows viewer (client) over TCP on a direct
wired link, and the viewer sends mouse and keyboard input back to the Mac.

## Transport

- **Protocol**: TCP over a direct Ethernet (or Thunderbolt/USB4 network) link
- **Ports**: 9802 (everything: handshake, video, input), 9801 (reserved, unused)
- **Byte order**: little endian

## Packet Format

Every packet starts with the same header:

```
┌──────────┬──────────┬──────────┬──────────────┐
│ Magic    │ Version  │ Type     │ Payload Len  │
│ 2 bytes  │ 2 bytes  │ 2 bytes  │ 4 bytes      │
│ 0xWM     │ 0x0001   │ uint16   │ uint32       │
└──────────┴──────────┴──────────┴──────────────┘
Total header: 10 bytes
```

Video width and height are aligned to 2 pixels by default (no 16-pixel cropping and
rescaling of Retina/high-DPI screens); `MACSTREAM_ALIGN` overrides it.

## Packet Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| HELLO | 0x0001 | C→S | Client hello with the Windows display info |
| HELLO_ACK | 0x0002 | S→C | Server acknowledgement |
| DISPLAY_INFO | 0x0010 | S→C | Display configuration |
| FRAME_REQUEST | 0x0020 | C→S | Ask for a keyframe |
| FRAME_H264 | 0x0030 | S→C | H.264 frame |
| FRAME_RAW | 0x0031 | S→C | Raw BGRA frame (fallback only) |
| FRAME_HEVC | 0x0032 | S→C | HEVC/H.265 frame |
| INPUT_EVENT | 0x0040 | C→S | Mouse/keyboard input for the Mac |
| INPUT_FEEDBACK | 0x0041 | S→C | The mouse moved onto / back from the Mac's own screen |
| STATS | 0x0050 | both | Stats, ping/pong |
| CLIPBOARD | 0x0080 | both | Clipboard sync (text, pictures) |
| AUTH_* / PAIR_* | 0x0090-0x0094 | both | Security handshake (below) |

Reserved, no longer used: 0x0060 (separate cursor channel) and 0x0070/0x0071 (the original
fork's Mac -> Windows control mode). The macOS cursor is drawn into the video frames.

## Packet Details

### HELLO (0x0001)
```
Payload (32 bytes, little endian; older viewers send only the first 16):
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ ClientWidth  │ ClientHeight │ RefreshRate  │ Dpi          │   this PC's monitor
│ uint32       │ uint32       │ uint32       │ uint32       │
├──────────────┼──────────────┼──────────────┼──────────────┤
│ ScreenMode   │ TextScale    │ ExtraWidth   │ ExtraHeight  │   what to show
│ uint32       │ uint32       │ uint32       │ uint32       │
└──────────────┴──────────────┴──────────────┴──────────────┘
```

- ScreenMode: 0 = mirror the Mac's own screen (default, also for 16-byte HELLOs), 1 = an
  extra Mac display (CGVirtualDisplay) made for the PC.
- TextScale (extra display only): 0 = automatic (Retina when the PC monitor is >= 144 dpi),
  1 = normal (one Mac pixel per PC pixel), 2 = Retina (logical size is half the pixels).
- ExtraWidth/ExtraHeight: the extra display's size in pixels; 0 = ClientWidth/ClientHeight.
- The Mac keeps the running stream when a reconnecting viewer asks for the same thing, and
  rebuilds it (new display, new capture and encoder) when the request changed. When the
  last viewer disconnects, an extra display is removed after 5 s unless a viewer reconnects
  in that time (the mirror keeps running). If the extra
  display can't be created it mirrors the Mac's screen instead. Mac environment variables
  win: `MACSTREAM_MIRROR_MAIN=1` or `MACSTREAM_VIRTUAL_DISPLAY=0` always mirror,
  `MACSTREAM_VIRTUAL_DISPLAY=1` always makes an extra display.

### HELLO_ACK (0x0002)
```
Payload:
┌──────────────┬──────────────┐
│ ServerWidth  │ ServerHeight │
│ uint32       │ uint32       │
└──────────────┴──────────────┘
```

### DISPLAY_INFO (0x0010)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ Width        │ Height       │ RefreshRate  │ Codec        │
│ uint32       │ uint32       │ uint32       │ uint8        │
│              │              │ Hz           │ 0=Raw,1=H264 │
│              │              │              │ 2=HEVC       │
└──────────────┴──────────────┴──────────────┘
```

### FRAME_H264 (0x0030)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ IsKeyFrame   │ Width        │ Height       │ NAL Data     │
│ uint64       │ uint64       │ uint8        │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ 0/1          │ pixels       │ pixels       │ Annex-B H264 │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### FRAME_HEVC (0x0032)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ IsKeyFrame   │ Width        │ Height       │ NAL Data     │
│ uint64       │ uint64       │ uint8        │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ 0/1          │ pixels       │ pixels       │ Annex-B HEVC │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### FRAME_RAW (0x0031)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ Width        │ Height       │ BytesPerRow  │ BGRA Data    │
│ uint64       │ uint64       │ uint32       │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ pixels       │ pixels       │ bytes        │              │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### INPUT_EVENT (0x0040)

Windows -> Mac control (Macstream). Little endian. Payload starts with a kind byte.
Coordinates are `f32` fractions (0..1) of the video picture; the Mac clamps them and maps
them onto the streamed display with `CGDisplayBounds`, so they are independent of window
size, DPI and Retina scaling. Packets with the wrong length, non-finite coordinates, an
unknown button or a key code above 127 are ignored.

```
0x01 MouseMove   f32 x, f32 y                                   (9 bytes)
0x02 MouseDown   u8 button, u8 clickCount, f32 x, f32 y         (11 bytes)
0x03 MouseUp     u8 button, u8 clickCount, f32 x, f32 y         (11 bytes)
0x04 KeyDown     u16 macKeyCode                                 (3 bytes)
0x05 KeyUp       u16 macKeyCode                                 (3 bytes)
0x06 Scroll      i32 dx, i32 dy  (pixels; +dy scrolls up, +dx scrolls right; clamped to ±10000) (9 bytes)
0x07 ReleaseAll  (1 byte)  release every button/key the client pressed
0x08 Heartbeat   (1 byte)  sent every 250 ms while connected
0x09 OwnScreenEnter  u8 side, f32 along                         (6 bytes)
0x0A RelativeMove    f32 dx, f32 dy  (pixels of the video picture; clamped to ±10000) (9 bytes)

button: 0 = left, 1 = right, 2 = middle. clickCount: 1-3 (double/triple click).
macKeyCode: Mac virtual key code (HIToolbox kVK_*, 0-127). The client maps Windows scan codes
to these by key position (Windows/MacstreamViewer/Input/MacKeyMap.cs); the Mac's keyboard
layout decides the character. A KeyDown for a key that is already down is a key repeat.
```

Keyboard: the Mac tracks the modifier keys the client holds and sets the matching flags
(Command, Shift, Option, Control, Fn, Caps Lock) on every key, click and scroll event, so
shortcuts and modifier-clicks do not depend on macOS's own idea of what is held. Modifier
keys are posted as flags-changed events and are never auto-repeated.

Safety: the Mac remembers which buttons and keys the client pressed and releases them when the
client disconnects, sends ReleaseAll, or sends nothing at all for 1 s while one is held.
The client releases held buttons when its window loses focus or mouse capture, and held keys
when its window loses focus or Mac control is switched off (Ctrl+Alt+F12 or the toolbar).

Policy (Mac): on by default only while the listener is restricted to wired interfaces;
`MACSTREAM_INPUT=0` turns it off, `MACSTREAM_INPUT=1` forces it on, and
`MACSTREAM_KEYBOARD=0` keeps the mouse but refuses key events. INPUT_EVENT is only accepted
from a connection that has sent HELLO, and key events only while a display is being streamed. It is refused with
`MACSTREAM_INTERFACE=any`. Posting
events needs the macOS Accessibility permission for Macstream (the Mac app).

(The inherited upstream layout with int32 pixel coordinates was documented but never
implemented by either side; this replaces it.)

#### The Mac's own screen (OwnScreenEnter, RelativeMove, INPUT_FEEDBACK 0x0041)

With an extra display, the MacBook's own screen can sit "beside" the PC monitor. The viewer
setting *MacBook screen* says on which side (side: 0 = the Mac is left of the PC, 1 = right,
2 = above, 3 = below) and which part of that edge of the viewer's picture area leads there
(whole, a half, or automatic: where that edge is on the monitor's edge, the part with no other
PC monitor beyond it, otherwise the whole edge). This works in full screen, maximized or a
window. While the pointer is in that part, the viewer stops it at the edge (a cursor clip
that only blocks that side), and when it reaches the edge it sends OwnScreenEnter with
`along` = 0..1 along that part (top to bottom, or left to right). Until the Mac answers (or
1 s passes) the viewer sends no MouseMove: the Mac would take it as "back on the streamed
display" and undo the move.

The Mac picks its own screen (the built-in display if it is on, otherwise another real
display, never the streamed one), puts the pointer on the side facing the PC at the same
fraction, and answers with INPUT_FEEDBACK. Only after `Entered` does the viewer hold the
Windows pointer inside its window and send RelativeMove for every movement (picture pixels;
the Mac multiplies by the streamed display's points per pixel, so the speed matches).
Clicks and scrolls keep their usual packets; on the own screen the Mac clicks where the
pointer is and ignores the coordinates. Any MouseMove (absolute) ends own-screen mode on the
Mac. A RelativeMove arriving while the Mac is not in own-screen mode is answered with
BackOnStreamed (at most every 200 ms), so the viewer never keeps its pointer held for nothing.
Pushing past the side facing the PC hands the mouse back:

```
INPUT_FEEDBACK payload, u8 kind:
0x01 Entered          (1 byte)   the pointer is on the Mac's own screen
0x02 Unavailable      (1 byte)   no own screen to go to (lid closed, only the extra display)
0x03 BackAcrossEdge   f32 along  came back over the side facing the PC; the viewer puts the
                                 Windows pointer at that fraction of its zone, just inside
0x04 BackOnStreamed   f32 x, f32 y   the pointer was moved onto the streamed display on the
                                 Mac (its trackpad); x, y are fractions of the picture
```

The viewer also leaves own-screen mode (and unclips the pointer) when its window loses
focus, Mac control is switched off or the connection drops; the Mac then keeps the pointer
where it is until the next absolute move. A Mac app without this ignores OwnScreenEnter and
never answers, so the viewer simply stays on the picture (it asks again after 1 s).

### STATS (0x0050)

Payload starts with a kind byte.

Kind 1, server stats (S→C, once per second while streaming, 49 bytes, little endian):

```
u8  kind = 1          u8  version = 2
u32 width             u32 height            u8 codec (1=H.264, 2=HEVC)
f32 captureFps        f32 encodeFps
f32 encodeMsAvg       f32 encodeMsMax       (capture → encoded, ms)
u32 bitrateKbps       u32 targetBitrateKbps
u32 deferredForBackpressure   u32 droppedFrames   u8 maxQP
[v2] f32 encoderMsAvg (inside VideoToolbox)   u8 flags
     (bit0 low-latency encoder, bit1 YUV capture, bit2 streaming an extra display)
```

Kind 2, ping (C→S, 9 bytes): `u8 kind = 2, u64 token`. The server echoes the same bytes
back as kind 3 (pong); the client computes round-trip time from its token.

### FRAME_REQUEST (0x0020)

Empty payload. The client lost its decoding reference and asks for a keyframe. The server stops
sending predicted frames to that client until the next keyframe and asks the encoder for one
(rate-limited to one request per 0.5 s).

### CLIPBOARD (0x0080)

Clipboard sync, text and pictures, encrypted like every packet after the handshake.
Payload starts with a kind byte:

```
0x01 SyncOn   C->S  (1 byte)   the PC's toggle is on: the Mac starts watching its clipboard
0x02 SyncOff  C->S  (1 byte)   stop watching
0x03 Text     both  u8 kind + UTF-8 text, at most 512 KiB
0x04 Image    both  u8 kind + a PNG file, at most 12 MiB (must start with the PNG signature
                    and decode; the receiver puts PNG, plus TIFF on the Mac / a bitmap on
                    Windows, on its clipboard)
```

- A copy with both text and a picture (e.g. from a word processor) sends the text. Copied
  files are not sent (Finder puts their icons on the clipboard as pictures).
- The Mac accepts control packets up to 13 MiB and encrypted records up to 14 MiB from the
  viewer for this; everything else the viewer sends is tiny.

- The PC's toggle decides for both machines. The Mac watches its clipboard only between
  SyncOn and SyncOff/disconnect, and ignores Text from the PC outside that window. The PC
  sends SyncOn after HELLO when it connects with the toggle on.
- Only copies made while sync is on are sent; what is already on the clipboard is not, and
  neither side keeps a history. Copies marked private (macOS `org.nspasteboard.ConcealedType`
  / `TransientType`, Windows `ExcludeClipboardContentFromMonitorProcessing`) are not sent.
  Text and pictures written on Windows carry `CanUploadToCloudClipboard = 0`.
- Accepted on the Mac only from a paired connection that sent HELLO. `MACSTREAM_CLIPBOARD=0`
  turns it off on the Mac. Contents are never logged, only sizes.

## Security handshake (Macstream)

Every connection starts with this handshake, in plaintext packets of the normal format,
before HELLO. Until it succeeds the Mac sends no video and ignores everything else. After it,
**every packet in both directions is encrypted** (see "Encrypted records").

Crypto: P-256 ECDH, HKDF-SHA256, AES-256-GCM, SHA-256 (CryptoKit on the Mac,
System.Security.Cryptography on Windows). Public keys are X9.63 uncompressed (65 bytes).
Each side has a long-term key: the Mac's is in the login Keychain
(`com.macstream.server` / `identity`), the PC's in `.local\pairing\windows-pairing.dat`
(DPAPI-encrypted for the Windows user). The Mac also keeps the paired PCs' public keys in the
Keychain (`trusted-clients`).

```
0x0090 AUTH_CLIENT_HELLO  C->S  u8 version=1, clientStatic(65), clientEphemeral(65), nonce(32),
                                u8 nameLen (<=64), name (UTF-8, shown in the Mac's alert)
0x0091 AUTH_SERVER_HELLO  S->C  u8 version=1, u8 status, serverStatic(65), serverEphemeral(65),
                                nonce(32), [commitment(32) when status = 1]
       status: 0 = known PC, 1 = pairing required, 2 = refused (another request is open, or
               one was refused in the last 10 s)
-- only when status = 1 (first connection of this PC) --
0x0092 PAIR_NONCE         C->S  clientPairNonce(32)
0x0093 PAIR_REVEAL        S->C  serverPairNonce(32)
0x0094 PAIR_RESULT        S->C  u8 1 = allowed, 0 = not allowed (then the Mac closes)
```

- commitment = SHA256("macstream commit v1" | serverStatic | clientStatic | serverPairNonce).
  The PC checks it after PAIR_REVEAL. Because the Mac commits before it sees the PC's nonce,
  a device in the middle cannot choose values that make both screens show the same code.
- code = first 4 bytes (big endian) of SHA256("macstream pairing code v1" | transcript)
  mod 1 000 000, shown as "123 456" on the PC and in the Mac's alert ("Don't Allow" is the
  default button). transcript = CH | SH | PAIR_NONCE | PAIR_REVEAL payloads.
- The PC also asks its user whether the Mac shows the same code; pairing needs a yes on both
  machines (otherwise a device posing as the Mac could just send PAIR_RESULT = 1). The PC
  stores serverStatic only after the first encrypted record from the Mac verifies.
- A known PC (status 0) must see the serverStatic it stored when pairing, or it stops.
  A PC that is already paired only accepts a pairing request (status 1) from that same key.
- Session keys: HKDF-SHA256(ikm = ss | ee | es | se, salt = SHA256("macstream session v1" |
  transcript), info = "macstream v1 keys", 64 bytes) -> client-to-server key (first 32),
  server-to-client key (last 32). ss = static x static, ee = ephemeral x ephemeral,
  es = clientEphemeral x serverStatic, se = clientStatic x serverEphemeral. Only the paired
  PC and Mac can compute them, and a new ephemeral pair per connection gives forward secrecy.
- Limits on the Mac: 4 connections in the handshake at once, 10 s to send AUTH_CLIENT_HELLO,
  120 s for the user to answer, one pairing alert at a time, 10 s cooldown after a refusal.

### Encrypted records

After the handshake each packet (10-byte header + payload) travels as one record:

```
u32 LE plaintextLength | ciphertext (plaintextLength bytes) | 16-byte GCM tag
```

AES-256-GCM with the direction's key; nonce = 4 zero bytes | u64 LE record counter (starts at
0 in each direction, never sent); associated data = the 4 length bytes. A record that fails
authentication (tampering, replay, reordering, a dropped record, the wrong keys) closes the
connection.

Forget pairings: `./tools/mac/unpair.sh` on the Mac (all PCs), delete
`.local\pairing\windows-pairing.dat` on the PC.

## Connection Flow

```
Windows (Client)                    Mac (Server)
     │                                  │
     │──── TCP Connect (port 9802) ────>│
     │<─── security handshake ─────────>│  (pairing the first time; see above)
     │      everything below is encrypted
     │──── HELLO ──────────────────────>│
     │      Mac creates virtual display │
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │                                  │
     │──── INPUT_EVENT ────────────────>│
     │                                  │
```

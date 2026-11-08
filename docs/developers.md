# Macstream for developers

## How it works

- **Video Mac → PC:** the Mac captures its screen (ScreenCaptureKit / CGDisplayStream) and
  encodes it with VideoToolbox (H.264, low-latency mode). The PC decodes it on the GPU
  (FFmpeg D3D11VA) and shows it in a native flip-model swap chain.
- **Input PC → Mac:** mouse and keyboard (by key position) go back over the same connection
  and are posted with CoreGraphics.
- **Security:** pairing with a 6-digit code confirmed on both machines, then P-256 ECDH and
  AES-256-GCM for everything. The Mac listens on wired interfaces only. Details:
  [Protocol/PROTOCOL.md](../Protocol/PROTOCOL.md) and [security-audit.md](security-audit.md).

Measured on an M2 MacBook Air and an NVIDIA RTX 5060 Ti over a 1 Gbps cable: 2560×1664 at
~60 fps, ~1 ms network round trip, ~16 ms from capture to encoded frame.

## Folders

```text
Mac/MacstreamServer/       Swift server: capture, encode, pairing, input, menu bar
Windows/MacstreamViewer/   C#/.NET 10 WPF viewer: receive, decode, render, input, tray
NetworkTest/               Link throughput and latency tool
Protocol/PROTOCOL.md       Wire format and security handshake
docs/                      Settings, closed-lid guide, security audit
tools/                     Pinned setup, build, run and uninstall scripts
```

## Building

- **PC:** `. .\tools\env.ps1`, then
  `dotnet build Windows\MacstreamViewer\MacstreamViewer.csproj -c Debug` (close the viewer
  first; the exe is locked while it runs). `Macstream.cmd` does this for you when a source
  file is newer than the last build.
- **Mac:** `./tools/mac/build-server.sh` builds `build/mac/Macstream.app` and signs it with a
  local certificate kept in `.local/signing`, so macOS keeps its permissions across rebuilds.
  The bundle ID is `local.macstream.server`; set your own in `tools/personal/mac.conf`
  (`MACSTREAM_BUNDLE_ID="..."`), see `tools/mac/app-id.sh`.

Every toolchain, cache and build output stays inside the project folder (`.local/`,
`third_party/`, `build/`, `logs/`, all git-ignored). The .NET SDK and FFmpeg are pinned
and checked against their published checksums.

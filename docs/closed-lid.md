# Using Macstream with the MacBook lid closed

Goal: the MacBook sits on a shelf with the lid shut, and you use it only from the PC.
Short version: **closing the lid puts the Mac to sleep, and the Ethernet cable does not
change that.** Pick one of the options below. Nothing here needs code changes.

## What macOS does when you close the lid

- By default, closing the lid puts the Mac to sleep (Apple's sleep/wake page says so).
  A sleeping Mac sends no video, so Macstream goes dark.
- Apple documents one exception, often called "clamshell mode": with an **external display**
  and an **external keyboard and mouse** connected, you can close the lid and keep working.
  Apple's dual-display article also lists power as part of the setup (not needed if the
  display itself powers the Mac). Plug the charger in anyway.
- Macstream's cable does not count. macOS only treats a real display connected to a video
  port (USB-C/Thunderbolt, HDMI, DisplayPort) as an external display. An Ethernet link
  carrying a video stream is just network traffic to the Mac.

## Your options

### A. Lid open, screen dimmed (simplest, no risk)

1. Leave the lid open, charger plugged in.
2. Turn the brightness all the way down (the screen then looks off).
3. Optional: keep the Mac awake with Amphetamine (see C, but with the lid open).

Downside: the Mac is not closed up, and the keyboard is exposed. Macstream still captures
the real screen at full resolution; brightness does not change the picture.

### B. HDMI / DisplayPort "dummy plug" (real clamshell mode)

A dummy plug (headless display emulator, a few euros/dollars) makes macOS think a monitor
is attached.

1. Plug it into the Mac (via a USB-C to HDMI/DisplayPort adapter if it is HDMI/DP).
2. Connect a keyboard and mouse to the Mac (a Bluetooth pair that is paired and on is fine;
   USB also works).
3. Connect the charger, then close the lid.
4. Check from the PC that the picture is still there.

Things to know:

- macOS now has two displays. Macstream streams the **main display** (the one with the menu
  bar). With the lid closed the dummy plug is the only display, so it becomes the main one.
- `MACSTREAM_CAPTURE_SIZE=panel` (the default) only applies to the **built-in panel**.
  For the dummy plug you get that display's own resolution, so pick a plug that offers
  your wanted size (e.g. 2560x1440 or higher) and set it in System Settings → Displays.
  The result is sharp on the PC but not the 2560x1664 of the MacBook panel.
- Cheap plugs vary: some limit refresh rate or resolution. This is the most "normal" way
  for macOS to behave, because it is Apple's own clamshell mode.
- With the lid open again, macOS goes back to using the built-in panel too.

### C. Amphetamine "Closed-Display Mode" (no plug, per-app)

Amphetamine is a free Mac App Store app that keeps the Mac awake. Its Closed-Display Mode
keeps a MacBook awake with the lid shut, without an external display.

1. In Amphetamine, start a session and turn on the option for the Mac to stay awake when the
   built-in display is closed (look for "Closed-Display Mode" in the session settings).
2. On Apple silicon (version 5.3 and later) it asks to install a small helper script and for
   your admin approval (Touch ID or password) when a session starts. You can choose Skip, but
   then a session can fail if you plug or unplug the charger during it.
3. Close the lid. Amphetamine deliberately turns the built-in display off while it is
   closed (to protect it from burn-in), so the Mac keeps running with a dark panel.

Things to know:

- Keep the charger plugged in the whole time. Do not plug or unplug it mid-session.
- It is reversible: end the session (or quit Amphetamine) and normal sleep returns. The
  helper script can be removed from the menu bar icon (Feedback & Support → Uninstall Scripts).
- Not yet tested with Macstream: whether the picture stays live while the built-in panel is
  forced dark. If it is black or frozen, use option A or B instead.
- Heat is the main caveat. See the next section.

### D. `pmset` and other system-wide tricks (just so you know)

`pmset` is macOS's built-in power settings tool. `sudo pmset -a disablesleep 1` and similar
commands turn off sleep for the *whole system*, including lid-close sleep.

**We do not recommend it.** It is a system-wide setting that stays on until you switch it
off, so if you forget it, the Mac can keep running inside a bag or on a bed and overheat.
If you ever do try it, know the undo command first (`sudo pmset -a disablesleep 0`) and
check with `pmset -g` that it is back to normal. Prefer A, B or C, which are per-app or
use Apple's own clamshell mode, and are easy to undo.

Settings like "Prevent automatic sleeping when the display is off" (System Settings →
Battery / Energy, where your Mac offers it) only stop *idle* sleep, not lid-close sleep.

## Heat and battery safety

- **Charger always connected.** Running on battery with the lid shut drains it and can
  lead to a sudden sleep.
- **Let it breathe.** The MacBook Air has no fan: it cools through its metal body. Stand it
  upright or lay it on a hard, open surface. Never on a bed, cushion, in a bag or under
  papers, and do not stack things on it.
- Streaming and encoding keep the chip busy. If the case feels hot, it will slow itself down
  (lower fps, more lag); if so, open the lid or stop streaming.
- Long-term, a laptop that is always plugged in and warm ages its battery faster. Check
  System Settings → Battery → Battery Health now and then, and keep **Optimized Battery
  Charging** on.
- I did not find a closed-lid heat guide from Apple; this is common sense plus how the Air
  is built.

## If the Mac sleeps anyway

- The PC viewer shows the connection dropped. Macstream on the Mac is paused with the rest
  of the Mac.
- When the Mac wakes (open the lid, or press a key on its own keyboard), the PC viewer
  reconnects automatically.
- Do not count on waking the Mac from the PC. "Wake for network access" is a Mac setting
  that works with some network setups, but over this direct cable it is not reliable, and
  Macstream does not send a wake signal. Wake it with a key press or by opening the lid.
- If it does not recover: quit and start `./tools/mac/run-server.sh` again.

## Sources

- Apple, [Put your Mac to sleep or wake it](https://support.apple.com/guide/mac-help/put-your-mac-to-sleep-or-wake-it-mh10330/mac) - closing a Mac laptop's display puts it to sleep.
- Apple, [Connect an external display to MacBook Air](https://support.apple.com/guide/macbook-air/connect-an-external-display-apd8cdd74f57/mac) - close the lid and keep working with an external display and accessories.
- Apple, [Use dual monitors with MacBook Air and MacBook Pro with M3 chip](https://support.apple.com/en-us/117373) - closed-lid requirements: external keyboard and mouse, power.
- Amphetamine, [Mac App Store page and release notes](https://apps.apple.com/us/app/amphetamine/id937984704) - Closed-Display Mode, script and admin approval on Apple silicon, built-in display forced to sleep when closed.

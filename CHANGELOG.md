# Changelog

## 0.7.0 - Chat, Echo Cancellation & Auto-Update

- Added text chat to every voice channel. The main area now shows the chat for the
  channel you're in, Discord-style, with names, avatars, timestamps and day dividers.
  - Joining a channel loads its recent history (up to 200 messages are kept per channel
    while the room is running).
  - Delivery is reliable over UDP: lost messages are re-sent automatically and everyone
    sees the same order. Tested at 25% simulated packet loss.
  - The taskbar button flashes when a message arrives while mæla is in the background.
  - Right-click a message to copy it.
- The sidebar now shows each channel with the people in it underneath (speaking ring,
  host tag, local-mute tag), plus a Lobby section. Right-click a person for volume, mute,
  move, kick and ban; double-click a channel or a person to join them.
- The room code and network status moved to the top bar.
- Channel switches are now retried until the host confirms them, and the host re-sends the
  member list every 3 seconds, so one lost packet can no longer leave someone in the wrong
  channel or with an outdated member list.
- "Leave Room" is available while still connecting (shown as "Cancel").
- Fixed "&" showing as an underline in some buttons and labels.

- Added built-in acoustic echo cancellation, on by default. Friends no longer hear
  themselves when you use speakers instead of a headset.
  - Automatically measures the speaker-to-microphone delay (up to ~680 ms).
  - Adaptive filter with an 85 ms echo tail, protected against double-talk.
  - Residual echo suppression for whatever the filter can't remove.
  - Adds only 10 ms of latency and about 4% of one CPU core.
  - Can be switched off in Settings; the change applies immediately.
- Added automatic updates from GitHub Releases.
  - Checks shortly after launch and every six hours while running (including in the tray).
  - Shows the release notes, with Update now / Later / Skip this version.
  - Downloads are checked against a published SHA-256 and the expected version before
    the executable is swapped and mæla restarts.
  - Can be switched off, or run on demand, from Settings.
- Release workflow now publishes automatically when `VERSION` changes on `main`, attaches
  `Maela.exe.sha256`, and uses the matching CHANGELOG section as release notes.
- App version is now stamped from the `VERSION` file at build time.
- Added echo canceller and chat regression tests to CI.
- Saved rooms are no longer probed every 5 seconds. Each joined room is checked once
  when mæla starts (or when it is added), and once more when you leave it. While you are connected, the room's status and
  member count come from the live session. Right-click a room and choose
  "Check if online" to refresh it by hand.

## 0.6.0 - Room Codes

- Replaced user-facing `IP:port` invites with short mæla room codes.
- Added checksum validation to room codes.
- Hid public IP and UDP port values from normal settings/UI.
- Hosting networking is now automatic from the user's perspective.
- Existing saved literal IPv4 invites are migrated to room codes when possible.
- Kept legacy parsing internally so older saved-room data can still recover.
- Preserved persistent hosting, saved-room presence checks, voice channels, per-user audio controls, kick/ban, and branded Windows integration.
- Added repository and GitHub Actions release structure.

## 0.5.1

- Branded mæla Windows build.
- Added embedded application icon and in-app mark.
- Fixed missing text prompt used by voice-channel creation and rename.

## 0.5.0

- Added multiple voice channels and Lobby state.
- Added persistent host/tray behavior.

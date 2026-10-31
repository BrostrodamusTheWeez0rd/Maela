# Changelog

## 0.7.0 - Echo Cancellation & Auto-Update

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
- Added an echo canceller regression test to CI.

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

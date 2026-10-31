# Release checklist

Before tagging a public build:

- [ ] `build_Maela.cmd` succeeds on a clean Windows machine.
- [ ] `Maela.exe` shows the branded icon in Explorer, taskbar and title bar.
- [ ] Host creates a room code and does not display raw public IP/port in normal UI.
- [ ] A second machine can join using only the room code.
- [ ] A one-character room-code typo is rejected.
- [ ] Saved rooms survive restart and presence status updates.
- [ ] Host can create/rename/delete voice channels.
- [ ] Channel switching does not reconnect the room socket.
- [ ] Audio is only delivered inside the current voice channel.
- [ ] Leave Voice keeps a persistent host room alive when enabled.
- [ ] Closing persistent host mode sends the server to tray without leaving the microphone live.
- [ ] Per-user volume/mute works locally.
- [ ] Host kick and session-ban controls work.
- [ ] Windows Firewall helper succeeds when approved.
- [ ] UPnP success and failure paths are both understandable.
- [ ] With speakers (no headset), the other side does not hear their own voice echoed back after a second or two of talking.
- [ ] Turning echo cancellation off in Settings takes effect immediately.
- [ ] `scripts\test_ci.cmd` passes.
- [ ] `VERSION` is bumped and `CHANGELOG.md` has a matching section.
- [ ] After the release workflow runs, the release has both `Maela.exe` and `Maela.exe.sha256`.
- [ ] A copy of the previous version offers the update and restarts into the new version.
- [ ] `package_release.cmd` produces the expected release ZIP.

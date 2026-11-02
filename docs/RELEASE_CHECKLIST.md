# Release checklist

Before tagging a public build:

- [ ] `build_Maela.cmd` succeeds on a clean Windows machine.
- [ ] `Maela.exe` shows the branded icon in Explorer, taskbar and title bar.
- [ ] Host creates a room code and does not display raw public IP/port in normal UI.
- [ ] A saved hosted room keeps the same code after restarting mæla (same public IP and port).
- [ ] Joining with a password-protected room works with the right password and is refused with a wrong one.
- [ ] Voice sounds clear between two machines, and nothing is sent while nobody talks (Task Manager network graph stays flat).
- [ ] A second machine can join using only the room code.
- [ ] A one-character room-code typo is rejected, and an old 0.6 code shows the "ask for a new code" message.
- [ ] Saved rooms survive restart; each is probed once at startup (not repeatedly), and "Check if online" refreshes one.
- [ ] Rooms saved by the previous version convert on first launch: `rooms-v2.txt` is gone, `rooms-v3.dat` isn't readable, and every room is still listed.
- [ ] A `rooms-v3.dat` replaced with junk shows the "couldn't be opened" warning and is kept as `rooms-v3.unreadable`.
- [ ] Friends between two machines on different networks: notice with Don't show me again, copy a code, send a request, accept, chat both ways; nothing can be sent before accepting.
- [ ] Your profile card's Appear as buttons change status. Friend statuses: Away and Busy show to friends; Busy makes no sound; Offline shows as offline and messages arrive after coming back.
- [ ] Friends and queued messages survive restarting both machines; pasting a friend's new code updates them with no new request.
- [ ] Backup and restore: back up on one PC, restore on another with the right password (a wrong one is refused), a friend pastes the new code, and messages flow both ways with no new request.
- [ ] Friend calls between two machines on different networks: ring, accept, talk both ways, and hanging up on either side ends it for both. Declining, nobody answering and Busy (a missed call in the chat) all behave.
- [ ] A call from inside a room asks first. Password rooms work with both sides on this version, and an older client can still join an updated host.
- [ ] Friend chats: colour emoji and the picker work, reactions show on both sides and clicking one takes yours away, and search filters and highlights.
- [ ] A friend request pops up on the receiver with a check and an X; Busy shows no pop-up and keeps the request in the list.
- [ ] First launch of this version offers the firewall rule and Defender exclusion once; Not now changes nothing and doesn't ask again. With the rule, a one-way friend request arrives; without it, the Friends window warns and can fix it. Settings > Network can add and remove both.
- [ ] Defender exclusion: add and remove report correctly, and show the manual steps if Tamper Protection blocks them.
- [ ] The top rail button swaps room tiles for friend tiles in the same row, and clicking one opens the chat; + adds a friend and right-click blocks or removes; the room card and name bar stay; the labels swap; the button goes back, with nothing overlapping after switching back and forth; the badge counts unread and requests; during a call, Friends offers a way back to it.
- [ ] Joining asks for no name; the room takes the host's name, for saved rooms too, and a host renaming shows for members next time they connect.
- [ ] Friends looks right in each theme, and blocking and removing work from the right-click menu.
- [ ] Host can create/rename/delete voice channels.
- [ ] Channel switching does not reconnect the room socket.
- [ ] Audio is only delivered inside the current voice channel.
- [ ] Leave Voice keeps a persistent host room alive when enabled.
- [ ] Closing persistent host mode sends the server to tray without leaving the microphone live.
- [ ] Per-user volume/mute works locally (right-click a person in the sidebar).
- [ ] Sidebar shows each channel's members underneath it; Lobby lists people not in voice.
- [ ] Chat messages appear for everyone in the same channel and not in other channels.
- [ ] Someone joining a channel sees its recent chat history.
- [ ] The taskbar flashes for a new message while mæla is in the background.
- [ ] Host kick and session-ban controls work.
- [ ] Windows Firewall helper succeeds when approved.
- [ ] UPnP success and failure paths are both understandable.
- [ ] With speakers (no headset), the other side does not hear their own voice echoed back after a second or two of talking.
- [ ] Turning echo cancellation off in Settings takes effect immediately.
- [ ] `scripts\test_ci.cmd` passes.
- [ ] `VERSION` is bumped and `CHANGELOG.md` has a matching section (rename `## Unreleased` to `## X.Y.Z - Title`, or the release notes come out empty).
- [ ] After the release workflow runs, the release has both `Maela.exe` and `Maela.exe.sha256`.
- [ ] A copy of the previous version offers the update and restarts into the new version.
- [ ] First run shows Setup; the emoji download completes; Skip asks again next launch.
- [ ] Each theme looks right, including the Windows title bar colour (Windows 11).
- [ ] Reactions, the emoji picker and search work; colour emoji show in chat.
- [ ] Deafen silences everyone and shows the deafened icon to others; ping shows in the voice card.
- [ ] Sound effects play at a comfortable level and can be turned off.
- [ ] Typing on a keyboard near the mic is not heard by others (nothing sent while only typing).
- [ ] Closing the window sends mæla to the tray and keeps you in voice; the tray menu mutes,
      deafens, leaves and quits; the Settings toggle turns this off.
- [ ] Launching mæla a second time brings the running copy forward instead of opening another.
- [ ] Ending mæla from Task Manager and reopening it rejoins at once, in the same channel,
      without a duplicate of you in the member list.
- [ ] Quitting the host (tray > Stop room and quit) disconnects members within a second.
- [ ] With a frozen copy running, opening mæla again closes it and starts normally.
- [ ] One click on a channel opens its chat; double-click joins voice; Leave voice keeps you
      in the room and off the member list; Disconnect leaves; hosts get "Stop room".
- [ ] Hosts can create a chat-only channel; nobody can join it for voice; it survives re-hosting.
- [ ] Setting a profile picture and status shows on your card, in the sidebar and in chat for
      everyone; the card's volume and mute work; host Move/Kick/Ban work from the card.
- [ ] First-run setup shows "What mæla uses".
- [ ] Share screen: OpenH264 downloads after asking; a screen and a single window can be shared;
      the red bar shows viewers; Stop works; leaving voice stops sharing.
- [ ] Watch: LIVE badge, double-click and the card's Watch stream open the viewer; full screen
      works; the picture is smooth at 1080p60 between two PCs on different networks.
- [ ] The screen sharing notice appears until Don't show me again is on; Cancel stops; "always through the host" in Settings works.
- [ ] Settings > Licenses shows the MIT license and third-party notices.
- [ ] `package_release.cmd` produces the expected release ZIP.

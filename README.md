<p align="center">
  <img src="assets/MaelaLogo.png" alt="mæla" width="520">
</p>

# mæla

**mæla** is a lightweight self-hosted Windows voice-room app. One person hosts, friends join with a short room code, and the host relays voice directly without accounts or a permanent central service.

Current release: **0.13.4**. Open source under the MIT License.

## Highlights

- Two layouts: **Dynamic**, where rooms and friends float as glass orbs in one space you pan and zoom, sorted into circles (Spatial is its first theme), and **Lightweight**, the classic window. Nine colour schemes, Midnight by default
- Direct self-hosted UDP voice rooms
- 48 kHz mono Opus voice with packet-loss recovery
- Automatic free local UDP port selection
- Best-effort UPnP mapping and public IPv4 discovery
- Encrypted voice and chat (X25519 + AES-256 + HMAC-SHA256), with optional room passwords
- Room codes that verify the host's identity, such as `M-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`
- Opus voice that only sends when you speak (voice detection ignores typing and clicks): roughly 60 kbps per person talking, nothing while quiet
- Saved rooms with online/offline probing
- Friends with no accounts or server: add someone with their friend code, then chat, react and call directly
- Online, Away, Busy and Offline statuses; Offline turns friends off completely
- Voice channels with their own text chat, plus chat-only channels; one click opens a chat, double-click joins voice
- Screen sharing: whole screen or one window, up to 4K and 120 fps, direct to viewers, encoded and decoded on your graphics card when it can, viewed in a locked-down sandbox
- Profile cards with a picture, status line, banner, card deco and picture deco (pictures are shared as plain pixels, never files)
- Pictures in room and friend chats, rebuilt from their pixels on your PC so nothing hidden in the file is sent, plus YouTube link cards that load nothing until you ask
- Hosts can drag channels round a room and resize them, live for everyone and saved with the room
- Discord-style sidebar showing who is in which channel, who's speaking, muted or deafened
- Colour emoji, reactions, an emoji picker and message search
- Mute, deafen, live ping, automatic mic level and gentle sound effects
- A noise filter and a voice-only mic gate, so typing and clicks don't open your mic and background noise is turned down between words
- Fonts including OpenDyslexic and Atkinson Hyperlegible, text size and Reduce motion settings
- Leave voice but stay in the room to keep chatting; Disconnect to leave it
- Runs in the system tray when closed (mute, deafen, leave or quit from the tray icon); hosts can keep the room online there
- Per-user local volume and mute
- Built-in acoustic echo cancellation, so speakers work without a headset
- Automatic updates from GitHub Releases, verified by SHA-256
- Host kick / session ban / move-to-channel controls
- Input/output device selection
- Embedded app, taskbar, title-bar and tray branding
- Only one copy runs at a time; reopening mæla brings the running copy forward
- Rejoins instantly after a crash or lost connection, back in the same channel
- A single self-contained `.exe`: no installer, NuGet packages or runtime dependencies beyond Windows .NET Framework

## Join flow

The host clicks **Host**, waits for mæla to finish network setup, then copies the generated **room code**. Friends paste that code into **Join**.

A room code contains the host's address and a fingerprint of the host's encryption key. When you join, mæla checks the host's key against that fingerprint before sending anything, so nobody else can pretend to be the room. Codes are checksummed, so a mistyped code is caught straight away.

The host can also set a **room password** when hosting, or later from the room's right-click menu.

> **Upgrading from 0.6?** 0.7 is a fresh install: everyone needs the new version, and hosts need to share their new (longer) room codes. Old saved rooms are cleared.

## Friends

Friends stay off until you first choose a status other than Offline: click your name at the bottom left, then choose under **Appear as**. In the Dynamic layout your friends are in the Friends circle, next to your rooms; in Lightweight, click **Friends** at the top left, which swaps your rooms for your friends in the same row. Copy your friend code and send it to someone. When they paste it, you get a request, and neither of you can message or see the other's status until you accept.

Your encryption key is your account, so a new IP address doesn't end a friendship. Friends who are online get your new address automatically. If both addresses change while you're apart, either of you pastes the other's current code and you carry on. Messages travel directly between your PCs and wait, encrypted, on the sender's PC until you're both online.

Without a server, friends can't always connect: at least one side's router has to accept incoming connections, so two people behind CGNAT may not reach each other. Use **Backup and restore** in Friends to keep your account safe, because nothing else can recover it after a Windows reinstall.

## Text chat

Every voice channel has a text chat. Join a channel and its chat fills the main window; anything you type there is shared with everyone in that channel. People who join later get the recent history (the host keeps the last 200 messages per channel while the room is running).

Chat travels over the same direct connection as voice. Messages are numbered by the host and re-sent automatically if a packet is lost, so everyone sees the same messages in the same order.

**Pictures** can be sent in room channels and friend chats with the picture button, or pasted with Ctrl+V (screenshots too). Your PC redraws each picture from its pixels, at up to 1920 pixels across, so a file's hidden details (location, camera, its name) are never sent, and everyone else only unpacks plain pixels, never running an image decoder on someone else's file. In rooms the host checks each picture before posting it; friend chat pictures come straight from your friend and are kept encrypted with the chat. People on older versions see "Sent a picture. Update mæla to see it." instead.

**YouTube links** show as a card. Play opens the video in your browser, and nothing is loaded from YouTube unless you click Show preview.

## Downloads and dependencies

mæla only uses open-source components:

- **Opus codec (Concentus)** is fetched at build time from its official source and every file
  is checked against pinned SHA-256 hashes.
- **Fonts** (Outfit, OpenDyslexic, Atkinson Hyperlegible, Lexend and Space Grotesk, all under the
  SIL Open Font License) are stored in `assets/fonts` and built into `Maela.exe`, so they're never
  downloaded. Each is checked against its SHA-256 hash before use.
- **Colour emoji (Twemoji, CC-BY 4.0)** are fetched on first run from the official Twemoji
  project and checked the same way. If they're missing or damaged, mæla re-fetches them on
  launch; **Settings → Downloads → Repair** does it on demand. Without them, emoji simply show
  as text.

See `deps/README.md` for the full list and how pinned versions are updated.

## Sound effects

Sounds are generated by a small synth inside mæla, so there are no audio files at all. They
play through your chosen output device at your output volume, and the echo canceller knows
about them, so your mic doesn't send them to the room. Turn them off in **Settings → Voice &
audio**.

## Echo cancellation

mæla removes your speakers' sound from your microphone before it is sent, so the people you talk to don't hear themselves echoed back. It is on by default and works without configuration:

1. It measures the delay between audio leaving your speakers and reaching your microphone.
2. An adaptive filter learns how your room echoes and subtracts it, usually within a second or two of someone talking.
3. A suppressor removes any echo that remains.

It costs 10 ms of latency and a few percent of one CPU core. Headset users can turn it off in **Settings → Voice & audio**. Keep your own speaker volume at a normal level: very loud or distorting speakers are harder to cancel.

## Updates

mæla checks this repository's GitHub Releases shortly after launch and every six hours while it is running. When a newer version is published it shows the release notes and offers **Update now**, **Later** or **Skip this version**. Updating downloads `Maela.exe`, verifies it against the release's `Maela.exe.sha256` and its embedded version number, replaces the running executable and restarts.

Automatic checks can be turned off, or run on demand, in **Settings → Updates**. mæla needs to be able to write to its own folder to update itself; if it lives somewhere protected such as `Program Files`, it will offer to open the download page instead.

### Publishing an update

1. Bump `VERSION` (for example `0.7.0` → `0.7.1`) and add a matching `## 0.7.1` section to `CHANGELOG.md`.
2. Push to `main`.

The **Release** workflow builds the exe, creates the `v0.7.1` tag and GitHub Release, and attaches `Maela.exe` and `Maela.exe.sha256` with the CHANGELOG section as release notes. Running copies of mæla will pick it up on their next check. Pushing a `v*` tag by hand also works, as long as it matches `VERSION`.

## Build on Windows

Double-click:

```text
build_Maela.cmd
```

The script uses the C# compiler bundled with Windows .NET Framework and writes:

```text
dist\Maela.exe
```

No Visual Studio, NuGet, or package restore is required.

## Repository layout

```text
assets/                  Logo, mark and Windows icon
assets/fonts/            Built-in fonts (SIL Open Font License)
src/Maela.cs             Application, UI and networking
src/SpatialWindow.cs     The Dynamic layout (WPF, on its own thread)
src/MainForm.Spatial.cs  Connects the Dynamic layout to the app
src/CardEffects.cs       Banners, card decos and picture decos
src/ChatPicture.cs       Chat pictures: rebuilding, checking and unpacking
src/VoipEngine.ChatPictures.cs, FriendService.Pictures.cs  Sending pictures in rooms and friend chats
src/VoipEngine.ProfileExtras.cs  Banners and decos on the wire
src/VoipEngine.ChannelLayout.cs  Where hosts put channels
src/ReadingFonts.cs      Built-in fonts
src/EchoCanceller.cs     Acoustic echo canceller
src/Updater.cs           GitHub Releases auto-updater
src/VoipEngine.Chat.cs   Chat protocol
src/VoipEngine.Security.cs  Encrypted handshake and sessions
src/Crypto.cs            X25519, HKDF, packet encryption
src/MainForm*.cs         Main window, layout and chat view
src/Theme.cs, Controls.cs, Icons.cs, Dialogs.cs  Themes, custom controls, vector icons, dialogs
src/Assets.cs            Verified downloads (emoji pack)
src/NoiseSuppressor.cs   Background-noise filter
src/SoundFx.cs           Built-in sound-effect synth
src/VoiceCodec.cs        Opus encoding, silence detection, loss recovery
src/VoicingDetector.cs   Tells a voice (pitched) from typing, clicks and bumps
deps/                    Pinned hashes for open-source dependencies (fetched, never stored here)
src/MainForm.Chat.cs     Chat view
tests/                   Crypto, security, voice, chat, picture, profile, storage, echo, noise, sound and download tests
scripts/build_ci.cmd      Non-interactive CI build
scripts/test_ci.cmd       Build and run the tests
.github/workflows/        GitHub build/release automation
build_Maela.cmd           Local Windows build
package_release.cmd       Local release ZIP builder
VERSION                   Current version
CHANGELOG.md              Release history
docs/NETWORKING.md        Networking/code design notes
docs/RELEASE_CHECKLIST.md Release smoke-test checklist
```

## Networking

Only the host needs to be reachable from the Internet. mæla attempts UPnP automatically. If the host router does not support or allow UPnP, manual forwarding or an overlay network such as Tailscale may still be required.

The program currently uses IPv4 for room codes and voice transport.

## Security status

All voice, chat and control traffic is encrypted and authenticated, and the host's identity is verified through the room code. The host relays the room, so **the host can hear and read everything in it**, just like any server. Room codes reveal the host's IP address, so share them only with people you intend to invite.

See `SECURITY.md` for the full threat model.

See `docs/NETWORKING.md` for more detail.

## License

mæla is open source under the [MIT License](LICENSE). Copyright (c) 2026 BrostrodamusTheWeez0rd.

mæla includes the Opus codec via [Concentus](https://github.com/lostromb/concentus) under the BSD 3-clause license, fonts under the SIL Open Font License and Twemoji under CC-BY 4.0. See `THIRD_PARTY_NOTICES.txt` (also viewable from Settings).

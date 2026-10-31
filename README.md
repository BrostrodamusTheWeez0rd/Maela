<p align="center">
  <img src="assets/MaelaLogo.png" alt="mæla" width="520">
</p>

# mæla

**mæla** is a lightweight self-hosted Windows voice-room app. One person hosts, friends join with a short room code, and the host relays voice directly without accounts or a permanent central service.

Current release: **0.7.0**.

## Highlights

- Native Windows desktop UI (WinForms)
- Direct self-hosted UDP voice rooms
- 48 kHz / 16-bit mono voice audio
- Automatic free local UDP port selection
- Best-effort UPnP mapping and public IPv4 discovery
- Short checksummed room codes such as `M-XXXX-XXXX-XXXXX`
- Saved rooms with online/offline probing
- Multiple voice channels inside a room
- Lobby state with no voice transmission
- Persistent-host mode that can keep the room server alive in the tray
- Per-user local volume and mute
- Built-in acoustic echo cancellation, so speakers work without a headset
- Automatic updates from GitHub Releases, verified by SHA-256
- Host kick / session ban / move-to-channel controls
- Input/output device selection
- Embedded app, taskbar, title-bar and tray branding
- No NuGet packages and no external runtime dependencies beyond Windows .NET Framework

## Join flow

The host clicks **Host**, waits for mæla to finish network setup, then copies the generated **room code**. Friends paste that code into **Join**.

Room codes intentionally keep raw IP addresses and ports out of the normal UI. Because mæla has no rendezvous server, a room code still encodes the network endpoint needed to reach the host. The code is obfuscated and checksummed for usability; it is **not** an anonymity or encryption mechanism.

## Echo cancellation

mæla removes your speakers' sound from your microphone before it is sent, so the people you talk to don't hear themselves echoed back. It is on by default and works without configuration:

1. It measures the delay between audio leaving your speakers and reaching your microphone.
2. An adaptive filter learns how your room echoes and subtracts it, usually within a second or two of someone talking.
3. A suppressor removes any echo that remains.

It costs 10 ms of latency and a few percent of one CPU core. Headset users can turn it off in **Settings → Voice & Audio**. Keep your own speaker volume at a normal level: very loud or distorting speakers are harder to cancel.

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
src/Maela.cs             Application, UI and networking
src/EchoCanceller.cs     Acoustic echo canceller
src/Updater.cs           GitHub Releases auto-updater
tests/                   Echo canceller regression test
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

This project is still an early lightweight voice client. Voice traffic is **not yet end-to-end encrypted**. Treat room codes like connection details and share them only with people you intend to invite.

See `docs/NETWORKING.md` for more detail.

## License

No open-source license has been selected yet. See `LICENSE`.

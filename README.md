<p align="center">
  <img src="assets/MaelaLogo.png" alt="mæla" width="520">
</p>

# mæla

**mæla** is a lightweight self-hosted Windows voice-room app. One person hosts, friends join with a short room code, and the host relays voice directly without accounts or a permanent central service.

Current release baseline: **0.6.0**.

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
- Host kick / session ban / move-to-channel controls
- Input/output device selection
- Embedded app, taskbar, title-bar and tray branding
- No NuGet packages and no external runtime dependencies beyond Windows .NET Framework

## Join flow

The host clicks **Host**, waits for mæla to finish network setup, then copies the generated **room code**. Friends paste that code into **Join**.

Room codes intentionally keep raw IP addresses and ports out of the normal UI. Because mæla has no rendezvous server, a room code still encodes the network endpoint needed to reach the host. The code is obfuscated and checksummed for usability; it is **not** an anonymity or encryption mechanism.

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
src/Maela.cs             Application source
scripts/build_ci.cmd      Non-interactive CI build
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

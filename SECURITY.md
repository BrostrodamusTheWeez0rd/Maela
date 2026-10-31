# Security

mæla is currently an early self-hosted voice client.

## Important current limitations

- Voice, chat and control packets are not end-to-end encrypted. Chat history lives in the host's memory while the room runs.
- Room codes hide raw endpoint details from the normal interface, but are not cryptographic secrets.
- Hosting exposes a UDP service to invited peers and may create a UPnP router mapping.

## Automatic updates

- Updates are only downloaded over HTTPS from this repository's own GitHub Releases.
- The download must match the published `Maela.exe.sha256` and report the expected version, or it is discarded.
- The checksum protects against corrupted or tampered downloads in transit. It is hosted alongside the exe, so it does **not** protect against someone who gains write access to the repository or its releases. Protect the GitHub account with two-factor authentication. Code-signing releases would be the next step.

Do not market the current build as private or secure against a determined network observer.

For a future public release, security-sensitive changes should include protocol authentication, encryption, replay protection and a documented threat model.

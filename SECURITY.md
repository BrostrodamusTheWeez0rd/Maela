# Security

mæla is currently an early self-hosted voice client.

## Important current limitations

- Voice and control packets are not end-to-end encrypted.
- Room codes hide raw endpoint details from the normal interface, but are not cryptographic secrets.
- Hosting exposes a UDP service to invited peers and may create a UPnP router mapping.

Do not market the current build as private or secure against a determined network observer.

For a future public release, security-sensitive changes should include protocol authentication, encryption, replay protection and a documented threat model.

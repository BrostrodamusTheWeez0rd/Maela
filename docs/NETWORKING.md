# Networking and room codes

mæla is currently serverless: the host's machine is the room server and relays audio between room members.

## Room-code design

A displayed room code has this form:

```text
M-XXXX-XXXX-XXXXX
```

The code contains a versioned IPv4 endpoint and a checksum. The bytes are transformed and Base32-encoded so normal users never need to see or type an IP address or UDP port.

The checksum rejects most accidental edits and mistyped codes before any network connection is attempted.

### Privacy limitation

The room code is an **endpoint encoding, not encryption**. A client must be able to recover the endpoint locally because no central rendezvous service exists. Someone deliberately reverse-engineering the program can therefore recover the endpoint from a valid code.

A future privacy-oriented code system would require a rendezvous/lookup service where the short code resolves to an endpoint server-side.

## Hosting

1. Windows selects or reuses a local UDP port.
2. mæla attempts UPnP mapping on the router.
3. mæla fetches the public IPv4 address.
4. mæla creates the room code from the externally reachable endpoint.
5. If no public address is available, mæla can fall back to a LAN-only room code.

## NAT limitations

UPnP can fail when:

- UPnP is disabled on the router.
- The network uses carrier-grade NAT (CGNAT).
- The host is behind multiple NAT layers.
- A firewall blocks UDP traffic.

In those cases manual forwarding or a private overlay network can be used.

## Current transport security

Voice/control traffic is not yet end-to-end encrypted. Encryption should be added before treating mæla as a security-sensitive communications product.

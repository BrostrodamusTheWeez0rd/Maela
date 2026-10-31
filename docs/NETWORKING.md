# Networking, room codes and the wire protocol

mæla is serverless: the host's PC is the room server. Every member talks only to the host,
which relays voice and chat to the others in the same channel.

## Room codes

```text
M-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX
```

A code is 19 bytes, lightly scrambled and Base32-encoded (no `0`, `1`, `I` or `O`):

| Bytes | Field |
|---|---|
| 1 | Code version (2) |
| 4 | Host IPv4 address |
| 2 | Host UDP port |
| 10 | Fingerprint of the host's X25519 public key |
| 2 | CRC-16 checksum (every single-character typo is rejected) |

The fingerprint is what makes joining safe: the client refuses to talk to any host whose key
doesn't hash to it. The endpoint is not secret; anyone with the code can recover it.

Saved hosted rooms keep the same key, so their code only changes if the public IP or port
changes. Codes from mæla 0.6 and earlier have no fingerprint and are refused with a message
asking for a new code.

## Hosting

1. Windows selects or reuses a local UDP port.
2. mæla attempts UPnP mapping on the router.
3. mæla fetches the public IPv4 address.
4. mæla creates the room code from the reachable endpoint and the room's key.
5. If no public address is available, mæla falls back to a LAN-only room code.

UPnP can fail when it is disabled on the router, behind carrier-grade NAT (CGNAT), behind
several NAT layers, or when a firewall blocks UDP. Manual port forwarding or a private overlay
network (for example Tailscale) works in those cases.

## Wire protocol (version 6)

Every datagram starts with a 6-byte outer header: magic `ROV2` (4) | version 6 | type.

| Type | Direction | Encrypted | Contents |
|---|---|---|---|
| `0x40` HELLO | client → host | no | client ephemeral X25519 key, padded to 160 bytes |
| `0x41` HELLO_REPLY | host → client | no | host static key, host ephemeral key (70 bytes, smaller than HELLO) |
| `0x42` DATA | both | **yes** | counter (8) · AES-256-CTR ciphertext · HMAC-SHA256 tag (16) |
| `0x43` PROBE / `0x44` PROBE_REPLY | anyone ↔ host | no | nothing (just "online") |

### Handshake (Noise NK)

1. The client sends HELLO with a fresh ephemeral key `Ec`.
2. The host replies with its static key `S` and a fresh ephemeral key `Eh`.
3. The client checks `S` against the room code's fingerprint, then both sides compute
   `DH(ec, S)` and `DH(ec, Eh)` and derive separate client→host and host→client keys with
   HKDF-SHA256, bound to a hash of `Ec`, `S` and `Eh`.
4. The client's first DATA packet is JOIN (display name and room password). The host accepts or
   rejects it inside the encrypted channel.

Hosts limit handshakes to 10 per IP per 10 seconds and lock an IP out for a minute after five
wrong passwords.

### Inside DATA

Decrypted DATA contains the inner packets: join, welcome, peer list, keepalive, leave, kick,
reject, audio, channel switch and the chat packets. Receivers drop anything that fails
authentication, arrives twice, or is older than the 1,024-packet replay window.

## Voice

- Opus at 48 kHz mono, 20 ms frames, 32 kbps VBR, with in-band forward error correction.
- Silence detection with an adaptive noise floor: nothing is sent while you are quiet. A
  400 ms tail and one frame of pre-roll keep word starts and ends intact.
- Receivers rebuild one or two lost packets from FEC or concealment, and keep a 40 ms jitter
  buffer at the start of each burst of speech, capped at 200 ms of queued audio.
- About 55–65 kbps per active speaker including all headers, versus ~800 kbps for the old
  uncompressed audio. Silence costs only keepalives.

## Reliability

- Channel switches are retried until the host confirms them.
- The host re-sends the member list every 3 seconds.
- Chat messages are numbered per channel; clients re-request gaps on every keepalive.

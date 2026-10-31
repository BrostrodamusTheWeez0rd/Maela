# Security

mæla encrypts all room traffic: voice, chat, member lists and control messages. This page
describes what that protects against and what it doesn't. Protocol details are in
`docs/NETWORKING.md`.

## What is protected

| Threat | Protected? | How |
|---|---|---|
| Someone on the network path (public Wi-Fi, ISP, router) listening to voice or reading chat | **Yes** | Every packet is encrypted (AES-256-CTR) after an X25519 key exchange. |
| Someone injecting fake audio, chat or commands | **Yes** | Every packet is authenticated (HMAC-SHA256); forged or modified packets are dropped. |
| Replaying captured packets | **Yes** | Per-packet counters with a replay window. |
| A fake host pretending to be your friend's room (man in the middle) | **Yes** | The room code contains a fingerprint of the host's key; mæla refuses any host that doesn't match it. |
| Recorded traffic decrypted later if a key leaks | **Yes** (forward secrecy) | Each connection uses fresh one-time keys. |
| Uninvited people who somehow get the room code | **Optional** | Hosts can set a room password. Five wrong passwords lock that IP out for a minute. |
| Someone guessing room codes | **Yes** | Codes include an 80-bit key fingerprint; guessing one is not feasible. |

## What is not protected

- **The host can hear and read everything in their room.** Audio and chat are relayed through
  the host's PC, which decrypts and re-encrypts them. This is the same trust you place in
  any server (Discord included). Only join rooms run by people you trust.
- **Anyone with the room code can find the host's IP address.** The code has to contain it,
  because there is no central server to look it up. Treat room codes like a phone number.
- **Anyone can tell a mæla room is running** at an address if they probe it. The probe reply
  contains nothing else: no room name, no member list, no count.
- **Display names are chosen freely.** mæla doesn't verify identities; someone with the code
  (and password) can call themselves anything.
- **A compromised PC.** If malware runs on the host's or a member's computer, it can listen in.

## Keys and secrets on disk

- Each hosted saved room has a long-term X25519 key. It is what keeps the room's code the same
  every time you host. It is stored in `%AppData%\Maela\rooms-v2.txt`, encrypted with Windows
  DPAPI so only your Windows account can read it.
- Room passwords (yours as a host, or the one you were given) are stored the same way.
- If a host key is ever exposed, delete the room and host it again to get a new key and code.

## Downloaded assets

- The colour emoji are downloaded on first run from the official Twemoji repository only
  (`raw.githubusercontent.com/jdecked/twemoji`, pinned version), over HTTPS.
- Every file must match a SHA-256 hash built into Maela.exe or nothing is installed; any other
  source is refused. Files are checked again on every launch, and a file that changes on disk
  is ignored.
- These downloads are images only. The only code mæla ever downloads is its own update (below).

## Automatic updates

- Updates are only downloaded over HTTPS from this repository's own GitHub Releases.
- The download must match the published `Maela.exe.sha256` and report the expected version, or it is discarded.
- The checksum protects against corrupted or tampered downloads in transit. It is hosted alongside the exe, so it does **not** protect against someone who gains write access to the repository or its releases. Protect the GitHub account with two-factor authentication. Code-signing releases would be the next step.

## Implementation notes

- Cryptography is built from standard primitives only: X25519 (RFC 7748), HKDF-SHA256
  (RFC 5869), AES-256 (the .NET implementation) in CTR mode, HMAC-SHA256 (encrypt-then-MAC).
  The handshake follows the Noise protocol's NK pattern. No custom ciphers.
- X25519 is a port of the TweetNaCl reference and is checked against the RFC 7748 test vectors,
  including the 1,000-iteration test. HKDF is checked against RFC 5869.
- `tests/SecurityTests.cs` attacks a real host over the network (eavesdropping, wrong keys,
  wrong passwords, forged, bit-flipped and replayed packets) on every CI run.
- This code has not had an independent security audit.

## Reporting a problem

Please report security issues privately to the repository owner rather than in a public issue.

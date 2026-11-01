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

## What other people in a room can and can't do

Encryption keeps outsiders out. This section is about the people you let in, including
someone running a modified copy of mæla.

**Nothing in a room can make your mæla act on its own.** Nobody can start your screen share,
open, save or run files on your PC, change your settings, or turn your mic on. Every action
needs a click from you. Other members can only send data, which mæla checks and then shows
or plays; it is never treated as a file or a command.

| Someone in your room can… | Can they? | Notes |
|---|---|---|
| Hear you, read your messages in channels they're in or have open | Yes | That's what a room is for. |
| See your display name, profile picture and status | Yes | Only what you chose to share. |
| See your IP address | **Host**, plus the people you share a screen with or watch | Members connect to the host. Screen sharing connects sharer and viewer directly (mæla explains this first); turn that off in Settings to always go through the host. |
| Watch your screen | Only when you choose to share, and only people in your voice channel who click Watch | You pick a screen or a single window; a red bar shows you're live. |
| Hear your PC's sound | Only those same viewers, when you leave Share sound on | Sharing a screen sends what your programs play, except mæla's own sound. Sharing a window sends only that program's sound. Encrypted like the video. |
| Break into your PC with a crafted video stream | Contained, with one exception | Streams are decoded in a separate viewer at Low integrity inside a restricted job: no writing to your files, no starting programs, no clipboard. **Exception:** at 1080p and above, by default, decoding happens on your graphics card, and the sandbox protects less against a bug in the graphics driver. mæla checks the stream first and allows only the kind of video its own encoders make. To keep all decoding on the processor, choose Never under Settings > App. |
| See your Windows username, file names or folders | No | Never sent. Your display name starts as your Windows account name; change it in Settings. |
| Get the location or camera details from your profile picture | No | Your PC turns the picture into an 80×80 pixel copy; only those pixels are sent. |
| Send you a booby-trapped image | No | Pictures arrive as plain pixels. mæla never runs an image decoder on anything another person sent. |
| Crash or freeze your mæla with bad data | Designed not to | Every message type has size limits and strict checks; bad data is dropped. `tests/ProfileTests.cs` and `tests/SecurityTests.cs` send random, oversized, inconsistent and "zip bomb" data at a real room on every build. |
| Flood the room | Limited | Chat and profile changes are rate-limited per person; the host can kick or ban. |
| Pretend to be the host | No | The host's key is checked against the room code. |
| Host your saved room themselves | No | Only the PC holding that room's key can host it, and that key never leaves it. |

The **host** can additionally move, kick and ban people, create, rename and delete channels,
and (as above) hear and read everything in their room.

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
- First-run setup lists everything mæla uses or downloads, with licenses, before anything is
  fetched. Nothing else is downloaded without asking.

## Automatic updates

- Updates are only downloaded over HTTPS from this repository's own GitHub Releases.
- The download must match the published `Maela.exe.sha256` and report the expected version, or it is discarded.
- The checksum protects against corrupted or tampered downloads in transit. It is hosted alongside the exe, so it does **not** protect against someone who gains write access to the repository or its releases.
  
## Implementation notes

- Cryptography is built from standard primitives only: X25519 (RFC 7748), HKDF-SHA256
  (RFC 5869), AES-256 (the .NET implementation) in CTR mode, HMAC-SHA256 (encrypt-then-MAC).
  The handshake follows the Noise protocol's NK pattern. No custom ciphers.
- X25519 is a port of the TweetNaCl reference and is checked against the RFC 7748 test vectors,
  including the 1,000-iteration test. HKDF is checked against RFC 5869.
- `tests/SecurityTests.cs` attacks a real host over the network (eavesdropping, wrong keys,
  wrong passwords, forged, bit-flipped and replayed packets) on every CI run.
- `tests/StreamTests.cs` checks screen-share keys, direct and relayed paths, loss recovery,
  and that the host relays only between people it introduced. `tests/CodecTests.cs` checks
  OpenH264's pinned hashes, end-to-end video, and that the viewer sandbox really runs at Low
  integrity and can't write files or start programs. `tests/StreamPipelineTests.cs` feeds the
  stream check forbidden formats and 50,000 random inputs.
- `tests/ProfileTests.cs` plays a hostile member: 20,000 random profiles, decompression bombs,
  oversized and inconsistent chunks, and floods. None may be stored or crash anyone.
- This code has not had an independent security audit.

## Reporting a problem

Please report security issues privately to the repository owner rather than in a public issue.

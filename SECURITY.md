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

## Friends

Friends stay off until you first choose a status other than Offline. While you're online, mæla
runs a small listener on your PC and asks your router to open a port for it.

| Threat | Protected? | How |
|---|---|---|
| Someone messaging you without being accepted | **Yes** | Your list decides, and only your own click on Accept changes it. A modified copy can't accept for you: your PC never takes anyone's word for it. `tests/FriendTests.cs` tries it. |
| Someone pretending to be your friend | **Yes** | Your key is your account. Both sides prove they hold their key on every connection (the Noise XX pattern), so copying a name or code isn't enough. |
| A fake listener at your friend's address | **Yes** | A friend code carries a 128-bit fingerprint of the person's key. Anything else is refused. |
| A friend reading your chat with someone else | **Yes** | Each chat has its own keys and file, and the listener has no way to pass anything between people. Friends can't see each other. |
| Someone scanning the internet for mæla | **Yes** | Without your code, the listener doesn't reply at all. |
| Reading your messages on the network | **Yes** | Direct and end-to-end encrypted, with forward secrecy. Nothing else relays or stores them. |

What it doesn't protect:

- **Anyone with your friend code can find your IP address and port.** There's no server to look it
  up, so the code has to contain it. They can also send you a request. Share it like a phone
  number.
- **Friends can see your IP address and status,** because your PCs talk directly.
- **Your PC is a little more visible.** While online, your listener sends a small encrypted check
  to each friend regularly. Someone watching your network can see that traffic and who it goes
  to, but not what's in it.
- **If your router can't give mæla your public address,** mæla asks `api.ipify.org`, as it does
  for hosting a room. That service sees your IP address. After that, mæla only asks your router.
- **Offline really is off, but it looks like anything else.** Friends can't tell Offline from a
  powered-off PC or a changed address. A message you're sent while Offline stays on the sender's
  PC, so nothing arrives unless you're both online at the same time.
- **Without a backup, losing your Windows account's keys loses your friend account.** There's no
  server to recover it from. Friends would need to add your new code.
- **A backup is only as strong as its password.** The Friends window can save your key and friends
  list to a file, encrypted with a key derived from your password (PBKDF2-HMAC-SHA256, 600,000
  rounds, then AES-256-CTR and HMAC-SHA256). Anyone who gets the file can try passwords on their own
  hardware, and one who guesses right can impersonate you to your friends and learn their addresses.
  mæla can't recover a forgotten password. Chats and unsent messages aren't in the backup.

## Keys and secrets on disk

- Your saved rooms are stored in `%AppData%\Maela\rooms-v3.dat`. The whole file is encrypted with
  Windows DPAPI, including names, channels and room codes (which contain the host's address). The
  key comes from your Windows account, not from mæla's source code, so a copy of the file can't be
  read elsewhere. Programs running under your own Windows login can still ask Windows to decrypt it.
- Each hosted saved room has a long-term X25519 key. It is what keeps the room's code the same
  every time you host. It is encrypted a second time inside that file.
- Room passwords (yours as a host, or the one you were given) are stored the same way.
- If a host key is ever exposed, delete the room and host it again to get a new key and code.
- Copies of the older `rooms-v2.txt`, which kept names and room codes in readable form, are
  converted and deleted on first launch. Deleting doesn't scrub a file from a drive or from backups.
- Because only your Windows account can decrypt the file, saved rooms don't move to another PC or
  survive reinstalling Windows. mæla keeps a file it can't open as `rooms-v3.unreadable` and says so.
- Your friend key, friends list, their last known addresses and queued messages are in
  `friends-v1.dat`, and each chat is in its own `friend-<id>.dat`. All are encrypted the same way.
- `settings.txt` is not encrypted. It holds preferences such as your display name and status.

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

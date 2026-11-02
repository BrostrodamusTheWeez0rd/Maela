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
4. The client's first DATA packet is JOIN (display name and password field). The field holds
   `proof1:` and a Base64 HMAC-SHA256 of the handshake hash, keyed by the SHA-256 of the room
   password, so it's useless on any other connection and the password never leaves the client.
   Hosts also accept the plain password from older copies. Older hosts don't understand proofs, so
   joining a password room needs the host to have updated. The host accepts or rejects JOIN inside
   the encrypted channel.

Hosts limit handshakes to 10 per IP per 10 seconds and lock an IP out for a minute after five
wrong passwords.

### Inside DATA

Decrypted DATA contains the inner packets: join, welcome, peer list, keepalive, leave, kick,
reject, audio, channel switch and the chat packets. Receivers drop anything that fails
authentication, arrives twice, or is older than the 1,024-packet replay window.

### Channels, open chats and profiles

- Each member has a voice channel (0 = not in voice) and an *open* channel whose chat they
  are reading. CHAT_SYNC tells the host which channel is open; the host sends that channel's
  history and new messages, and accepts posts only for the channel you are in or have open.
- Chat-only channels are listed in a peer-list extension section (tag 1). Nobody can switch
  voice into them.
- Profiles: PROFILE_PUT (client to host, chunked, resent until PROFILE_ACK), PROFILE_GET and
  PROFILE_DATA (host to client, chunked). The peer list carries (member, profile version) in
  extension section 2, so clients fetch only what changed. A profile is at most 16 KB: a status
  line of up to 60 characters and 80×80 RGB565 pixels compressed with Deflate. Receivers
  inflate to exactly 12,800 bytes or reject it; no image decoder runs on received data.
- Older versions stop reading the peer list after the member entries, or at a section they don't
  know, so extension sections are ignored by them.
- The room's name is in peer-list section 4 (length byte, UTF-8, at most 40 characters), so everyone
  sees the name the host chose. Room codes don't carry it. A room joined from a host too old to send
  it stays "Voice Room".

### Screen sharing

- STREAM_ANNOUNCE (client to host) says you're live (size and frame rate) or stopped; the host
  lists live streams in peer-list section 3. Only members in a voice channel can go live.
- STREAM_WATCH (viewer to host) asks to watch. The host checks both are in the same voice
  channel (at most 8 viewers), creates a random 32-byte key for the pair and sends STREAM_PEER
  to both over their encrypted sessions, with each other's public and LAN addresses.
- Both sides send sealed "punch" packets (outer type 0x45) at each other for 6 s; the first
  one to arrive opens the direct path. Until then, or if either side disallows direct
  connections, sealed media goes inside RELAY_MEDIA through the host, which forwards only
  between pairs it introduced, within 16 MB/s per member.
- Media is sealed with keys derived from the pair key (HKDF-SHA256, one per direction):
  AES-256-CTR + HMAC-SHA256 with a replay window. Video frames are H.264, split into 1,100-byte
  fragments; viewers rebuild them, ask for a keyframe after any loss and report received
  bitrate and loss every 0.5 s.
- Video also has three optional media types; older copies ignore them and still exchange plain
  video. PARITY (4): XOR of a group of fragments, sent only after a viewer reports loss, more
  of it at higher loss; it rebuilds one lost fragment per group. RESEND (5): a viewer asks for
  fragments again, 15 ms after a later frame shows they're missing, at most twice per frame and
  2,000 fragments a second per viewer. PROBE (6) and its reply (7): the sharer's ping, 4 times a
  second, whose rise above its usual value warns of congestion before anything is lost.
- The viewer draws with Direct3D 11 (GDI if that fails). It decodes with Windows' H.264 decoder
  on the graphics card at 1080p and above by default (Settings > App), after `H264StreamCheck`
  allows only 8-bit 4:2:0 progressive baseline, main or high within the size limits, and strips
  SEI. A refusal or any decoder error switches that stream to OpenH264 for good, replaying from the
  last keyframe.
- AUDIO (media type 8, ignored by older copies): sequence (4) and one 20 ms stereo Opus frame at
  up to 128 kbps. Goes straight to the socket, unpaced, and isn't sent during silence. Captured
  with Windows' per-process loopback, leaving out mæla's process tree, or only the shared window's
  process tree. It isn't timed against the video: both are kept low-latency instead.
- Viewers hand frames over strictly in order. Frames after a damaged one wait up to 120 ms for
  the repair; if none comes (or a newer keyframe arrives) it's skipped and the viewer waits for
  a keyframe. Repaired fragments still count as lost in reports, so the bitrate reacts.
- The sharer lowers the bitrate on loss over 3% (by 15%, or 30% over 10%) or a 60 ms rise in
  ping (by 10%), at most once a second, and climbs back 5% every 0.5 s after 3 quiet seconds. A
  screen that simply isn't sending much doesn't lower it. Keyframes come every 8 s, or on
  request at most every 0.5 s.
- Sending is paced (`SendScheduler`). Voice and control packets go straight to the socket;
  video and relayed video leave evenly from their own thread and are encrypted as they go. The
  rate is 2.5x the recent average, or enough to clear the backlog within 66 ms. A waiting
  frame is dropped when a new keyframe replaces it, or (with the frames that depend on it) after
  waiting 300 ms. When a viewer reports over 3% loss and ping has risen over 30 ms, a queue is
  filling, so the sharer limits video to 1.5x what it's getting through, then raises the limit by
  25% every 0.5 s once that stops. Loss without rising ping (bad Wi-Fi) isn't limited this way.

### Leaving and coming back

- Quitting sends LEAVE three times (UDP can drop one); mæla also sends it if it crashes.
- A host that quits sends LEAVE to every member; members disconnect at once and keep retrying
  until the room is back.
- If a client vanishes without LEAVE (Task Manager, power cut, lost Wi-Fi), the host drops it
  after 10 seconds without packets.
- If the same person comes back before then (a JOIN from the same IP address with the same
  display name, on a new or the same port), the host replaces the stale entry at once instead
  of listing them twice.
- WELCOME carries the channel to enter (4 bytes): the member's previous channel when they are
  coming back, otherwise #1. Older clients ignore it and join #1.

## Friends

Friends don't use rooms or the wire protocol above. Each person who appears online runs a small
listener on its own UDP port. It has no members, channels or relay.

```text
F-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
```

A friend code is 25 bytes, scrambled and Base32-encoded: version (1), IPv4 address (4), UDP port
(2), a fingerprint of the person's X25519 key (16) and CRC-16 (2). The key is the account; the
address only says where they were.

Every datagram starts with magic `MFR1` (4) and a type (1).

| Type | Direction | Contents |
|---|---|---|
| 1 HELLO | initiator → responder | session id (4), ephemeral key (32), gate (16), random padding to 160 bytes |
| 2 REPLY | responder → initiator | both session ids (8), ephemeral key (32), encrypted static key (56) |
| 3 FINISH | initiator → responder | session id (4), encrypted static key (56) |
| 4 DATA | both | receiver's session id (4), counter (8), AES-256-CTR ciphertext, HMAC-SHA256 tag (16) |

- The handshake is the Noise XX pattern with X25519 and HKDF-SHA256. The initiator checks the
  responder's key against the code's fingerprint; the responder looks the initiator's key up in
  its own list.
- The gate is an HMAC-SHA256 of the ephemeral key, keyed by the responder's fingerprint. Without
  it there is no reply, so the listener can't be discovered by scanning. Listeners also limit
  handshakes to 10 per IP per 10 seconds.
- Inside DATA: READY, PING (status, name, current code), REQUEST and ACCEPT (name, code), MESSAGE
  (number, time, message id, reaction target, text), ACK and BYE. A MESSAGE with a reaction target
  is a reaction: its text must be a known emoji and the target a real message, or it's dropped.
  Sending the same one again takes it away.
- A stranger's session accepts only REQUEST and never receives PING. ACCEPT counts only from the
  exact key you sent a request to. If two people request each other, they become friends, because
  each has already chosen the other's key.
- Messages are numbered per direction, at most 400 characters, accepted strictly in order, resent
  every second until acknowledged, and kept encrypted on the sender's PC until then. Receivers
  take at most 30 messages per friend per 10 seconds; anything over is simply resent later.
- PING goes to each friend every 15 seconds, which also keeps the path through a router open. A
  friend is offline after BYE or 45 seconds of silence. Listeners try offline friends every 20
  seconds, at their announced address and the one they last sent from.
- A changed address reaches friends in the next PING. If both people's addresses change while
  they're apart, nothing can find them: either of them pastes the other's current code, and the
  connection updates both. mæla deliberately has no server or DHT lookup.
- CALL (kind, call id, details) rings a friend: INVITE (can I host?), ACCEPT (should you host?),
  ROOM_READY (room code, one-off password), JOINING, DECLINE, CANCEL. Each is resent every second
  until answered, for up to 30 seconds. Only friends can ring, and ROOM_READY is ignored unless it
  matches the call you started or accepted. The call itself is an ordinary private room.
- PING and MESSAGE carry a random 4-byte incarnation, which changes when someone restores a backup.
  A backup's numbering may be out of date, so a friend who sees a new incarnation starts both
  directions again from 1 and renumbers anything undelivered. Running one key on two PCs at once
  keeps triggering this, so it isn't supported.
- If neither router accepts incoming connections (for example, both behind CGNAT), friends may
  not be able to connect at all.

## Voice

- Opus at 48 kHz mono, 20 ms frames, 32 kbps VBR, with in-band forward error correction.
- Voice detection: a frame is sent only if it is loud enough (adaptive noise floor) *and*
  pitched like a voice (autocorrelation, 70–400 Hz), so typing, clicks and bumps alone send
  nothing. A 400 ms tail and 60 ms of pre-roll keep word starts and ends intact.
- Receivers rebuild one or two lost packets from FEC or concealment, and keep a 40 ms jitter
  buffer at the start of each burst of speech, capped at 200 ms of queued audio.
- About 55–65 kbps per active speaker including all headers, versus ~800 kbps for the old
  uncompressed audio. Silence costs only keepalives.

## Reliability

- Channel switches are retried until the host confirms them.
- The host re-sends the member list every 3 seconds.
- Chat messages are numbered per channel; clients re-request gaps on every keepalive.

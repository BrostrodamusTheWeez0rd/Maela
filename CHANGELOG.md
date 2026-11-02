# Changelog

## 0.13.0 - The Dynamic layout

The biggest update yet: a whole new way to get around, a much stronger noise filter, pictures in
chat, profile banners and decos, new fonts and accessibility settings, and a lot more.

### The Dynamic layout

- **A new Dynamic layout, with Spatial as its first theme**, and more themes to come. Your rooms
  and friends float together in one space you can pan and zoom across. Drag the background to move,
  scroll to zoom, and click a room (or scroll in on it) to fly into it. Scroll back out, or press
  Esc, to leave. Your voice controls stay in a dock at the bottom wherever you are.
- **Glass orbs.** Rooms, friends and people are glass orbs that catch the light. An orb without a
  picture gets its own swirl of colour and marbled lines made from its name, so they're easy to tell
  apart, and the swirl turns slowly while you hover it.
- **Orbs light up.** Online rooms and friends glow in their own colour, friends softly amber when
  away and red when busy. Offline ones look switched off, picture or pattern still showing. This
  replaces the little status dots.
- **A living background.** Dots drift behind everything and reach out to your pointer with fine
  lines. Hovering an orb, the lines wrap round it as it grows and breathes; dragging one, they stay
  locked on it. Circles gather the dots into a rippling ring when you drag something into them or
  zoom in on one, and an orb you hover draws a looser ring round its circle.
- **Right-click menus** are drawn in your theme, and the map is tucked behind a Map button.
- **The original window stays**, now called the Lightweight layout. Pick Dynamic or Lightweight in
  Settings, under Layout, from a preview of each in your colours, and switch at any time without
  leaving your room or call.
- **Chosen for your PC.** PCs with a graphics card start on Dynamic, and those without start on
  Lightweight. The first time mæla opens after this update, a short tour shows what's new and
  lets you pick.
- **Bigger to start with.** Both layouts open a little larger, never more than most of your screen.
- **Safe to try.** If the Dynamic layout ever has a problem, mæla switches to Lightweight with one
  message, and your room or call keeps running.

### Rooms, friends and circles

- **One space for rooms and friends.** The Rooms and Friends tabs are gone: everything sorts into
  circles. Rooms start in Rooms and friends in Friends, and any circle can hold both, so a friend's
  room can sit with them.
- **Put things where you like.** Drop a room or friend anywhere: into a circle, where it stays put
  (the circle grows to fit), or out on its own. Hold an orb and scroll to make it bigger or smaller.
  Drag a circle's name to move the whole circle.
- **Make circles where you want them.** Right-click empty space for "New circle here". Right-click
  a circle's name to tidy it back to the automatic layout, rename it or delete it, and right-click a
  resized orb to reset its size. Circles and where you put things are kept on your PC only.
- **A tidier side panel.** Its buttons are now "+ New" (host a room, join with a code, add a friend
  or make a circle) and "My code", with Backup behind "...". Empty circles have a "+" too, and orbs
  in no circle are listed under "Not in a circle".

### Inside a room

- **Channels spread round the room**, with everyone in them. Click a voice channel to move into it,
  and double-click any channel to open its chat. Click the room's own orb to pull its channels in,
  and again to let them out.
- **See who's talking.** A line runs from everyone in voice to their channel. It lies flat while
  they're quiet and becomes a live waveform in your highlight colour when they talk, bigger the
  louder they are. Talkers grow and are pushed gently outwards by their own wave, loud ones nudge
  their neighbours aside, and the talking ring takes your highlight colour.
- **Profile cards grow out of people.** Rest on someone and their card appears; click them, or
  someone in a list, and it opens out with your volume and mute for them, their stream, and the
  host's move, kick and ban. Click your own picture in the dock and your card grows up out of it.
- **Hosts can arrange the room.** Drag people between voice channels (anyone can drag themselves).
  Drag a channel to slide it round the room or in and out, and hold it and scroll to resize it.
  Everyone in the room sees it move live, and the layout is saved with the room for next time.
- **Your highlight colour** replaces the fixed green for the room you're in, your channel and
  "Voice connected".

### Profiles

- **Banners.** In Edit profile, give your card a banner: a colour, a gradient or a picture.
- **Card decos** (Glow, Sparkles or Aurora) play on your card when people open it, and **picture
  decos** (Orbit, Halo or Ripple) circle your picture on your card, in rooms and in the dock.
  Sparkles and Orbit are on to start with.
- **Dynamic effects**, in Settings, decides whether you see the decos people choose. It's on with
  the Dynamic layout and off with Lightweight until you switch it, then stays how you set it.
- Only a small copy of a banner picture is ever sent, made on your PC, and others only ever read
  its pixels.

### Voice

- **A much stronger noise filter.** The new Strong noise filter, on to start with, uses RNNoise
  (from Xiph), a small neural network built into mæla, to take out typing, clicks and other sudden
  noises as well as fans and hum. It also decides when you're actually talking, so typing or noise
  alone doesn't open your mic. It runs entirely on your PC, on a few percent of one core, and
  learns, stores and sends nothing. Turn it off in Settings for the lighter original filter; a PC
  that can't keep up switches back on its own.

### Chat

- **Pictures in chat**, in room channels and friend chats. Pick one with the picture button, or
  paste one, a screenshot too, with Ctrl+V. Click a picture to see it full size and save it.
- **Safe and private pictures.** Your PC rebuilds each picture from its pixels, at up to 1920
  pixels across in full colour, so nothing else in the file (location, camera details, its name)
  is ever sent. Everyone else only unpacks plain pixels with mæla's own small code, never running
  an image decoder on a file from someone else. In rooms the host checks every picture before it's
  posted; friend chat pictures come straight from your friend and are kept, encrypted, with the chat.
- **YouTube links** show as a card with a Play button that opens the video in your browser. Nothing
  is loaded from YouTube unless you click Show preview.
- **Colour emoji** now show in the Dynamic layout's chat and statuses, as they do in Lightweight.

### Look and accessibility

- **Midnight is the new default colour**, a neon cyan and navy, in both layouts. If you'd already
  chosen a colour, you keep it.
- **Five new colours**: Synthwave, Forest, Ember, Ocean and Sakura, each with two accents, sorted
  with similar colours together. Browse them in Settings with the arrows either side, or the
  mouse wheel.
- **Your icons take your colour**: the window, taskbar and tray, and shortcuts to mæla on your
  desktop, in the Start menu or pinned to the taskbar. mæla's own icon is now the blue Midnight logo.
- **New fonts.** The Dynamic layout now uses Outfit, and you can pick OpenDyslexic (easier for some
  readers with dyslexia), Atkinson Hyperlegible (for low vision), Lexend, Space Grotesk or Windows'
  Segoe UI instead. All are free under the SIL Open Font License, built into mæla and never
  downloaded. Initials on orbs follow your font.
- **Text size** at 100, 115 or 130%, and the smallest text is a little bigger than before.
- **Reduce motion** turns off the moving dots and lines, growing and breathing orbs, decos and
  swirls, while everything still works.
- **Motion** sets how often the Dynamic layout draws while things move: Smooth (every refresh, the
  default), Balanced (half the refresh on screens over 100 Hz, evenly paced and lighter) or Light
  (30 a second, for battery).
- The font and text size apply in the Dynamic layout; Lightweight keeps Windows' font.

### Works with older versions

Everyone can still talk, chat and share screens together across versions. People on older versions
don't see banners, decos, pictures or the host's arrangement of channels, and see a short
"Sent a picture. Update mæla to see it." line in place of a picture. Hosts need this version
for members to send pictures or see their channel layout.

### Credits

RNNoise (BSD licence), Outfit, Space Grotesk, Lexend, Atkinson Hyperlegible and OpenDyslexic (SIL
Open Font License) and Twemoji (CC-BY 4.0) are listed with their licences in Settings, Licenses.

## 0.12.0 - Live stream changes and room art

- **Hosts can dress up their room.** Right-click a room you host to rename it, or set a picture and
  banner. Everyone in the room sees changes straight away. Rooms show their initials until they have
  a picture, and each gets its own generated banner until the host picks one. Members need this
  version to see them.
- **Change your stream while live.** A Change button on the live bar lets you switch screen or
  window, quality, frame rate, sound and pointer on the fly, without restarting the stream. People
  watching stay connected. Changing resolution or frame rate causes one brief hitch.

## 0.11.3 - Call prompt fix and tidier sharing

- **Tidier screen sharing.** Sharing no longer allocates a new full-size preview frame 10 times a
  second, and a graphics card encoder that fails to open is now fully released.
- **Fixed a freeze when answering a call.** If you were still in a room, the question asking whether
  to leave it opened behind the call prompt and locked mæla until it was force-closed. Call and
  friend request prompts now appear in the corner of the screen.

## 0.11.2 - Friends in the room row and call sounds

- **Fixed the broken layout from 0.11.1.** Opening Friends could put the room list on top of it, or
  move the room list to the wrong side of the window.
- **Friends swap in for your rooms.** A labelled button at the top left replaces the room tiles with
  your friends in the same row, and clicking one opens your chat. The labels swap too, so the heading
  always says where you are. The room card and your name bar stay put.
- **Calls sound like calls.** A proper ring when someone calls you, and a soft tone while you wait
  for them to answer.
- **Easier to spot new messages.** The Friends button lights up and shows a count when a friend
  messages you or sends a request.
- **Status moved to your profile.** Click your name to choose Online, Away, Busy or Offline.

## 0.11.1 - Friends view and easier requests

- **Friends get their own view.** The mæla icon at the top of the room list opens Friends in the
  main window; clicking a room goes back. Requests and unread messages show on the icon. Your status
  is there and in the tray.
- **Rooms use the host's name.** Everyone sees the name the host chose, and joining no longer asks
  you to name it. Hosts need this version for members to see it.
- **Friend requests pop up.** When someone adds you, a window asks straight away, with a check to
  accept and an X to decline. On Busy, the request waits quietly in your friends list.
- **Shorter notices.** The screen sharing and friends notices are now one line, each with a Don't
  show me again switch.
- **Windows setup, if you want it.** First-time setup, or your first launch of this version, offers
  to allow mæla through Windows Firewall and exclude it from Defender. Nothing changes unless you
  choose it. Without the firewall rule, a request from someone new can be blocked silently, so the
  Friends window warns you and offers to fix it. You can add or remove either one any time in
  Settings > Network.

## 0.11.0 - Friends

- **Friends.** Everyone gets a friend code. Paste someone's code to send a request; once they
  accept, click them to chat. Your encryption key is your account, so a new IP address never ends
  a friendship. There are no accounts and no server: messages go directly between your two PCs,
  and wait encrypted on the sender's PC until you're both online.
- **Friend chats have everything room chat does.** Colour emoji, an emoji picker, reactions and
  search.
- **Call your friends.** Click Call on a friend who's online. It rings until they answer, then
  starts a private room just for the two of you, with screen sharing and everything else a room
  has. Busy doesn't ring: you get a missed call in the chat.
- **Room passwords stay on your PC.** Joining now proves you know the password without sending it.
  To join a password room, the host needs this version.
- **Back up your account.** Save your friend key and friends list to a password-protected file, and
  restore it after reinstalling Windows or on a new PC. Friends carry on with no new request.
- **Online, Away, Busy, Offline.** Busy still delivers messages but makes no sound. Offline closes
  the listener completely. Friends stay off until you first choose to go online.
- **Saved rooms are fully encrypted on disk.** Room names, channels and room codes, which contain
  the host's address, used to be readable in `rooms-v2.txt`. They now live in an encrypted
  `rooms-v3.dat`, and your existing rooms convert by themselves on first launch. Older copies of
  mæla won't see your rooms afterwards.
- **Saved rooms are harder to lose.** A crash during a save can't damage the file, and a file mæla
  can't open is kept, with a warning, instead of being overwritten.

## 0.10.0 - Graphics card, sound and steadier streams

- **Share your PC's sound.** Share sound, on by default, sends what your programs play along with
  a screen, or only that program's sound with a window. The voice chat is always left out. In the
  viewer, M mutes and the arrow keys change the volume. Needs Windows 10 version 2004 or later;
  on older Windows the picture carries on without sound.
- **Graphics-card watching.** The viewer now draws with your graphics card, and at 1080p and
  above decodes there too, inside the same sandbox. Settings > App lets you choose Automatic,
  Always or Never. If decoding fails, or a stream doesn't pass mæla's safety check, it carries on
  with the processor. Decoding on the card means video reaches your graphics driver, which the
  sandbox protects less (see SECURITY.md).
- **Graphics-card encoding.** Screen sharing now uses your NVIDIA, AMD or Intel graphics card
  when it can, so high resolutions and frame rates leave your processor free. mæla goes back to
  the processor by itself if the card can't do it.
- **Fewer freezes when packets are lost.** Viewers can rebuild or re-request lost pieces instead
  of waiting for a whole new picture, and keyframes are rarer, so there are fewer spikes.
- **Smoother to watch.** The viewer now shows frames on an even beat, with a small buffer that
  grows only as much as your connection needs.
- **Steadier frame rate when sharing.** Capture and encoding run separately, so a slow frame
  doesn't hold up the next.
- **Better quality decisions.** Quality drops only on real signs of congestion (loss or rising
  ping), not because the screen was still, and changes without a visible hitch.
- **Stats.** The Stats button on the live bar, and F3 in the viewer, show where the time goes.
- **Steadier voice during screen sharing.** Video now leaves evenly instead of a whole frame at
  once, on its own thread, so voice no longer gets stuck behind it. When a viewer's connection
  can't keep up, mæla holds video back instead of flooding the router.
- **Rooms open a port on more kinds of network**: PCs with a VPN or several network adapters,
  routers with an unused PPP service, routers that refuse permanent mappings, and routers that
  forget mappings when they restart. If another router or your internet provider sits in front
  of yours, mæla now says so, and the room code uses your real public address.

## 0.9.2 - Router fix

- **Fixed: rooms not opening a port on the router automatically.** mæla now also asks the
  router directly (Windows Firewall can silently drop the router's reply to a network-wide
  request), tries every router that answers, and falls back to NAT-PMP, which many routers
  support when UPnP doesn't answer.

## 0.9.1 - Stream preview

- **Preview your own stream**: the Preview button on the red live bar shows exactly what
  you're sending.
- Settings > App explains the screen-sharing choice more clearly: direct (default, best
  quality; the people you stream with can see your IP) or always through the host.

## 0.9.0 - Screen sharing

- **Share your screen** with the new button on the voice card: a whole screen or a single
  window, 720p to 4K, 30, 60 or 120 fps (1080p60 by default), with or without your mouse
  pointer. A red bar shows you're live, how many are watching, and a Stop button.
- **Watch** by double-clicking someone with a LIVE badge in your voice channel, or from their
  profile card. Nothing is sent until someone watches. Watch in a window or full screen
  (double-click or F11).
- **Direct, encrypted and private by design.** Video goes straight from the sharer to each
  viewer, encrypted with a fresh key for that pair; if a direct path can't be opened it goes
  through the host. The first time, mæla explains that a direct connection shows the other
  person your IP address, and you can choose to always go through the host instead
  (Settings > App).
- **The viewer is locked down.** Watching runs in a separate copy of mæla at Low integrity in
  a restricted Windows job: it can't write to your files, start programs or use the clipboard,
  so even a deliberately broken stream can't reach the rest of your PC.
- **Smooth over real connections.** After lost data, video resumes cleanly from a fresh
  keyframe; viewers report what they receive and the quality adapts to the slowest viewer.
- Video uses OpenH264 (Cisco), downloaded from Cisco's server the first time you share or
  watch and checked against a fingerprint built into mæla.
- Encoding runs on the processor for now: 1080p60 is comfortable on most PCs; 1440p, 4K and
  120 fps depend on how fast your processor is. Graphics-card encoding is planned.
- Screen sharing needs 64-bit Windows.

## 0.8.0 - Channels, profiles and Disconnect

- **One click opens a channel's chat, double-click joins its voice.** You can read and type in
  the channel you have open without being in its voice.
- **Leave voice keeps you in the room.** You drop out of voice and off the member list (there's
  no Lobby any more), but you can keep chatting. **Disconnect** leaves the room completely; for
  a host it stops the room, asking first if anyone else is in it.
- **Chat-only channels.** Hosts can create text channels (shown with #) alongside voice ones.
- **Profiles.** Click anyone in the sidebar or chat to see their card: picture, status and
  what they're doing, plus your own volume and mute for them (and Move, Kick and Ban for the
  host). Click yourself to set your picture and status. Only a small 80×80 pixel copy of your
  picture is shared, never the file or its details.
- **One room at a time.** Opening another room asks, then leaves the current one.
- **"What mæla uses"** in first-run setup lists everything mæla uses or downloads, with
  licenses, before anything is fetched.
- Security: a new hostile-member test suite, and SECURITY.md now spells out exactly what other
  people in a room can and can't do.
- Everyone in a room should update: older versions still connect but won't see chat-only
  channels or profiles.

## 0.7.2 - Freeze fix

- **Fixed: mæla could freeze when someone joined and started talking.** The microphone's
  audio work ran inside a Windows audio callback, and opening speakers for a new person at
  the same moment could lock both up for good. Microphone processing now runs on its own
  thread, and speakers are never opened while other work is waiting on them.
- **Quitting always cuts the connection cleanly.** Members say goodbye to the host, and a
  host that quits now tells everyone at once (they see "The host closed the room" and
  rejoin by themselves when it is back) instead of hanging for 8 seconds.
- **Fixed: slow rejoins on Windows after a crash.** Messages to the crashed copy's closed
  port made Windows report errors that flooded the host just as that person rejoined; mæla
  now turns that reporting off, as UDP apps on Windows should.
- **A clean start every time.** If an earlier copy of mæla is frozen, opening mæla again
  closes the frozen copy and starts fresh, instead of just poking the stuck window.
- **Fixed: the old icon showing after updating.** Windows keeps a cached copy of each
  program's icon; mæla now asks Windows to refresh it once after each update, so the desktop,
  shortcuts and taskbar show the current icon.
- **A room's new code updates its saved entry.** If a host's address or port changes, pasting
  their new code updates the room you already have saved (it is recognised by the host's
  key) instead of leaving a dead copy behind.

## 0.7.1 - Clearer voices, tray and instant rejoin

- **Runs in the tray.** Closing the window keeps mæla running in the system tray and keeps
  you in your room, like other voice apps. Click the tray icon to reopen it; right-click
  it to mute, deafen, leave voice or quit. You can turn this off in Settings > App.
- **Only one copy runs at a time.** Opening mæla again brings the running copy to the front
  instead of starting a second one.
- **Fixed: rejoining after mæla closed unexpectedly.** If mæla crashed, was closed from Task
  Manager or lost its connection, the room kept a stale copy of you, so you showed up twice
  when you came back. You now replace it straight away and land back in the channel you were
  in. A normal quit also tells the room more reliably, so you disappear from the list at once.

- **Automatic mic level** (on by default): quiet mics are boosted up to 24 dB and loud ones
  brought down, so everyone is heard at a similar, clear level. Background noise is never
  pumped up between words, and a soft limiter prevents clipping. Turn it off in
  Settings > Voice & audio.
- **Keyboard typing is no longer sent.** Your mic now only opens for an actual voice (it
  listens for pitch, not just loudness), so typing, clicks and desk bumps on their own stay
  silent. The first 60 ms before you speak are still sent, so words aren't clipped.
- **Key clicks under your voice are turned down** by the noise filter (about 15 dB), without
  changing how your voice sounds.
- **mæla is now open source under the MIT License.** Settings > Licenses shows it alongside
  the third-party notices, and releases include `LICENSE.txt`.
- Fixed: the "+" button for creating channels was positioned off-screen.

## 0.7.0 - New look, Encryption, Opus, Chat, Echo Cancellation & Auto-Update

> **Fresh install.** 0.7.0 can't talk to 0.6 or earlier. Everyone needs to download it once
> (later versions update themselves), and hosts need to send their friends new room codes.
> Saved rooms from 0.6 are cleared; your display name and audio settings are kept.

- **Brand-new look.** A new "Bubble M" icon and a redesigned window: room tiles, a channel
  tree showing who's where, a voice card, a user bar, and a cleaner chat.
- **Themes:** Maela Red (default), Midnight, Daylight and OLED Black, with a live preview in
  Settings. The Windows title bar follows the theme.
- **Colour emoji, reactions and an emoji picker.** Emoji are open-source Twemoji images
  (CC-BY 4.0), downloaded on first run from the official project and checked file by file
  against hashes built into the app. Nothing unverified is used; Settings can repair them.
- **First-run setup:** pick your name and a theme, and fetch the emoji.
- **Message search** in the channel you're in.
- **Deafen**, and everyone in the room sees who's muted or deafened.
- **Live ping** to the host in the voice card.
- **Noise filter** that removes steady background noise (fans, hum, hiss); on by default.
- **Sound effects** from a small built-in synth (no audio files): join/leave, someone
  joining/leaving, mute, deafen, messages, connect/disconnect. Can be turned off.
- "Alice joined" / "Bob left" lines in the channel chat.
- **Everything is encrypted.** Voice, chat, member lists and control messages use an X25519
  key exchange (Noise NK pattern), AES-256-CTR and HMAC-SHA256, with forward secrecy and
  replay protection. See `SECURITY.md` for what this does and doesn't protect against.
- **New room codes** include a fingerprint of the host's key, so nobody can impersonate the
  host. Codes are longer (`M-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`) and every single-character
  typo is detected. Saved hosted rooms keep the same key, so their code stays stable.
- **Room passwords** (optional). Set one when hosting or from the room's right-click menu.
  Repeated wrong guesses lock that IP out for a minute.
- **Opus voice** replaces uncompressed audio: about 55–65 kbps per speaker instead of
  ~800 kbps, with forward error correction and loss concealment.
- **Silence detection**: nothing is sent while you're quiet, so a room mostly costs only the
  people currently talking.
- Rooms now hold up to 16 people (was 6).
- The online check for saved rooms now only reveals that a room is online, not its name or
  member count.
- Third-party licenses are viewable in Settings.

- Added text chat to every voice channel. The main area now shows the chat for the
  channel you're in, Discord-style, with names, avatars, timestamps and day dividers.
  - Joining a channel loads its recent history (up to 200 messages are kept per channel
    while the room is running).
  - Delivery is reliable over UDP: lost messages are re-sent automatically and everyone
    sees the same order. Tested at 25% simulated packet loss.
  - The taskbar button flashes when a message arrives while mæla is in the background.
  - Right-click a message to copy it.
- The sidebar now shows each channel with the people in it underneath (speaking ring,
  host tag, local-mute tag), plus a Lobby section. Right-click a person for volume, mute,
  move, kick and ban; double-click a channel or a person to join them.
- The room code and network status moved to the top bar.
- Channel switches are now retried until the host confirms them, and the host re-sends the
  member list every 3 seconds, so one lost packet can no longer leave someone in the wrong
  channel or with an outdated member list.
- "Leave Room" is available while still connecting (shown as "Cancel").
- Fixed "&" showing as an underline in some buttons and labels.

- Added built-in acoustic echo cancellation, on by default. Friends no longer hear
  themselves when you use speakers instead of a headset.
  - Automatically measures the speaker-to-microphone delay (up to ~680 ms).
  - Adaptive filter with an 85 ms echo tail, protected against double-talk.
  - Residual echo suppression for whatever the filter can't remove.
  - Adds only 10 ms of latency and about 4% of one CPU core.
  - Can be switched off in Settings; the change applies immediately.
- Added automatic updates from GitHub Releases.
  - Checks shortly after launch and every six hours while running (including in the tray).
  - Shows the release notes, with Update now / Later / Skip this version.
  - Downloads are checked against a published SHA-256 and the expected version before
    the executable is swapped and mæla restarts.
  - Can be switched off, or run on demand, from Settings.
- Release workflow now publishes automatically when `VERSION` changes on `main`, attaches
  `Maela.exe.sha256`, and uses the matching CHANGELOG section as release notes.
- App version is now stamped from the `VERSION` file at build time.
- Added crypto (RFC test vectors), security (live attack), voice, chat and echo canceller
  test suites to CI.
- Saved rooms are no longer probed every 5 seconds. Each joined room is checked once
  when mæla starts (or when it is added), and once more when you leave it. While you are connected, the room's status and
  member count come from the live session. Right-click a room and choose
  "Check if online" to refresh it by hand.

## 0.6.0 - Room Codes

- Replaced user-facing `IP:port` invites with short mæla room codes.
- Added checksum validation to room codes.
- Hid public IP and UDP port values from normal settings/UI.
- Hosting networking is now automatic from the user's perspective.
- Existing saved literal IPv4 invites are migrated to room codes when possible.
- Kept legacy parsing internally so older saved-room data can still recover.
- Preserved persistent hosting, saved-room presence checks, voice channels, per-user audio controls, kick/ban, and branded Windows integration.
- Added repository and GitHub Actions release structure.

## 0.5.1

- Branded mæla Windows build.
- Added embedded application icon and in-app mark.
- Fixed missing text prompt used by voice-channel creation and rename.

## 0.5.0

- Added multiple voice channels and Lobby state.
- Added persistent host/tray behavior.

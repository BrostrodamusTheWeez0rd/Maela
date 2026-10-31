# Contributing

mæla is currently a compact single-source Windows application while the core architecture is still moving quickly.

For changes:

1. Keep compatibility with the Windows .NET Framework compiler used by `scripts/build_ci.cmd` (C# 5: no string interpolation, `?.`, `=>` members, etc.).
2. Avoid adding external runtime/package dependencies without a clear reason. Open-source components only, and never committed: they are fetched from their official source and verified against pinned SHA-256 hashes (see `deps/README.md`).
3. Keep network protocol changes versioned and reject incompatible packets safely. Never send anything outside an encrypted session except HELLO, HELLO_REPLY and PROBE (see `docs/NETWORKING.md`).
4. Do not expose raw network endpoints in the normal user interface.
5. Test host, join, leave, tray persistence, saved-room probing and multi-channel voice before release.
6. Run `scripts\test_ci.cmd` after touching audio or networking code; CI runs it on every push.
7. Bump `VERSION` and add a CHANGELOG entry to release. Do not edit assembly version attributes by hand; the build generates them from `VERSION`.

# Contributing

mæla is currently a compact single-source Windows application while the core architecture is still moving quickly.

For changes:

1. Keep compatibility with the Windows .NET Framework compiler used by `scripts/build_ci.cmd`.
2. Avoid adding external runtime/package dependencies without a clear reason.
3. Keep network protocol changes versioned and reject incompatible packets safely.
4. Do not expose raw network endpoints in the normal user interface.
5. Test host, join, leave, tray persistence, saved-room probing and multi-channel voice before release.
6. Run `scripts\test_ci.cmd` after touching `src/EchoCanceller.cs`; CI runs it on every push.
7. Bump `VERSION` and add a CHANGELOG entry to release. Do not edit assembly version attributes by hand; the build generates them from `VERSION`.

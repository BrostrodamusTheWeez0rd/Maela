# Dependencies

mæla only uses open-source components, and **none of them are stored in this repository**.
Each is fetched from its official source and checked against SHA-256 hashes pinned here.
A file that doesn't match is rejected; there is no "skip verification" path.

| Component | What | Fetched | Official source | License |
|---|---|---|---|---|
| Concentus 1.1.7 | Opus voice codec (C#) | at **build** time by `scripts/fetch_deps.ps1`, compiled into `Maela.exe` | github.com/lostromb/concentus | BSD 3-clause |
| Twemoji 17.0.3 | Colour emoji images | at **first run** by the app (Setup), stored in `%LocalAppData%\Maela\assets` | github.com/jdecked/twemoji | CC-BY 4.0 (graphics) |

Code is fetched at build time (so the app never downloads and runs code). Assets such as
images are fetched at run time.

## Updating a pinned dependency

1. Pick the new upstream commit or tag and review what changed.
2. Regenerate the hashes:
   - Concentus: `deps/concentus.lock` (per file, CRLF normalised to LF).
   - Twemoji: the manifest in `src/Assets.cs`.
3. Build, run `scripts\test_ci.cmd`, and release. Installed copies re-fetch the new asset
   versions automatically after updating.

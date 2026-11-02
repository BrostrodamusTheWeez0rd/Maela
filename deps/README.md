# Dependencies

mæla only uses open-source components. Code and large assets are **not stored in this
repository**: each is fetched from its official source and checked against SHA-256 hashes
pinned here. A file that doesn't match is rejected; there is no "skip verification" path.
The exceptions are a few small files built into `Maela.exe`, listed below the table.

| Component | What | Fetched | Official source | License |
|---|---|---|---|---|
| Concentus 1.1.7 | Opus voice codec (C#) | at **build** time by `scripts/fetch_deps.ps1`, compiled into `Maela.exe` | github.com/lostromb/concentus | BSD 3-clause |
| Twemoji 17.0.3 | Colour emoji images | at **first run** by the app (Setup), stored in `%LocalAppData%\Maela\assets` | github.com/jdecked/twemoji | CC-BY 4.0 (graphics) |

Code is fetched at build time (so the app never downloads and runs code). Assets such as
images are fetched at run time.

Stored in this repository and built into `Maela.exe`:

| Component | What | Where | Official source | License |
|---|---|---|---|---|
| RNNoise 0.1.1 | Strong noise filter: ported to C# in `src/Rnnoise.cs`, with its trained model | `assets/rnnoise` | github.com/xiph/rnnoise | BSD 3-clause |
| Outfit | Default font in the Dynamic layout | `assets/fonts` | github.com/Outfitio/Outfit-Fonts | SIL OFL 1.1 |
| OpenDyslexic | Font for readers with dyslexia | `assets/fonts` | github.com/antijingoist/opendyslexic | SIL OFL 1.1 |
| Atkinson Hyperlegible | Font for low vision | `assets/fonts` | github.com/google/fonts (Braille Institute) | SIL OFL 1.1 |
| Lexend | Font | `assets/fonts` | github.com/googlefonts/lexend | SIL OFL 1.1 |
| Space Grotesk | Font | `assets/fonts` | github.com/floriankarsten/space-grotesk | SIL OFL 1.1 |

Their SHA-256 hashes are in `src/ReadingFonts.cs` (fonts, checked when they're unpacked) and in
`src/Rnnoise.cs` (the model). The full licences are in `THIRD_PARTY_NOTICES.txt`.

## Updating a pinned dependency

1. Pick the new upstream commit or tag and review what changed.
2. Regenerate the hashes:
   - Concentus: `deps/concentus.lock` (per file, CRLF normalised to LF).
   - Twemoji: the manifest in `src/Assets.cs`.
3. Build, run `scripts\test_ci.cmd`, and release. Installed copies re-fetch the new asset
   versions automatically after updating.

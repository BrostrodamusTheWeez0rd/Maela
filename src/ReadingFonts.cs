using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Maela
{
    /// <summary>A font you can pick in Settings, built into mæla.</summary>
    internal sealed class AppFont
    {
        public readonly string Name, Family, Description;
        public readonly string[] Files, Sha256;

        public AppFont(string name, string family, string description, string[] files, string[] sha256)
        {
            Name = name;
            Family = family;
            Description = description;
            Files = files;
            Sha256 = sha256;
        }
    }

    /// <summary>
    /// The fonts the Dynamic layout can use. All are free under the SIL Open Font License 1.1
    /// (see THIRD_PARTY_NOTICES), built into mæla unmodified and never downloaded. WPF draws
    /// fonts from files, so a chosen font's files are copied out to %LocalAppData%\Maela\Fonts,
    /// and checked against these hashes every time they're used.
    /// </summary>
    internal static class ReadingFonts
    {
        /// <summary>In the order Settings lists them; a font's place here is what's saved. Outfit is the default.</summary>
        public static readonly AppFont[] All =
        {
            new AppFont("Outfit", "Outfit", "mæla's font: rounded and modern",
                new[] { "Outfit-Regular.ttf", "Outfit-SemiBold.ttf" },
                new[] { "3b64ac4f6ab6a8eebddd4b0bc03c811c43602e11e176382ab0ee6be615ab861b", "bf2e1d2a6ec2a67952e8b36edd2b2bb9f340c0cdd10b0ad5145b4dbbc1339608" }),
            new AppFont("OpenDyslexic", "OpenDyslexic", "easier for some readers with dyslexia",
                new[] { "OpenDyslexic-Regular.otf", "OpenDyslexic-Bold.otf" },
                new[] { "32f5840fb2bf844bdabafe372591ddfb9286e98117f5b21950aaf54ea856919a", "5d690ea4c87ccb0d5d5d81487826a395e9b20a6c1d606f1f262c060d2bef4f03" }),
            new AppFont("Atkinson Hyperlegible", "Atkinson Hyperlegible", "very clear letters, for low vision",
                new[] { "AtkinsonHyperlegible-Regular.ttf", "AtkinsonHyperlegible-Bold.ttf" },
                new[] { "7fb917c89019896d0b52ee84b7cbb3304c18cb90b19a62f5e32712bd23e97669", "5a3b0c8cc8ca545155150b4512a1fa248298df121c50d6557e651e61fbdab92f" }),
            new AppFont("Lexend", "Lexend", "made to be easier to read",
                new[] { "Lexend-Regular.ttf", "Lexend-SemiBold.ttf" },
                new[] { "e2082c28389e9871d5d77ae9163d5c0c1c259fe39bdac91f73d56a600b61f60f", "204875848cc7981290a75f8be0e64b335b6d8f6ec6cc3ea05d907a37adab8b7b" }),
            new AppFont("Space Grotesk", "Space Grotesk", "techy, with character",
                new[] { "SpaceGrotesk-Regular.ttf", "SpaceGrotesk-Bold.ttf" },
                new[] { "5ede28c4425f3fe4830c8f4754b39e9a87a93d0c3baa5e0a9924532aaa8a98bd", "7209bbb75fc0f5c546a5f5773b0db74ffc6abf04c2148c1105cace5765a96bdb" }),
            new AppFont("Segoe UI", null, "Windows' own", new string[0], new string[0]),
        };

        private static readonly object Gate = new object();
        private static string _folder;
        private static bool _broken;

        /// <summary>The folder holding a font's files, checked and ready, or null to use Windows' font.</summary>
        public static string Folder(AppFont font)
        {
            if (font == null || font.Files.Length == 0)
                return null;
            lock (Gate)
            {
                if (_broken)
                    return null;
                try
                {
                    if (_folder == null)
                    {
                        _folder = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maela"), "Fonts");
                        Directory.CreateDirectory(_folder);
                    }
                    for (int i = 0; i < font.Files.Length; i++)
                    {
                        string path = Path.Combine(_folder, font.Files[i]);
                        if (File.Exists(path) && Matches(File.ReadAllBytes(path), font.Sha256[i]))
                            continue;
                        byte[] built = BuiltIn(font.Files[i]);
                        if (built == null || !Matches(built, font.Sha256[i]))
                            throw new InvalidDataException("The built-in font " + font.Files[i] + " doesn't match its checksum.");
                        File.WriteAllBytes(path, built);
                    }
                    return _folder;
                }
                catch (Exception e)
                {
                    Program.LogCrash(e);
                    _broken = true; // Windows' font, then; not worth trying again this run.
                    return null;
                }
            }
        }

        public static AppFont At(int index)
        {
            return index >= 0 && index < All.Length ? All[index] : All[0];
        }

        private static byte[] BuiltIn(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                    return null;
                MemoryStream copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        private static bool Matches(byte[] data, string sha256)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", String.Empty).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
    }
}

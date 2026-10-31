using System;
using System.Collections.Generic;
using System.IO;

namespace Maela.Tests
{
    // Downloads the real emoji pack from its official source into a temp folder and checks
    // verification, tamper detection, repair, and that unverifiable files are refused.
    //
    // Build and run:  scripts\test_ci.cmd   (needs internet access)
    internal static class AssetTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "maela-asset-test-" + Guid.NewGuid().ToString("N"));
            AssetStore.RootOverrideForTests = root;
            try
            {
                Check(!EmojiPack.Verify(), "nothing installed yet");
                Check(EmojiPack.GetImage(EmojiPack.Find("😀")) == null, "no image before install");

                int lastDone = 0, total = 0;
                EmojiPack.Install(delegate(int done, int count) { lastDone = done; total = count; }, null);
                Check(total == EmojiPack.All.Length + 1 && lastDone == total, "downloaded " + lastDone + "/" + total + " files from " + EmojiPack.Homepage);
                Check(EmojiPack.Verify() && EmojiPack.Available, "installed pack verifies");
                Check(File.Exists(Path.Combine(EmojiPack.Folder, "LICENSE-GRAPHICS.txt")), "license file installed alongside");

                EmojiEntry grin = EmojiPack.Find("😀");
                Check(EmojiPack.GetImage(grin) != null && EmojiPack.GetImage(grin).Width == 72, "colour image loads (72 px)");
                Check(EmojiPack.Find("❤️") != null && EmojiPack.Find("❤") != null, "heart found with and without FE0F");

                List<object> tokens = EmojiPack.Tokenize("gg 🎉🔥 nice ❤️!");
                int emoji = 0; foreach (object t in tokens) if (t is EmojiEntry) emoji++;
                Check(emoji == 3 && tokens.Count == 6, "tokenizer splits text and 3 emoji (" + tokens.Count + " parts)");

                // Tamper with one installed file.
                string victim = Path.Combine(EmojiPack.Folder, "1f44d.png");
                File.WriteAllBytes(victim, new byte[] { 1, 2, 3 });
                Check(!EmojiPack.Verify(), "tampered file detected");
                Check(EmojiPack.GetImage(EmojiPack.Find("👍")) == null, "tampered pack is not used");

                EmojiPack.Install(null, null);
                Check(EmojiPack.Verify(), "repair re-downloads and verifies");

                // A file whose pinned hash is wrong must never be installed.
                string badFolder = Path.Combine(root, "bad");
                List<AssetFile> forged = new List<AssetFile>();
                forged.Add(new AssetFile("x.png", EmojiPack.BaseUrl + "assets/72x72/1f600.png", new string('0', 64)));
                bool refused = false;
                try { AssetStore.InstallFolder(badFolder, forged, null, null); } catch (InvalidOperationException) { refused = true; }
                Check(refused && !Directory.Exists(badFolder), "hash mismatch refused, nothing installed");

                List<AssetFile> elsewhere = new List<AssetFile>();
                elsewhere.Add(new AssetFile("x.png", "https://example.com/1f600.png", new string('0', 64)));
                refused = false;
                try { AssetStore.InstallFolder(badFolder, elsewhere, null, null); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "non-official source refused");
            }
            catch (Exception ex)
            {
                Check(false, "unexpected error: " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All asset checks passed." : _failures + " asset check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>A published GitHub release that is newer than the running build.</summary>
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string Title;
        public string Notes;
        public string PageUrl;
        public string ExeUrl;
        public string ChecksumUrl;
    }

    /// <summary>
    /// Self-update from the project's GitHub Releases.
    ///
    /// The release workflow publishes <c>Maela.exe</c> and <c>Maela.exe.sha256</c>. The updater
    /// downloads both over HTTPS, refuses anything whose SHA-256 or assembly version does not
    /// match, then swaps the executable in place (Windows allows renaming a running .exe) and
    /// restarts. The previous build is kept as <c>Maela.exe.old</c> until the next launch.
    /// </summary>
    internal static class Updater
    {
        public const string RepositoryOwner = "BrostrodamusTheWeez0rd";
        public const string RepositoryName = "Maela";
        public const string ExeAssetName = "Maela.exe";
        public const string ChecksumAssetName = "Maela.exe.sha256";
        public const string UpdatedArgument = "--updated";

        private const int MaxDownloadBytes = 64 * 1024 * 1024;

        public static string ReleasesPage
        {
            get { return "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases"; }
        }

        public static Version CurrentVersion
        {
            get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
        }

        public static string CurrentVersionText
        {
            get { return FormatVersion(CurrentVersion); }
        }

        public static string FormatVersion(Version v)
        {
            if (v == null)
                return "?";
            return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
        }

        /// <summary>Returns the latest release if it is newer than this build, otherwise null. Throws on network errors.</summary>
        public static UpdateInfo CheckForUpdate()
        {
            string url = "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
            string json = Encoding.UTF8.GetString(Download(url, "application/vnd.github+json", 2 * 1024 * 1024, 8000));
            Dictionary<string, object> release = MiniJson.Parse(json) as Dictionary<string, object>;
            if (release == null)
                throw new InvalidOperationException("GitHub returned an unexpected response.");

            if (GetBool(release, "draft") || GetBool(release, "prerelease"))
                return null;

            string tag = GetString(release, "tag_name");
            Version latest;
            if (!TryParseTag(tag, out latest))
                return null;
            if (latest <= CurrentVersion)
                return null;

            UpdateInfo info = new UpdateInfo();
            info.Version = latest;
            info.Tag = tag;
            info.Title = GetString(release, "name");
            info.Notes = GetString(release, "body");
            info.PageUrl = GetString(release, "html_url");
            if (String.IsNullOrEmpty(info.PageUrl))
                info.PageUrl = ReleasesPage;

            List<object> assets = null;
            object assetsValue;
            if (release.TryGetValue("assets", out assetsValue))
                assets = assetsValue as List<object>;
            if (assets != null)
            {
                foreach (object item in assets)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset == null)
                        continue;
                    string name = GetString(asset, "name");
                    string download = GetString(asset, "browser_download_url");
                    if (String.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
                        info.ExeUrl = download;
                    else if (String.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                        info.ChecksumUrl = download;
                }
            }

            return info;
        }

        /// <summary>True when this release can be installed automatically (it ships a checksum).</summary>
        public static bool CanInstall(UpdateInfo info)
        {
            return info != null && IsGitHubUrl(info.ExeUrl) && IsGitHubUrl(info.ChecksumUrl);
        }

        /// <summary>Downloads and verifies the update next to the running executable. Returns the staged file path.</summary>
        public static string DownloadAndVerify(UpdateInfo info)
        {
            if (!CanInstall(info))
                throw new InvalidOperationException("This release does not include a verified Windows download.");

            string checksumText = Encoding.UTF8.GetString(Download(info.ChecksumUrl, "application/octet-stream", 4096, 15000));
            string expected = ParseChecksum(checksumText);
            if (expected == null)
                throw new InvalidOperationException("The release checksum file is not valid.");

            byte[] exe = Download(info.ExeUrl, "application/octet-stream", MaxDownloadBytes, 60000);
            string actual = Sha256Hex(exe);
            if (!String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update failed its integrity check and was discarded.");

            string target = Application.ExecutablePath;
            string staged = target + ".download";
            TryDelete(staged);
            File.WriteAllBytes(staged, exe);

            try
            {
                Version stagedVersion = Normalize(AssemblyName.GetAssemblyName(staged).Version);
                if (stagedVersion != info.Version)
                    throw new InvalidOperationException("The downloaded update reports version " + FormatVersion(stagedVersion) + " instead of " + FormatVersion(info.Version) + ".");
            }
            catch (BadImageFormatException)
            {
                TryDelete(staged);
                throw new InvalidOperationException("The downloaded update is not a valid mæla executable.");
            }
            catch
            {
                TryDelete(staged);
                throw;
            }

            return staged;
        }

        /// <summary>Replaces the running executable with the staged one and launches it.</summary>
        public static void InstallAndRestart(string staged)
        {
            string target = Application.ExecutablePath;
            string backup = target + ".old";

            TryDelete(backup);
            File.Move(target, backup);
            try
            {
                File.Move(staged, target);
            }
            catch
            {
                try { File.Move(backup, target); } catch { }
                throw;
            }

            ProcessStartInfo start = new ProcessStartInfo(target, UpdatedArgument);
            start.UseShellExecute = false;
            start.WorkingDirectory = Path.GetDirectoryName(target);
            Process.Start(start);
        }

        /// <summary>Removes leftovers from a previous update. Safe to call on every launch.</summary>
        public static void CleanupAfterUpdate()
        {
            string target;
            try { target = Application.ExecutablePath; }
            catch { return; }

            ThreadPool.QueueUserWorkItem(delegate
            {
                int attempt;
                for (attempt = 0; attempt < 20; attempt++)
                {
                    // The previous process may still be shutting down.
                    if (TryDelete(target + ".old") && TryDelete(target + ".download"))
                        return;
                    Thread.Sleep(500);
                }
            });
        }

        public static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(tag))
                return false;
            string clean = tag.Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(1);
            int dash = clean.IndexOfAny(new char[] { '-', '+' });
            if (dash >= 0)
                return false; // Pre-release suffixes are never offered automatically.
            Version parsed;
            if (!Version.TryParse(clean, out parsed))
                return false;
            version = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version v)
        {
            if (v == null)
                return new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static string ParseChecksum(string text)
        {
            if (text == null)
                return null;
            // Accepts "<hash>" or the sha256sum style "<hash>  Maela.exe".
            string[] parts = text.Split(new char[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length != 64)
                return null;
            string token = parts[0];
            int i;
            for (i = 0; i < token.Length; i++)
            {
                if (!Uri.IsHexDigit(token[i]))
                    return null;
            }
            return token.ToLowerInvariant();
        }

        private static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                int i;
                for (i = 0; i < hash.Length; i++)
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static bool IsGitHubUrl(string url)
        {
            Uri uri;
            if (String.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;
            // Only accept assets attached to this repository's own releases.
            string releasePath = "/" + RepositoryOwner + "/" + RepositoryName + "/releases/download/";
            return uri.Scheme == Uri.UriSchemeHttps &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith(releasePath, StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] Download(string url, string accept, int maxBytes, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072; // TLS 1.2
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "Maela/" + CurrentVersionText + " (+" + ReleasesPage + ")";
            request.Accept = accept;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ResponseUri == null || response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidOperationException("Update download was redirected to an insecure address.");
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream memory = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            memory.Write(buffer, 0, read);
                            if (memory.Length > maxBytes)
                                throw new InvalidOperationException("Update download is larger than expected.");
                        }
                        return memory.ToArray();
                    }
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse error = ex.Response as HttpWebResponse;
                if (error != null && error.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("No published release was found.");
                throw new InvalidOperationException("Could not reach GitHub: " + ex.Message);
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value is string ? (string)value : String.Empty;
        }

        private static bool GetBool(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool && (bool)value;
        }
    }

    /// <summary>Prompt shown when a newer release is available.</summary>
    internal sealed class UpdateDialog : Form
    {
        public const DialogResult Skip = DialogResult.Ignore;

        public UpdateDialog(UpdateInfo info, bool inCall)
        {
            Text = "mæla update";
            try { Icon = Branding.LoadAppIcon(); } catch { }
            ClientSize = new Size(520, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(49, 51, 56);
            ForeColor = Color.FromArgb(242, 243, 245);
            Font = new Font("Segoe UI", 9F);

            Label title = new Label();
            title.AutoSize = true;
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Text = "mæla " + Updater.FormatVersion(info.Version) + " is available";
            title.Location = new Point(22, 18);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.AutoSize = false;
            subtitle.Size = new Size(476, 40);
            subtitle.Location = new Point(24, 54);
            subtitle.ForeColor = Color.FromArgb(181, 186, 193);
            subtitle.Text = "You have " + Updater.CurrentVersionText + "." +
                (Updater.CanInstall(info)
                    ? (inCall ? " Installing restarts mæla and ends your current room session." : " Installing takes a few seconds and restarts mæla.")
                    : " This release has no verified automatic download, so it will open in your browser.");
            Controls.Add(subtitle);

            TextBox notes = new TextBox();
            notes.Multiline = true;
            notes.ReadOnly = true;
            notes.ScrollBars = ScrollBars.Vertical;
            notes.BorderStyle = BorderStyle.FixedSingle;
            notes.BackColor = Color.FromArgb(30, 31, 34);
            notes.ForeColor = Color.FromArgb(219, 222, 225);
            notes.Location = new Point(24, 100);
            notes.Size = new Size(472, 196);
            string body = String.IsNullOrWhiteSpace(info.Notes) ? "No release notes were provided." : info.Notes.Trim();
            notes.Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
            Controls.Add(notes);

            Button install = MakeButton(Updater.CanInstall(info) ? "Update now" : "Open download", true);
            install.Location = new Point(366, 310);
            install.Size = new Size(130, 34);
            install.DialogResult = DialogResult.OK;
            Controls.Add(install);

            Button later = MakeButton("Later", false);
            later.Location = new Point(262, 310);
            later.Size = new Size(96, 34);
            later.DialogResult = DialogResult.Cancel;
            Controls.Add(later);

            Button skip = MakeButton("Skip this version", false);
            skip.Location = new Point(24, 310);
            skip.Size = new Size(140, 34);
            skip.DialogResult = Skip;
            Controls.Add(skip);

            AcceptButton = install;
            CancelButton = later;
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(63, 65, 71);
            b.BackColor = primary ? Color.FromArgb(88, 101, 242) : Color.FromArgb(43, 45, 49);
            b.ForeColor = Color.White;
            b.Cursor = Cursors.Hand;
            return b;
        }
    }

    /// <summary>Minimal JSON reader: objects become Dictionary, arrays List, numbers double.</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null)
                return null;
            int index = 0;
            object value = ParseValue(json, ref index, 0);
            SkipWhitespace(json, ref index);
            return value;
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 64)
                throw new FormatException("JSON is nested too deeply.");
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                throw new FormatException("Unexpected end of JSON.");

            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return map; }
                while (true)
                {
                    SkipWhitespace(s, ref i);
                    string key = ParseString(s, ref i);
                    SkipWhitespace(s, ref i);
                    Expect(s, ref i, ':');
                    map[key] = ParseValue(s, ref i, depth + 1);
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return map;
                }
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i, depth + 1));
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            if (c == '"')
                return ParseString(s, ref i);
            if (Matches(s, i, "true")) { i += 4; return true; }
            if (Matches(s, i, "false")) { i += 5; return false; }
            if (Matches(s, i, "null")) { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                i++;
            double number;
            if (i == start || !Double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new FormatException("Invalid JSON value.");
            return number;
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder builder = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                    return builder.ToString();
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (i >= s.Length)
                    break;
                char e = s[i++];
                switch (e)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length)
                            throw new FormatException("Invalid JSON escape.");
                        builder.Append((char)Int32.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException("Invalid JSON escape.");
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
                throw new FormatException("Expected '" + c + "' in JSON.");
            i++;
        }

        private static bool Matches(string s, int i, string word)
        {
            return String.CompareOrdinal(s, i, word, 0, word.Length) == 0;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && Char.IsWhiteSpace(s[i]))
                i++;
        }
    }
}

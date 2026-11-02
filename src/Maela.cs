using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

[assembly: System.Reflection.AssemblyTitle("m\u00E6la")]
[assembly: System.Reflection.AssemblyProduct("m\u00E6la")]
[assembly: System.Reflection.AssemblyDescription("Private self-hosted voice rooms")]
[assembly: System.Reflection.AssemblyCompany("m\u00E6la")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 BrostrodamusTheWeez0rd. MIT License.")]
// AssemblyVersion / AssemblyFileVersion are generated from the VERSION file by scripts\build_ci.cmd.

namespace Maela
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A locked-down stream viewer (see StreamViewerHost): just show the stream.
            if (args != null && args.Length >= 3 && args[0] == "--watch")
            {
                StreamViewerForm.RunHelper(args[1], args[2], args.Length >= 4 && args[3] == "gpu", args.Length >= 5 ? args[4] : null);
                return;
            }

            bool justUpdated = false;
            int i;
            for (i = 0; args != null && i < args.Length; i++)
            {
                if (String.Equals(args[i], Updater.UpdatedArgument, StringComparison.OrdinalIgnoreCase))
                    justUpdated = true;
            }

            // One copy at a time: a second launch (say, from the Start menu while mæla sits in
            // the tray) just brings the running one to the front. Right after an update the old
            // copy may still be closing, so wait a moment for it.
            SingleInstance instance = SingleInstance.Acquire(justUpdated ? 15000 : 0);
            if (instance == null)
            {
                // Another copy is running. If it answers, just bring it forward. If it is frozen,
                // close it so this start-up is clean: no stuck window, no stale connection.
                if (SingleInstance.RunningCopyResponds(4000))
                {
                    SingleInstance.ShowRunningCopy();
                    return;
                }
                SingleInstance.EndFrozenCopies();
                instance = SingleInstance.Acquire(5000);
                if (instance == null)
                {
                    MessageBox.Show("Another copy of m\u00E6la is still running and not responding. Close it in Task Manager, then try again.", "m\u00E6la", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            using (instance)
            {

                // If mæla crashes, still tell the room we left so rejoining is instant.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    EmergencyLeave();
                    MessageBox.Show("m\u00E6la ran into a problem and needs to close.\n\n" + e.Exception.Message, "m\u00E6la", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate { EmergencyLeave(); };

                Updater.CleanupAfterUpdate();
                Application.Run(new MainForm(justUpdated));
            }
        }

        /// <summary>Set by the main window: leaves the room without touching the UI.</summary>
        internal static Action LeaveRoomOnCrash;

        private static void EmergencyLeave()
        {
            Action leave = LeaveRoomOnCrash;
            LeaveRoomOnCrash = null;
            if (leave != null)
            {
                try { leave(); } catch { }
            }
        }
    }

    /// <summary>Keeps mæla to one running copy per Windows user.</summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const string Name = "Local\\Maela-7d1f6a52-single-instance";
        /// <summary>Window message that asks the running copy to show itself (-1 if unavailable).</summary>
        public static readonly int ShowMessage = RegisterShowMessage();

        private static int RegisterShowMessage()
        {
            try
            {
                int id = RegisterWindowMessage("Maela-7d1f6a52-show");
                return id == 0 ? -1 : id;
            }
            catch
            {
                return -1;
            }
        }
        private Mutex _mutex;

        public static SingleInstance Acquire(int waitMs)
        {
            Mutex mutex;
            try { mutex = new Mutex(false, Name); }
            catch { return new SingleInstance(); } // Can't tell: don't block startup.
            try
            {
                if (mutex.WaitOne(waitMs, false))
                {
                    SingleInstance instance = new SingleInstance();
                    instance._mutex = mutex;
                    return instance;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous copy crashed while holding it; it's ours now.
                SingleInstance instance = new SingleInstance();
                instance._mutex = mutex;
                return instance;
            }
            mutex.Close();
            return null;
        }

        private const string PingName = "Local\\Maela-7d1f6a52-ping";
        private const string PongName = "Local\\Maela-7d1f6a52-pong";
        private static EventWaitHandle _ping;
        private static EventWaitHandle _pong;

        /// <summary>
        /// Running copy: called from the UI timer. Answering proves the window is alive; a frozen
        /// copy can't answer.
        /// </summary>
        public static void AnswerPing()
        {
            try
            {
                if (_ping == null)
                {
                    _ping = new EventWaitHandle(false, EventResetMode.AutoReset, PingName);
                    _pong = new EventWaitHandle(false, EventResetMode.AutoReset, PongName);
                }
                if (_ping.WaitOne(0, false))
                    _pong.Set();
            }
            catch { }
        }

        /// <summary>New copy: asks the running one to answer; false if it doesn't within the time.</summary>
        public static bool RunningCopyResponds(int timeoutMs)
        {
            try
            {
                using (EventWaitHandle ping = new EventWaitHandle(false, EventResetMode.AutoReset, PingName))
                using (EventWaitHandle pong = new EventWaitHandle(false, EventResetMode.AutoReset, PongName))
                {
                    pong.Reset();
                    ping.Set();
                    return pong.WaitOne(timeoutMs, false);
                }
            }
            catch
            {
                return true; // Can't tell: assume it is fine rather than closing it.
            }
        }

        /// <summary>Closes other copies of this same mæla.exe (used only when they are frozen).</summary>
        public static void EndFrozenCopies()
        {
            try
            {
                Process current = Process.GetCurrentProcess();
                string path = Application.ExecutablePath;
                foreach (Process other in Process.GetProcessesByName(current.ProcessName))
                {
                    try
                    {
                        if (other.Id == current.Id)
                            continue;
                        if (!String.Equals(other.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                            continue;
                        other.Kill();
                        other.WaitForExit(3000);
                    }
                    catch { }
                    finally { other.Dispose(); }
                }
            }
            catch { }
        }

        public static void ShowRunningCopy()
        {
            try { PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        public void Dispose()
        {
            Mutex mutex = _mutex;
            _mutex = null;
            if (mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Close();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>When the viewer decodes streams on the graphics card. Automatic: at 1080p and above.</summary>
    internal enum StreamDecodeMode
    {
        Automatic = 0,
        Always = 1,
        Never = 2
    }

    internal enum RoomPresence
    {
        Unknown,
        Checking,
        Online,
        Offline
    }

    internal static class Branding
    {
        public static Icon LoadAppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                    return icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        public static Image LoadBrandMark()
        {
            try
            {
                using (Stream stream = typeof(Branding).Assembly.GetManifestResourceStream("MaelaMark.png"))
                {
                    if (stream != null)
                    {
                        using (Image image = Image.FromStream(stream))
                            return new Bitmap(image);
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal sealed class SavedRoom
    {
        public string Name = "Voice Room";
        public string Invite = String.Empty;
        public bool Owned;
        public int PreferredPort;
        public bool KeepAlive = true;
        public readonly List<string> ChannelNames = new List<string>();
        public RoomPresence Presence = RoomPresence.Unknown;
        public int MemberCount;
        public int MaxMembers;

        /// <summary>Session-only: set once this room has been probed, so it is not probed again.</summary>
        public bool PresenceChecked;

        /// <summary>Hosted rooms: the room's long-term X25519 private key (stored DPAPI-protected).</summary>
        public byte[] HostKey;

        /// <summary>Hosted rooms: the password to require. Joined rooms: the password to send. Empty = none.</summary>
        public string Password = String.Empty;

        private byte[] _iconBlob, _bannerBlob;
        private Image _iconImage, _bannerImage;

        /// <summary>The room's picture and banner, as compressed pixels (see RoomArt). Null = none.</summary>
        public byte[] IconBlob { get { return _iconBlob; } }
        public byte[] BannerBlob { get { return _bannerBlob; } }

        public void SetArt(byte[] iconBlob, byte[] bannerBlob)
        {
            _iconBlob = iconBlob;
            _bannerBlob = bannerBlob;
            _iconImage = null;
            _bannerImage = null;
        }

        public Image IconImage
        {
            get
            {
                if (_iconImage == null && _iconBlob != null)
                    _iconImage = RoomArt.ToImage(RoomArt.Icon, _iconBlob);
                return _iconImage;
            }
        }

        public Image BannerImage
        {
            get
            {
                if (_bannerImage == null && _bannerBlob != null)
                    _bannerImage = RoomArt.ToImage(RoomArt.Banner, _bannerBlob);
                return _bannerImage;
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class SavedRoomStore
    {
        private static readonly object _fileLock = new object();
        private static bool _saveBlocked; // The rooms file couldn't be read or moved aside; saving would destroy it.

        /// <summary>Tests keep their files away from the real saved rooms.</summary>
        internal static string FolderForTests;

        /// <summary>True when the last Load found a rooms file it can't decrypt. The file is kept.</summary>
        public static bool LoadFailed { get; private set; }

        private static string FilePath
        {
            get
            {
                return Path.Combine(GetFolder(), "rooms-v3.dat");
            }
        }

        private static string UnencryptedFilePath
        {
            get
            {
                return Path.Combine(GetFolder(), "rooms-v2.txt");
            }
        }

        public static List<SavedRoom> Load()
        {
            lock (_fileLock)
            {
                LoadFailed = false;
                _saveBlocked = false;
                RemoveOldRoomFiles();
                string path = FilePath;
                string unencrypted = UnencryptedFilePath;
                if (File.Exists(path))
                {
                    byte[] data;
                    try { data = EncryptedFile.Read(path); }
                    catch
                    {
                        _saveBlocked = true;
                        return new List<SavedRoom>();
                    }
                    if (data != null)
                    {
                        // An older file is only still here if a conversion was interrupted.
                        DeleteQuietly(unencrypted);
                        return Parse(Encoding.UTF8.GetString(data).Split('\n'));
                    }
                    if (!EncryptedFile.SetAside(path))
                        _saveBlocked = true;
                    if (!File.Exists(unencrypted))
                    {
                        LoadFailed = true;
                        return new List<SavedRoom>();
                    }
                }
                return File.Exists(unencrypted) ? ConvertUnencryptedFile(unencrypted) : new List<SavedRoom>();
            }
        }

        /// <summary>The old file is deleted only after the encrypted copy has been read back.</summary>
        private static List<SavedRoom> ConvertUnencryptedFile(string unencrypted)
        {
            string[] lines;
            try { lines = File.ReadAllLines(unencrypted); }
            catch
            {
                _saveBlocked = true;
                return new List<SavedRoom>();
            }
            List<SavedRoom> rooms = Parse(lines);
            if (Save(rooms))
            {
                try
                {
                    byte[] check = EncryptedFile.Read(FilePath);
                    if (check != null && Parse(Encoding.UTF8.GetString(check).Split('\n')).Count == rooms.Count)
                        DeleteQuietly(unencrypted);
                }
                catch { }
            }
            return rooms;
        }

        private static void DeleteQuietly(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        private static List<SavedRoom> Parse(string[] lines)
        {
            List<SavedRoom> result = new List<SavedRoom>();
            try
            {
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length < 4)
                        continue;
                    SavedRoom room = new SavedRoom();
                    room.Owned = parts[0] == "1";
                    int p;
                    Int32.TryParse(parts[1], out p);
                    room.PreferredPort = p;
                    room.Name = Decode(parts[2]);
                    room.Invite = Decode(parts[3]);
                    if (parts.Length >= 5)
                        room.KeepAlive = parts[4] != "0";
                    if (parts.Length >= 6)
                    {
                        string channelText = Decode(parts[5]);
                        string[] channelNames = channelText.Replace("\r\n", "\n").Split('\n');
                        int c;
                        for (c = 0; c < channelNames.Length; c++)
                        {
                            string channelName = channelNames[c].Trim();
                            if (channelName.Length > 0)
                                room.ChannelNames.Add(channelName);
                        }
                    }
                    if (parts.Length >= 7 && parts[6].Length > 0)
                    {
                        byte[] key = SecretStore.Unprotect(parts[6]);
                        if (key != null && key.Length == X25519.KeySize)
                            room.HostKey = key;
                    }
                    if (parts.Length >= 8 && parts[7].Length > 0)
                    {
                        byte[] password = SecretStore.Unprotect(parts[7]);
                        room.Password = password == null ? String.Empty : Encoding.UTF8.GetString(password);
                    }
                    if (parts.Length >= 10)
                    {
                        byte[] icon = parts[8].Length > 0 ? Convert.FromBase64String(parts[8]) : null;
                        byte[] banner = parts[9].Length > 0 ? Convert.FromBase64String(parts[9]) : null;
                        room.SetArt(RoomArt.Unpack(RoomArt.Icon, icon) != null ? icon : null, RoomArt.Unpack(RoomArt.Banner, banner) != null ? banner : null);
                    }
                    if (room.Owned && room.ChannelNames.Count == 0)
                        room.ChannelNames.Add("General");
                    if (String.IsNullOrWhiteSpace(room.Name))
                        room.Name = "Voice Room";
                    result.Add(room);
                }
            }
            catch { }
            return result;
        }

        public static bool Save(IList<SavedRoom> rooms)
        {
            lock (_fileLock)
            {
                if (_saveBlocked)
                    return false;
                try
                {
                    string folder = GetFolder();
                    Directory.CreateDirectory(folder);
                    List<string> lines = new List<string>();
                    int i;
                    for (i = 0; i < rooms.Count; i++)
                    {
                        SavedRoom room = rooms[i];
                        string channelText = room.ChannelNames == null ? String.Empty : String.Join("\n", room.ChannelNames.ToArray());
                        string key = room.HostKey == null ? String.Empty : SecretStore.Protect(room.HostKey);
                        string password = String.IsNullOrEmpty(room.Password) ? String.Empty : SecretStore.Protect(Encoding.UTF8.GetBytes(room.Password));
                        lines.Add((room.Owned ? "1" : "0") + "|" + room.PreferredPort + "|" + Encode(room.Name) + "|" + Encode(room.Invite) + "|" + (room.KeepAlive ? "1" : "0") + "|" + Encode(channelText) + "|" + key + "|" + password
                            + "|" + (room.IconBlob == null ? String.Empty : Convert.ToBase64String(room.IconBlob))
                            + "|" + (room.BannerBlob == null ? String.Empty : Convert.ToBase64String(room.BannerBlob)));
                    }
                    EncryptedFile.Write(FilePath, Encoding.UTF8.GetBytes(String.Join("\n", lines.ToArray())));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string GetFolder()
        {
            if (FolderForTests != null)
                return FolderForTests;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela");
        }

        /// <summary>
        /// mæla 0.7 starts with a fresh room list: older room codes carry no security key and
        /// can't be used with encrypted rooms, so the old files are removed.
        /// </summary>
        private static void RemoveOldRoomFiles()
        {
            string[] old = new string[] { Path.Combine(GetFolder(), "rooms.txt") };
            int i;
            for (i = 0; i < old.Length; i++)
            {
                try
                {
                    if (File.Exists(old[i]))
                        File.Delete(old[i]);
                }
                catch { }
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value == null ? String.Empty : value));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return String.Empty; }
        }
    }

    internal sealed class AppPreferences
    {
        public string DisplayName = Environment.UserName;
        public uint InputDeviceId = NativeMethods.WAVE_MAPPER;
        public uint OutputDeviceId = NativeMethods.WAVE_MAPPER;
        public int MicVolumePercent = 100;
        public int MasterVolumePercent = 100;
        public bool EchoCancellation = true;
        public bool AutoUpdate = true;
        public string SkippedVersion = String.Empty;
        public string Theme = "maela-red";
        public bool NoiseSuppression = true;
        public bool SetupDone;
        public bool SoundEffects = true;
        public bool AutoGain = true;
        public bool CloseToTray = true;
        public bool TrayHintShown;
        public string IconRefreshedFor = String.Empty;
        public string Status = String.Empty;
        public bool AllowDirectStreams = true; // Off: streams go through the host, so viewers don't learn your IP.
        public bool StreamNoticeShown;
        public FriendStatus FriendStatus = FriendStatus.Offline; // Friends stay off until you choose to go online.
        public bool FriendsNoticeShown;
        public bool WindowsSetupOffered; // Asked once whether to set up the firewall and Defender.
        public StreamDecodeMode StreamDecode = StreamDecodeMode.Automatic;
        public int ShareHeight = 1080;
        public int ShareFps = 60;
        public bool ShareAudio = true;

        public AppPreferences Clone()
        {
            AppPreferences p = new AppPreferences();
            p.DisplayName = DisplayName;
            p.InputDeviceId = InputDeviceId;
            p.OutputDeviceId = OutputDeviceId;
            p.MicVolumePercent = MicVolumePercent;
            p.MasterVolumePercent = MasterVolumePercent;
            p.EchoCancellation = EchoCancellation;
            p.AutoUpdate = AutoUpdate;
            p.SkippedVersion = SkippedVersion;
            p.Theme = Theme;
            p.NoiseSuppression = NoiseSuppression;
            p.SetupDone = SetupDone;
            p.SoundEffects = SoundEffects;
            p.AutoGain = AutoGain;
            p.CloseToTray = CloseToTray;
            p.TrayHintShown = TrayHintShown;
            p.IconRefreshedFor = IconRefreshedFor;
            p.Status = Status;
            p.AllowDirectStreams = AllowDirectStreams;
            p.StreamNoticeShown = StreamNoticeShown;
            p.FriendStatus = FriendStatus;
            p.FriendsNoticeShown = FriendsNoticeShown;
            p.WindowsSetupOffered = WindowsSetupOffered;
            p.StreamDecode = StreamDecode;
            p.ShareHeight = ShareHeight;
            p.ShareFps = ShareFps;
            p.ShareAudio = ShareAudio;
            return p;
        }
    }

    internal static class PreferencesStore
    {
        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela", "settings.txt"); }
        }

        public static AppPreferences Load()
        {
            AppPreferences p = new AppPreferences();
            try
            {
                string loadPath = FilePath;
                if (!File.Exists(loadPath))
                    return p;
                string[] lines = File.ReadAllLines(loadPath);
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = lines[i].Substring(0, eq);
                    string value = lines[i].Substring(eq + 1);
                    if (key == "name")
                    {
                        try { p.DisplayName = Encoding.UTF8.GetString(Convert.FromBase64String(value)); } catch { }
                    }
                    else if (key == "in")
                    {
                        uint v; if (UInt32.TryParse(value, out v)) p.InputDeviceId = v;
                    }
                    else if (key == "out")
                    {
                        uint v; if (UInt32.TryParse(value, out v)) p.OutputDeviceId = v;
                    }
                    else if (key == "mic")
                    {
                        int v; if (Int32.TryParse(value, out v)) p.MicVolumePercent = Clamp(v, 0, 150);
                    }
                    else if (key == "master")
                    {
                        int v; if (Int32.TryParse(value, out v)) p.MasterVolumePercent = Clamp(v, 0, 150);
                    }
                    else if (key == "aec")
                    {
                        p.EchoCancellation = value != "0";
                    }
                    else if (key == "updates")
                    {
                        p.AutoUpdate = value != "0";
                    }
                    else if (key == "skip")
                    {
                        p.SkippedVersion = value.Trim();
                    }
                    else if (key == "theme")
                    {
                        p.Theme = value.Trim();
                    }
                    else if (key == "noise")
                    {
                        p.NoiseSuppression = value != "0";
                    }
                    else if (key == "setup")
                    {
                        p.SetupDone = value == "1";
                    }
                    else if (key == "sfx")
                    {
                        p.SoundEffects = value != "0";
                    }
                    else if (key == "agc")
                    {
                        p.AutoGain = value != "0";
                    }
                    else if (key == "tray")
                    {
                        p.CloseToTray = value != "0";
                    }
                    else if (key == "trayhint")
                    {
                        p.TrayHintShown = value == "1";
                    }
                    else if (key == "directstreams2")
                        p.AllowDirectStreams = value != "0";
                    else if (key == "shareaudio")
                        p.ShareAudio = value != "0";
                    else if (key == "streamnotice")
                        p.StreamNoticeShown = value == "1";
                    else if (key == "friendstatus")
                    {
                        byte v;
                        if (Byte.TryParse(value, out v) && v <= (byte)FriendStatus.Busy) p.FriendStatus = (FriendStatus)v;
                    }
                    else if (key == "friendsnotice")
                        p.FriendsNoticeShown = value == "1";
                    else if (key == "windowssetup")
                        p.WindowsSetupOffered = value == "1";
                    else if (key == "streamdecode")
                        p.StreamDecode = value == "1" ? StreamDecodeMode.Always : value == "2" ? StreamDecodeMode.Never : StreamDecodeMode.Automatic;
                    else if (key == "shareheight")
                    {
                        int v; if (Int32.TryParse(value, out v) && (v == 720 || v == 1080 || v == 1440 || v == 2160)) p.ShareHeight = v;
                    }
                    else if (key == "sharefps")
                    {
                        int v; if (Int32.TryParse(value, out v) && (v == 30 || v == 60 || v == 120)) p.ShareFps = v;
                    }
                    else if (key == "status")
                    {
                        try { p.Status = VoipEngine.CleanStatus(Encoding.UTF8.GetString(Convert.FromBase64String(value))); } catch { }
                    }
                    else if (key == "iconver")
                    {
                        p.IconRefreshedFor = value.Trim();
                    }
                }
            }
            catch { }
            return p;
        }

        public static void Save(AppPreferences p)
        {
            if (p == null)
                return;
            try
            {
                string folder = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(folder);
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.DisplayName == null ? String.Empty : p.DisplayName));
                File.WriteAllLines(FilePath, new string[]
                {
                    "name=" + encoded,
                    "in=" + p.InputDeviceId,
                    "out=" + p.OutputDeviceId,
                    "mic=" + p.MicVolumePercent,
                    "master=" + p.MasterVolumePercent,
                    "aec=" + (p.EchoCancellation ? "1" : "0"),
                    "updates=" + (p.AutoUpdate ? "1" : "0"),
                    "skip=" + (p.SkippedVersion == null ? String.Empty : p.SkippedVersion.Trim()),
                    "theme=" + (p.Theme ?? "maela-red"),
                    "noise=" + (p.NoiseSuppression ? "1" : "0"),
                    "setup=" + (p.SetupDone ? "1" : "0"),
                    "sfx=" + (p.SoundEffects ? "1" : "0"),
                    "agc=" + (p.AutoGain ? "1" : "0"),
                    "tray=" + (p.CloseToTray ? "1" : "0"),
                    "trayhint=" + (p.TrayHintShown ? "1" : "0"),
                    "iconver=" + (p.IconRefreshedFor ?? String.Empty),
                    "status=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Status ?? String.Empty)),
                    "directstreams2=" + (p.AllowDirectStreams ? "1" : "0"),
                    "streamnotice=" + (p.StreamNoticeShown ? "1" : "0"),
                    "friendstatus=" + (byte)p.FriendStatus,
                    "friendsnotice=" + (p.FriendsNoticeShown ? "1" : "0"),
                    "windowssetup=" + (p.WindowsSetupOffered ? "1" : "0"),
                    "streamdecode=" + (int)p.StreamDecode,
                    "shareheight=" + p.ShareHeight,
                    "sharefps=" + p.ShareFps,
                    "shareaudio=" + (p.ShareAudio ? "1" : "0")
                });
            }
            catch { }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>
    /// Room codes: everything a friend needs to reach and verify a host, in one pasteable string.
    ///
    /// Version 2 layout (19 bytes, Base32, shown as M-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX):
    ///   version (1) | IPv4 (4) | UDP port (2) | host key fingerprint (10) | CRC-16 (2)
    /// The fingerprint is what makes the connection secure: the client refuses any host whose
    /// key doesn't match it. The bytes are lightly scrambled so codes don't visibly contain an
    /// IP address, but the endpoint is not secret: anyone with the code can recover it.
    /// </summary>
    internal static class RoomCode
    {
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const byte CodeVersion = 2;
        private const int FingerprintLength = 10;
        private const int DataLength = 19;
        private const int CharLength = 31;
        private static readonly byte[] Mask = new byte[]
        {
            0xA7, 0x3D, 0xC1, 0x58, 0x92, 0xE4, 0x6B, 0xF0, 0x1E, 0x85, 0x3B, 0xD6, 0x47, 0x9A, 0x2C, 0xE1, 0x70, 0x0F, 0xB4
        };

        public static string Encode(IPAddress ip, int port, byte[] fingerprint)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Room codes currently require IPv4.");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            if (fingerprint == null || fingerprint.Length != FingerprintLength)
                throw new ArgumentException("A host key fingerprint is required.");

            byte[] data = new byte[DataLength];
            data[0] = CodeVersion;
            Buffer.BlockCopy(ip.GetAddressBytes(), 0, data, 1, 4);
            data[5] = (byte)((port >> 8) & 0xFF);
            data[6] = (byte)(port & 0xFF);
            Buffer.BlockCopy(fingerprint, 0, data, 7, FingerprintLength);
            ushort crc = Crc16(data, DataLength - 2);
            data[17] = (byte)(crc >> 8);
            data[18] = (byte)(crc & 0xFF);

            int i;
            for (i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] ^ Mask[i]);

            string raw = Base32Encode(data);
            if (raw.Length != CharLength)
                throw new InvalidOperationException("Could not create room code.");
            return "M-" + raw.Substring(0, 5) + "-" + raw.Substring(5, 5) + "-" + raw.Substring(10, 5) + "-" +
                raw.Substring(15, 5) + "-" + raw.Substring(20, 5) + "-" + raw.Substring(25, 6);
        }

        public static bool IsValid(string code)
        {
            IPAddress ip;
            int port;
            byte[] fingerprint;
            return TryDecode(code, out ip, out port, out fingerprint);
        }

        /// <summary>True for a well-formed code from mæla 0.6 or earlier (no security key).</summary>
        public static bool IsOldFormat(string code)
        {
            string clean = Clean(code);
            return clean.Length == 14 && clean[0] == 'M';
        }

        public static bool TryDecode(string code, out IPAddress ip, out int port, out byte[] fingerprint)
        {
            ip = null;
            port = 0;
            fingerprint = null;
            string clean = Clean(code);
            if (clean.Length != CharLength + 1 || clean[0] != 'M')
                return false;

            string body = clean.Substring(1);
            int lastIndex = Alphabet.IndexOf(body[body.Length - 1]);
            if (lastIndex < 0 || (lastIndex & 7) != 0)
                return false; // Unused padding bits must be zero.

            byte[] data;
            if (!TryBase32Decode(body, out data) || data.Length != DataLength)
                return false;

            int i;
            for (i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] ^ Mask[i]);

            ushort crc = Crc16(data, DataLength - 2);
            if (data[0] != CodeVersion || data[17] != (byte)(crc >> 8) || data[18] != (byte)(crc & 0xFF))
                return false;

            int decodedPort = (data[5] << 8) | data[6];
            if (decodedPort < 1 || decodedPort > 65535)
                return false;

            ip = new IPAddress(new byte[] { data[1], data[2], data[3], data[4] });
            port = decodedPort;
            fingerprint = new byte[FingerprintLength];
            Buffer.BlockCopy(data, 7, fingerprint, 0, FingerprintLength);
            return true;
        }

        internal static string Clean(string code)
        {
            if (String.IsNullOrWhiteSpace(code))
                return String.Empty;
            StringBuilder builder = new StringBuilder();
            string upper = code.Trim().ToUpperInvariant();
            int i;
            for (i = 0; i < upper.Length; i++)
            {
                char c = upper[i];
                if (c == '-' || Char.IsWhiteSpace(c))
                    continue;
                builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>CRC-16/CCITT-FALSE. Catches every single-character typo and almost all others.</summary>
        internal static ushort Crc16(byte[] data, int count)
        {
            ushort crc = 0xFFFF;
            int i;
            for (i = 0; i < count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                int bit;
                for (bit = 0; bit < 8; bit++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        internal static string Base32Encode(byte[] data)
        {
            StringBuilder result = new StringBuilder();
            uint buffer = 0;
            int bits = 0;
            int i;
            for (i = 0; i < data.Length; i++)
            {
                buffer = (buffer << 8) | data[i];
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    result.Append(Alphabet[(int)((buffer >> bits) & 31)]);
                }
            }
            if (bits > 0)
                result.Append(Alphabet[(int)((buffer << (5 - bits)) & 31)]);
            return result.ToString();
        }

        internal static bool TryBase32Decode(string value, out byte[] data)
        {
            data = null;
            if (String.IsNullOrEmpty(value))
                return false;
            List<byte> output = new List<byte>();
            uint buffer = 0;
            int bits = 0;
            int i;
            for (i = 0; i < value.Length; i++)
            {
                int index = Alphabet.IndexOf(value[i]);
                if (index < 0)
                    return false;
                buffer = (buffer << 5) | (uint)index;
                bits += 5;
                while (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            data = output.ToArray();
            return true;
        }
    }

    internal sealed class ProbeResult
    {
        public bool Online;
    }

    internal static class RoomProbe
    {
        private const uint Magic = 0x32564F52;

        /// <summary>
        /// Asks whether a room is online. The reply carries no room details (name, members)
        /// because probes are unauthenticated; those are only shared inside the encrypted session.
        /// </summary>
        public static ProbeResult Check(string invite, int timeoutMs)
        {
            ProbeResult result = new ProbeResult();
            IPAddress ip;
            int port;
            byte[] fingerprint;
            if (!RoomCode.TryDecode(invite, out ip, out port, out fingerprint))
                return result;
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = timeoutMs;
                    socket.SendTimeout = timeoutMs;
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    byte[] packet = new byte[VoipEngine.OuterHeaderBytes];
                    Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, packet, 0, 4);
                    packet[4] = VoipEngine.WireVersion;
                    packet[5] = VoipEngine.OuterProbe;
                    socket.SendTo(packet, new IPEndPoint(ip, port));
                    byte[] buffer = new byte[64];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length = socket.ReceiveFrom(buffer, ref remote);
                    result.Online = length == VoipEngine.OuterHeaderBytes && BitConverter.ToUInt32(buffer, 0) == Magic &&
                        buffer[4] == VoipEngine.WireVersion && buffer[5] == VoipEngine.OuterProbeReply;
                }
            }
            catch { }
            return result;
        }
    }

    internal sealed class UpnpPortMapping : IDisposable
    {
        private readonly Uri _controlUrl;
        private readonly string _serviceType;
        private readonly int _externalPort;
        private bool _disposed;

        public string ExternalIp { get; private set; }
        public int ExternalPort { get { return _externalPort; } }

        private IPAddress _natPmpGateway;
        private int _natPmpInternalPort;

        private const int NatPmpLeaseSeconds = 7200;
        private const int FiniteLeaseSeconds = 7200;
        private const int PermanentRefreshSeconds = 1800; // Some routers forget "permanent" mappings when they restart.

        private readonly object _renewLock = new object();
        private System.Threading.Timer _renewTimer;
        private int _internalPort;
        private string _internalClient;
        private int _leaseSeconds;

        private UpnpPortMapping(Uri controlUrl, string serviceType, int externalPort, string externalIp)
        {
            _controlUrl = controlUrl;
            _serviceType = serviceType;
            _externalPort = externalPort;
            ExternalIp = externalIp;
        }

        /// <summary>Asks the router for your current public address. Only your router is contacted.</summary>
        public string RefreshExternalIp()
        {
            if (_controlUrl == null || _disposed)
                return null;
            string response;
            if (!Soap(_controlUrl, _serviceType, "GetExternalIPAddress", String.Empty, out response))
                return null;
            string ip = ExtractSoapValue(response, "NewExternalIPAddress");
            if (!String.IsNullOrWhiteSpace(ip))
                ExternalIp = ip;
            return ip;
        }

        public static UpnpPortMapping TryCreate(int port, string internalClient, out string failure)
        {
            failure = String.Empty;
            List<Uri> tried = new List<Uri>();

            // The patient multicast search first: some routers only answer when asked one thing at a time.
            Uri first = DiscoverGatewayMulticast();
            if (first != null)
            {
                tried.Add(first);
                UpnpPortMapping mapping = TryCreateAt(first, port, internalClient);
                if (mapping != null)
                    return mapping;
            }

            foreach (Uri location in DiscoverGatewayLocations())
            {
                if (tried.Contains(location))
                    continue;
                tried.Add(location);
                UpnpPortMapping mapping = TryCreateAt(location, port, internalClient);
                if (mapping != null)
                    return mapping;
            }

            UpnpPortMapping natPmp = TryNatPmp(port); // Many routers that don't answer UPnP speak NAT-PMP.
            if (natPmp == null)
                failure = tried.Count == 0 ? "no router answered" : "the router answered but refused the port";
            return natPmp;
        }

        internal static UpnpPortMapping TryCreateAt(Uri location, int port, string internalClient)
        {
            // Tell the router the address it can actually reach; with a VPN up, the PC's
            // default route is the VPN's address, which the router refuses.
            string client = LocalAddressToward(location, internalClient);
            foreach (KeyValuePair<string, Uri> service in FindWanServices(location))
            {
                UpnpPortMapping mapping = TryService(service.Value, service.Key, port, client);
                if (mapping != null)
                    return mapping;
            }
            return null;
        }

        private static UpnpPortMapping TryService(Uri controlUrl, string serviceType, int port, string internalClient)
        {
            List<int> candidates = new List<int>();
            candidates.Add(port);

            Random random = new Random(unchecked(Environment.TickCount ^ port));
            int attempts;
            for (attempts = 0; attempts < 12; attempts++)
            {
                int candidate = random.Next(40000, 65001);
                if (!candidates.Contains(candidate))
                    candidates.Add(candidate);
            }

            int lease = 0;
            int i;
            for (i = 0; i < candidates.Count; i++)
            {
                int externalPort = candidates[i];
                string fault;
                bool added = AddMapping(controlUrl, serviceType, externalPort, port, internalClient, lease, out fault);
                if (!added && String.IsNullOrEmpty(fault))
                    return null; // No answer at all: more tries would only keep the user waiting.
                if (!added && lease == 0 && !IsPortConflict(fault))
                {
                    // Some routers won't accept a mapping with no expiry.
                    string finiteFault;
                    added = AddMapping(controlUrl, serviceType, externalPort, port, internalClient, FiniteLeaseSeconds, out finiteFault);
                    if (added)
                        lease = FiniteLeaseSeconds;
                }
                if (!added)
                    continue;

                string externalIp = String.Empty;
                string response;
                if (Soap(controlUrl, serviceType, "GetExternalIPAddress", String.Empty, out response))
                    externalIp = ExtractSoapValue(response, "NewExternalIPAddress");

                UpnpPortMapping mapping = new UpnpPortMapping(controlUrl, serviceType, externalPort, externalIp);
                mapping._internalPort = port;
                mapping._internalClient = internalClient;
                mapping._leaseSeconds = lease;
                mapping.StartRenewing(lease > 0 ? lease / 2 : PermanentRefreshSeconds);
                return mapping;
            }

            return null;
        }

        private static bool AddMapping(Uri controlUrl, string serviceType, int externalPort, int internalPort, string internalClient, int lease, out string fault)
        {
            string body =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + externalPort + "</NewExternalPort>" +
                "<NewProtocol>UDP</NewProtocol>" +
                "<NewInternalPort>" + internalPort + "</NewInternalPort>" +
                "<NewInternalClient>" + EscapeXml(internalClient) + "</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>Maela</NewPortMappingDescription>" +
                "<NewLeaseDuration>" + lease + "</NewLeaseDuration>";
            string response;
            bool added = Soap(controlUrl, serviceType, "AddPortMapping", body, out response);
            fault = added ? String.Empty : response;
            return added;
        }

        // 718 ConflictInMappingEntry, 724 SamePortValuesRequired, 727 ExternalPortOnlySupportsWildcard, 728 NoPortMapsAvailable.
        private static bool IsPortConflict(string fault)
        {
            string code = ExtractSoapValue(fault, "errorCode");
            return code == "718" || code == "724" || code == "727" || code == "728";
        }

        private void StartRenewing(int seconds)
        {
            int ms = seconds * 1000;
            _renewTimer = new System.Threading.Timer(delegate { Renew(); }, null, ms, ms);
        }

        private void Renew()
        {
            lock (_renewLock)
            {
                if (_disposed)
                    return;
                try
                {
                    if (_natPmpGateway != null)
                    {
                        NatPmpRequest(_natPmpGateway, _natPmpInternalPort, _externalPort, NatPmpLeaseSeconds);
                        return;
                    }
                    string ignored;
                    AddMapping(_controlUrl, _serviceType, _externalPort, _internalPort, _internalClient, _leaseSeconds, out ignored);
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            lock (_renewLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            if (_renewTimer != null)
                _renewTimer.Dispose();

            if (_natPmpGateway != null)
            {
                try { NatPmpRequest(_natPmpGateway, _natPmpInternalPort, 0, 0); } catch { }
                return;
            }
            try
            {
                string body =
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + _externalPort + "</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>";
                string ignored;
                Soap(_controlUrl, _serviceType, "DeletePortMapping", body, out ignored);
            }
            catch
            {
            }
        }

        /// <summary>True for addresses nobody on the internet can reach: a router showing one has another router or the provider's NAT in front of it.</summary>
        internal static bool IsNonPublic(string address)
        {
            IPAddress parsed;
            if (!IPAddress.TryParse(address ?? String.Empty, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] b = parsed.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 ||
                (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                (b[0] == 169 && b[1] == 254) ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168);
        }

        private static string LocalAddressToward(Uri location, string fallback)
        {
            try
            {
                IPAddress host;
                if (!IPAddress.TryParse(location.Host, out host))
                    return fallback;
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(host, 1900)); // Sends nothing; only picks the route.
                    return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
                }
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Asks on the multicast address for one search target at a time, each on its own socket.</summary>
        private static Uri DiscoverGatewayMulticast()
        {
            string[] searchTargets = new string[]
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "urn:schemas-upnp-org:service:WANPPPConnection:1"
            };

            foreach (string target in searchTargets)
            {
                Socket socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.ReceiveTimeout = 1400;
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                    byte[] bytes = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: " + target + "\r\n\r\n");
                    socket.SendTo(bytes, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

                    DateTime until = DateTime.UtcNow.AddMilliseconds(1500);
                    byte[] buffer = new byte[8192];
                    while (DateTime.UtcNow < until)
                    {
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int count = socket.ReceiveFrom(buffer, ref remote);
                        string location = HeaderValue(Encoding.UTF8.GetString(buffer, 0, count), "LOCATION");
                        Uri parsed;
                        if (!String.IsNullOrWhiteSpace(location) && Uri.TryCreate(location.Trim(), UriKind.Absolute, out parsed))
                            return parsed;
                    }
                }
                catch
                {
                }
                finally
                {
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Finds the router's UPnP description. Asks on the multicast address and also straight
        /// at each default gateway: Windows Firewall lets a gateway's direct reply through even
        /// when mæla has no firewall rule yet, while replies to multicast can be dropped.
        /// </summary>
        private static List<Uri> DiscoverGatewayLocations()
        {
            string[] searchTargets = new string[]
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "urn:schemas-upnp-org:service:WANPPPConnection:1",
                "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                "urn:schemas-upnp-org:service:WANIPConnection:2"
            };
            IPEndPoint multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            List<IPEndPoint> direct = new List<IPEndPoint>();
            foreach (IPAddress gateway in Gateways())
                direct.Add(new IPEndPoint(gateway, 1900));

            List<Uri> found = new List<Uri>();
            List<Socket> sockets = new List<Socket>();
            try
            {
                Socket main = OpenSearchSocket(IPAddress.Any);
                sockets.Add(main);
                // Windows sends multicast out of one adapter only, which may be a VPN or virtual
                // adapter, so ask from every adapter.
                foreach (IPAddress local in LocalAddresses())
                {
                    try
                    {
                        Socket socket = OpenSearchSocket(local);
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                        sockets.Add(socket);
                    }
                    catch { }
                }

                for (int round = 0; round < 2; round++)
                {
                    foreach (string target in searchTargets)
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(
                            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: " + target + "\r\n\r\n");
                        foreach (Socket socket in sockets)
                        {
                            try { socket.SendTo(bytes, multicast); } catch { }
                        }
                        foreach (IPEndPoint to in direct)
                        {
                            try { main.SendTo(bytes, to); } catch { }
                        }
                        Thread.Sleep(20); // Small routers can drop a burst.
                    }
                    DateTime until = DateTime.UtcNow.AddMilliseconds(1300);
                    byte[] buffer = new byte[8192];
                    while (DateTime.UtcNow < until)
                    {
                        List<Socket> ready = new List<Socket>(sockets);
                        Socket.Select(ready, null, null, 200000);
                        foreach (Socket socket in ready)
                        {
                            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                            int count;
                            try { count = socket.ReceiveFrom(buffer, ref remote); }
                            catch (SocketException) { continue; } // Stray errors: keep listening.
                            string location = HeaderValue(Encoding.UTF8.GetString(buffer, 0, count), "LOCATION");
                            Uri parsed;
                            if (!String.IsNullOrWhiteSpace(location) && Uri.TryCreate(location.Trim(), UriKind.Absolute, out parsed) && !found.Contains(parsed))
                                found.Add(parsed);
                        }
                    }
                    if (found.Count > 0)
                        break;
                }
            }
            catch
            {
            }
            finally
            {
                foreach (Socket socket in sockets)
                {
                    try { socket.Close(); } catch { }
                }
            }
            return found;
        }

        private static Socket OpenSearchSocket(IPAddress local)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0));
            try { socket.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch { } // Ignore ICMP errors.
            return socket;
        }

        private static List<IPAddress> LocalAddresses()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                        nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !list.Contains(a.Address))
                            list.Add(a.Address);
                }
            }
            catch { }
            return list;
        }

        private static List<IPAddress> Gateways()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation g in nic.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any) && !list.Contains(g.Address))
                            list.Add(g.Address);
                }
            }
            catch { }
            return list;
        }

        /// <summary>NAT-PMP (RFC 6886): ask the gateway on UDP 5351 to forward a UDP port.</summary>
        private static UpnpPortMapping TryNatPmp(int port)
        {
            foreach (IPAddress gateway in Gateways())
            {
                try
                {
                    int external = NatPmpRequest(gateway, port, port, NatPmpLeaseSeconds);
                    if (external <= 0)
                        continue;
                    UpnpPortMapping mapping = new UpnpPortMapping(null, null, external, NatPmpExternalIp(gateway));
                    mapping._natPmpGateway = gateway;
                    mapping._natPmpInternalPort = port;
                    mapping.StartRenewing(NatPmpLeaseSeconds / 2); // These mappings expire.
                    return mapping;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Returns the mapped external port, or 0 if the gateway said no or didn't answer.</summary>
        private static int NatPmpRequest(IPAddress gateway, int internalPort, int externalPort, int lifetimeSeconds)
        {
            byte[] request = new byte[12];
            request[1] = 1; // Map UDP.
            request[4] = (byte)(internalPort >> 8); request[5] = (byte)internalPort;
            request[6] = (byte)(externalPort >> 8); request[7] = (byte)externalPort;
            request[8] = (byte)(lifetimeSeconds >> 24); request[9] = (byte)(lifetimeSeconds >> 16); request[10] = (byte)(lifetimeSeconds >> 8); request[11] = (byte)lifetimeSeconds;
            byte[] reply = NatPmpExchange(gateway, request, 16);
            if (reply == null || reply[1] != 129 || reply[2] != 0 || reply[3] != 0)
                return 0;
            return (reply[10] << 8) | reply[11];
        }

        private static string NatPmpExternalIp(IPAddress gateway)
        {
            byte[] reply = NatPmpExchange(gateway, new byte[2], 12);
            if (reply == null || reply[1] != 128 || reply[2] != 0 || reply[3] != 0)
                return String.Empty;
            return reply[8] + "." + reply[9] + "." + reply[10] + "." + reply[11];
        }

        private static byte[] NatPmpExchange(IPAddress gateway, byte[] request, int replyLength)
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                EndPoint target = new IPEndPoint(gateway, 5351);
                int wait = 250;
                for (int attempt = 0; attempt < 4; attempt++, wait *= 2)
                {
                    socket.SendTo(request, target);
                    socket.ReceiveTimeout = wait;
                    byte[] buffer = new byte[64];
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        int n = socket.ReceiveFrom(buffer, ref from);
                        if (n >= replyLength && ((IPEndPoint)from).Address.Equals(gateway))
                            return buffer;
                    }
                    catch (SocketException) { }
                }
            }
            return null;
        }

        /// <summary>The router's WAN services, connected ones first: many list an unused PPP service ahead of the live one.</summary>
        private static List<KeyValuePair<string, Uri>> FindWanServices(Uri location)
        {
            List<KeyValuePair<string, Uri>> connected = new List<KeyValuePair<string, Uri>>();
            List<KeyValuePair<string, Uri>> others = new List<KeyValuePair<string, Uri>>();

            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(location);
                request.Proxy = null; // The router is local; a system proxy can't reach it.
                request.UserAgent = "Maela/3";
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                string xml;
                using (WebResponse webResponse = request.GetResponse())
                using (StreamReader reader = new StreamReader(webResponse.GetResponseStream()))
                    xml = reader.ReadToEnd();

                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.LoadXml(xml);
                XmlNodeList services = doc.GetElementsByTagName("service");
                int i;
                for (i = 0; i < services.Count; i++)
                {
                    string type = ChildText(services[i], "serviceType");
                    if (String.IsNullOrWhiteSpace(type))
                        continue;
                    if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string path = ChildText(services[i], "controlURL");
                    if (String.IsNullOrWhiteSpace(path))
                        continue;

                    KeyValuePair<string, Uri> service = new KeyValuePair<string, Uri>(type.Trim(), new Uri(location, path.Trim()));
                    string status;
                    if (Soap(service.Value, service.Key, "GetStatusInfo", String.Empty, out status) &&
                        String.Equals(ExtractSoapValue(status, "NewConnectionStatus"), "Connected", StringComparison.OrdinalIgnoreCase))
                        connected.Add(service);
                    else
                        others.Add(service);
                }
            }
            catch
            {
            }

            connected.AddRange(others);
            return connected;
        }

        private static string ChildText(XmlNode parent, string localName)
        {
            XmlNode child;
            for (child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (String.Equals(child.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return child.InnerText;
            }
            return String.Empty;
        }

        private static bool Soap(Uri controlUrl, string serviceType, string action, string innerBody, out string response)
        {
            response = String.Empty;
            HttpWebRequest request = null;
            try
            {
                string envelope =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body><u:" + action + " xmlns:u=\"" + EscapeXml(serviceType) + "\">" + innerBody +
                    "</u:" + action + "></s:Body></s:Envelope>";
                byte[] payload = Encoding.UTF8.GetBytes(envelope);

                request = (HttpWebRequest)WebRequest.Create(controlUrl);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=\"utf-8\"";
                request.Headers["SOAPAction"] = "\"" + serviceType + "#" + action + "\"";
                request.ContentLength = payload.Length;
                request.Proxy = null;
                request.KeepAlive = false;
                request.ServicePoint.Expect100Continue = false; // Some routers hang waiting on this.
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;

                using (Stream stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                using (WebResponse webResponse = request.GetResponse())
                using (StreamReader reader = new StreamReader(webResponse.GetResponseStream()))
                    response = reader.ReadToEnd();

                return true;
            }
            catch (Exception ex)
            {
                // A refusal arrives as an HTTP error whose body says why; no body means no answer.
                WebException web = ex as WebException;
                if (web != null && web.Response != null)
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(web.Response.GetResponseStream()))
                            response = reader.ReadToEnd();
                        if (response.Length == 0)
                            response = "refused";
                    }
                    catch
                    {
                        response = "refused";
                    }
                }
                if (request != null)
                {
                    try { request.Abort(); } catch { }
                }
                return false;
            }
        }

        private static string ExtractSoapValue(string xml, string name)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                XmlNodeList all = doc.GetElementsByTagName("*");
                int i;
                for (i = 0; i < all.Count; i++)
                {
                    if (String.Equals(all[i].LocalName, name, StringComparison.OrdinalIgnoreCase))
                        return all[i].InnerText.Trim();
                }
            }
            catch
            {
            }
            return String.Empty;
        }

        private static string HeaderValue(string headers, string name)
        {
            string[] lines = headers.Replace("\r\n", "\n").Split('\n');
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                string key = lines[i].Substring(0, colon).Trim();
                if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(colon + 1).Trim();
            }
            return String.Empty;
        }

        private static string EscapeXml(string value)
        {
            if (value == null)
                return String.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }

    internal sealed class VoiceChannelInfo
    {
        public int Id;
        public string Name;
        public int MemberCount;
        public bool IsText;         // Chat-only channel: no voice.

        public override string ToString()
        {
            return Name == null ? String.Empty : Name;
        }
    }

    internal sealed class RoomMemberInfo
    {
        public uint Id;
        public string Name;
        public int ChannelId;
        public bool IsLocal;
        public bool IsHost;
        public bool IsSpeaking;
        public bool Muted;
        public bool Deafened;

        public override string ToString()
        {
            return Name == null ? String.Empty : Name;
        }
    }

    internal sealed partial class VoipEngine : IDisposable
    {
        public const int MaxRoomMembers = 16;

        private const int SampleRate = 48000;
        private const int BitsPerSample = 16;
        private const int Channels = 1;
        private const int FrameMilliseconds = 10;
        private const int AudioBytesPerFrame = SampleRate * (BitsPerSample / 8) * Channels * FrameMilliseconds / 1000;
        private const int CaptureBufferCount = 8;
        private const int HeaderBytes = 16;
        private const uint Magic = 0x32564F52; // "ROV2" in little-endian memory
        private const byte ProtocolVersion = WireVersion;

        private const byte PacketJoin = 1;
        private const byte PacketWelcome = 2;
        private const byte PacketPeerList = 3;
        private const byte PacketKeepAlive = 4;
        private const byte PacketLeave = 5;
        private const byte PacketReject = 6;
        private const byte PacketKick = 7;
        // 8 and 9 were the unencrypted probe packets; probes now live in the outer layer.
        private const byte PacketAudio = 10;
        private const byte PacketChannelSwitch = 11;

        private readonly object _stateLock = new object();
        private readonly object _sendLock = new object();
        private readonly Dictionary<string, Participant> _hostParticipants = new Dictionary<string, Participant>();
        private readonly Dictionary<uint, string> _memberNames = new Dictionary<uint, string>();
        private readonly Dictionary<uint, int> _memberChannels = new Dictionary<uint, int>();
        private readonly Dictionary<int, string> _channels = new Dictionary<int, string>();
        private readonly Dictionary<uint, PlaybackStream> _playbackStreams = new Dictionary<uint, PlaybackStream>();
        private readonly Dictionary<uint, int> _peerVolumes = new Dictionary<uint, int>();
        private readonly HashSet<uint> _peerMutes = new HashSet<uint>();
        private readonly Dictionary<uint, DateTime> _lastSpeakingUtc = new Dictionary<uint, DateTime>();
        private readonly HashSet<string> _bannedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<NativeBuffer> _captureBuffers = new List<NativeBuffer>();
        private readonly uint _inputDeviceId;
        private readonly uint _outputDeviceId;
        private readonly FarEndReference _farEnd = new FarEndReference();
        private readonly Dictionary<uint, VoiceDecoder> _voiceDecoders = new Dictionary<uint, VoiceDecoder>();
        private readonly List<EncodedVoice> _encodedScratch = new List<EncodedVoice>();
        private readonly List<byte[]> _decodedScratch = new List<byte[]>();
        private VoiceEncoder _voiceEncoder = new VoiceEncoder();
        private long _txBytes;
        private readonly EchoCanceller _echoCanceller;
        private volatile bool _echoCancellation = true;
        private volatile bool _echoResetPending;

        private Socket _socket;
        private Thread _receiveThread;
        private Thread _maintenanceThread;
        private volatile bool _running;
        private volatile bool _muted;
        private volatile bool _isHost;
        private volatile bool _connected;
        private volatile bool _reconnectAllowed;
        private IPEndPoint _hostEndpoint;
        private uint _localId;
        private uint _hostId;
        private string _localName;
        private int _localChannelId;
        private int _requestedChannelId = -1;
        private int _channelRequestAttempts;
        private DateTime _channelRequestSentUtc;
        private DateTime _lastPeerListBroadcastUtc;
        private volatile int _peerListRepeats;
        private int _nextChannelId = 2;

        public const byte ChannelKindVoice = 0;
        public const byte ChannelKindText = 1;
        /// <summary>Marks a chat-only channel in a saved room's channel list.</summary>
        public const string TextChannelMarker = "\u0001";
        private readonly Dictionary<int, byte> _channelKinds = new Dictionary<int, byte>();
        private int _viewChannelId;

        /// <summary>The channel whose chat is open (highlighted). You can read and post there.</summary>
        public int ViewChannelId { get { return _viewChannelId; } }

        public bool IsTextChannel(int channelId)
        {
            lock (_stateLock)
                return IsTextChannelLocked(channelId);
        }

        private bool IsTextChannelLocked(int channelId)
        {
            byte kind;
            return _channelKinds.TryGetValue(channelId, out kind) && kind == ChannelKindText;
        }

        /// <summary>
        /// Opens a channel's chat (one click in the sidebar). Doesn't join voice. The host is told
        /// so it sends that channel's history and new messages.
        /// </summary>
        public void ViewChannel(int channelId)
        {
            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
            }
            _viewChannelId = channelId;
            _chatSyncedChannel = -1; // Sync (and fetch history) straight away.
        }
        private int _masterVolumePercent = 100;
        private int _micVolumePercent = 100;
        private long _txPackets;
        private long _rxPackets;
        private DateTime _lastHostSeenUtc;
        private DateTime _lastJoinSentUtc;
        private DateTime _lastKeepAliveUtc;
        private DateTime _joinStartedUtc;
        private string _lastMessage;
        private int _boundPort;

        private IntPtr _waveIn = IntPtr.Zero;
        private WaveInProc _waveInProc;

        /// <summary>Tests only: run without opening audio devices.</summary>
        internal bool HeadlessForTests { get; set; }

        /// <summary>Tests only: fraction (0-1) of outgoing packets to drop, to exercise retries.</summary>
        internal double PacketLossForTests { get; set; }

        /// <summary>Tests only: client binds this local port (0 = any) to mimic a reused NAT mapping.</summary>
        internal int LocalPortForTests { get; set; }
        private readonly Random _testLossRandom = new Random(1234);

        public bool IsRunning { get { return _running; } }
        public bool IsHost { get { return _isHost; } }
        public bool IsConnected { get { return _isHost || _connected; } }
        public bool IsInVoice { get { return IsConnected && _localChannelId > 0; } }
        public int LocalChannelId { get { return _localChannelId; } }
        public bool Muted
        {
            get { return _muted; }
            set
            {
                if (_muted == value)
                    return;
                _muted = value;
                LocalStateChanged();
            }
        }

        /// <summary>Removes speaker audio that leaks back into the microphone.</summary>
        public bool EchoCancellation
        {
            get { return _echoCancellation; }
            set
            {
                if (value && !_echoCancellation)
                    _echoResetPending = true;
                _echoCancellation = value;
            }
        }

        /// <summary>Estimated speaker-to-microphone delay in milliseconds, or -1 while unknown.</summary>
        public int EchoDelayMs { get { return _echoCancellation ? _echoCanceller.EstimatedDelayMs : -1; } }
        public long TxPackets { get { return Interlocked.Read(ref _txPackets); } }
        public long TxBytes { get { return Interlocked.Read(ref _txBytes); } }
        public long RxPackets { get { return Interlocked.Read(ref _rxPackets); } }
        public string LastMessage { get { lock (_stateLock) { return _lastMessage; } } }
        public int BoundPort { get { return _boundPort; } }
        public uint LocalMemberId { get { return _localId; } }
        public int BanCount { get { lock (_stateLock) { return _bannedIps.Count; } } }

        public int MasterVolumePercent
        {
            get { return _masterVolumePercent; }
            set
            {
                int clamped = Clamp(value, 0, 150);
                _masterVolumePercent = clamped;
                UpdateAllPlaybackGains();
            }
        }

        public int MicVolumePercent
        {
            get { return _micVolumePercent; }
            set { _micVolumePercent = Clamp(value, 0, 150); }
        }

        /// <summary>People in a voice channel (those who left voice aren't counted).</summary>
        public int VoiceMemberCount
        {
            get
            {
                lock (_stateLock)
                {
                    int count = 0;
                    foreach (KeyValuePair<uint, int> pair in _memberChannels)
                    {
                        if (pair.Value > 0 && _memberNames.ContainsKey(pair.Key))
                            count++;
                    }
                    return count;
                }
            }
        }

        public int MemberCount
        {
            get
            {
                lock (_stateLock)
                    return _memberNames.Count;
            }
        }

        public int CurrentChannelMemberCount
        {
            get
            {
                lock (_stateLock)
                {
                    if (_localChannelId <= 0)
                        return 0;
                    int count = 0;
                    foreach (KeyValuePair<uint, int> pair in _memberChannels)
                    {
                        if (pair.Value == _localChannelId)
                            count++;
                    }
                    return count;
                }
            }
        }

        public VoipEngine() : this(NativeMethods.WAVE_MAPPER, NativeMethods.WAVE_MAPPER)
        {
        }

        public VoipEngine(uint inputDeviceId, uint outputDeviceId)
        {
            _inputDeviceId = inputDeviceId;
            _outputDeviceId = outputDeviceId;
            _echoCanceller = new EchoCanceller(_farEnd);
        }

        public string GetChannelName(int channelId)
        {
            lock (_stateLock)
            {
                string value;
                return _channels.TryGetValue(channelId, out value) ? value : String.Empty;
            }
        }

        public VoiceChannelInfo[] GetChannels()
        {
            lock (_stateLock)
            {
                List<VoiceChannelInfo> result = new List<VoiceChannelInfo>();
                foreach (KeyValuePair<int, string> pair in _channels)
                {
                    VoiceChannelInfo info = new VoiceChannelInfo();
                    info.Id = pair.Key;
                    info.Name = pair.Value;
                    info.IsText = IsTextChannelLocked(pair.Key);
                    int count = 0;
                    foreach (KeyValuePair<uint, int> member in _memberChannels)
                    {
                        if (member.Value == pair.Key)
                            count++;
                    }
                    info.MemberCount = count;
                    result.Add(info);
                }
                result.Sort(delegate(VoiceChannelInfo a, VoiceChannelInfo b) { return a.Id.CompareTo(b.Id); });
                return result.ToArray();
            }
        }

        public RoomMemberInfo[] GetMembers()
        {
            return GetMembers(true);
        }

        /// <summary>Every member in the room, whatever channel they are in.</summary>
        public RoomMemberInfo[] GetAllMembers()
        {
            return GetMembers(false);
        }

        private RoomMemberInfo[] GetMembers(bool currentChannelOnly)
        {
            lock (_stateLock)
            {
                List<RoomMemberInfo> result = new List<RoomMemberInfo>();
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<uint, string> pair in _memberNames)
                {
                    int channelId;
                    if (!_memberChannels.TryGetValue(pair.Key, out channelId))
                        channelId = 0;

                    if (currentChannelOnly && _localChannelId > 0 && channelId != _localChannelId)
                        continue;

                    RoomMemberInfo info = new RoomMemberInfo();
                    info.Id = pair.Key;
                    info.Name = pair.Value;
                    info.ChannelId = channelId;
                    info.IsLocal = pair.Key == _localId;
                    info.IsHost = pair.Key == _hostId;
                    DateTime last;
                    info.IsSpeaking = _lastSpeakingUtc.TryGetValue(pair.Key, out last) && (now - last).TotalMilliseconds < 420.0;
                    byte flags = FlagsOf(pair.Key);
                    info.Muted = (flags & FlagMuted) != 0;
                    info.Deafened = (flags & FlagDeafened) != 0;
                    if (info.Muted || info.Deafened)
                        info.IsSpeaking = false;
                    result.Add(info);
                }

                result.Sort(delegate(RoomMemberInfo a, RoomMemberInfo b)
                {
                    if (a.IsLocal && !b.IsLocal)
                        return -1;
                    if (!a.IsLocal && b.IsLocal)
                        return 1;
                    if (a.IsHost && !b.IsHost)
                        return -1;
                    if (!a.IsHost && b.IsHost)
                        return 1;
                    return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                return result.ToArray();
            }
        }

        public void ChangeChannel(int channelId)
        {
            if (!_running || !IsConnected)
                return;

            lock (_stateLock)
            {
                if (channelId != 0 && (!_channels.ContainsKey(channelId) || IsTextChannelLocked(channelId)))
                    return; // Chat-only channels have no voice.
            }
            if (channelId > 0)
                ViewChannel(channelId); // Joining a channel also opens its chat.

            if (_isHost)
            {
                lock (_stateLock)
                {
                    _localChannelId = channelId;
                    _memberChannels[_localId] = channelId;
                }
                DisposePlaybackStreams();
                BroadcastPeerList();
                SetLastMessage(channelId == 0 ? "Left voice. Room server is still running." : "Joined #" + GetChannelName(channelId) + ".");
            }
            else if (_connected && _hostEndpoint != null)
            {
                RequestChannel(channelId);
            }
        }

        public void LeaveVoice()
        {
            ChangeChannel(0);
        }

        public int CreateChannel(string name)
        {
            return CreateChannel(name, false);
        }

        public int CreateChannel(string name, bool textOnly)
        {
            if (!_isHost)
                return 0;

            string clean = CleanChannelName(name);
            if (clean.Length == 0)
                return 0;

            int id;
            lock (_stateLock)
            {
                if (_channels.Count >= 16)
                    return 0;
                id = _nextChannelId++;
                while (_channels.ContainsKey(id))
                    id = _nextChannelId++;
                _channels[id] = clean;
                if (textOnly)
                    _channelKinds[id] = ChannelKindText;
            }

            BroadcastPeerList();
            SetLastMessage("Created #" + clean + ".");
            return id;
        }

        public void RenameChannel(int channelId, string name)
        {
            if (!_isHost)
                return;

            string clean = CleanChannelName(name);
            if (clean.Length == 0)
                return;

            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
                _channels[channelId] = clean;
            }

            BroadcastPeerList();
        }

        public void DeleteChannel(int channelId)
        {
            if (!_isHost || channelId == 1)
                return;

            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
                _channels.Remove(channelId);
                _channelKinds.Remove(channelId);
                if (_viewChannelId == channelId)
                    _viewChannelId = 1;

                List<uint> ids = new List<uint>(_memberChannels.Keys);
                int i;
                for (i = 0; i < ids.Count; i++)
                {
                    if (_memberChannels[ids[i]] == channelId)
                        _memberChannels[ids[i]] = 1;
                }

                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.ChannelId == channelId)
                        pair.Value.ChannelId = 1;
                    if (pair.Value.ViewChannelId == channelId)
                        pair.Value.ViewChannelId = 1;
                }

                if (_localChannelId == channelId)
                    _localChannelId = 1;
            }

            DisposePlaybackStreams();
            BroadcastPeerList();
        }

        public void MoveMemberToChannel(uint memberId, int channelId)
        {
            if (!_isHost)
                return;

            lock (_stateLock)
            {
                if (channelId != 0 && !_channels.ContainsKey(channelId))
                    return;

                if (memberId == _localId)
                {
                    _localChannelId = channelId;
                    _memberChannels[_localId] = channelId;
                }
                else
                {
                    Participant target = null;
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        if (pair.Value.Id == memberId)
                        {
                            target = pair.Value;
                            break;
                        }
                    }
                    if (target == null)
                        return;
                    target.ChannelId = channelId;
                    _memberChannels[memberId] = channelId;
                }
            }

            DisposePlaybackStreams();
            BroadcastPeerList();
        }

        private static string CleanChannelName(string value)
        {
            string clean = value == null ? String.Empty : value.Trim();
            StringBuilder printable = new StringBuilder(clean.Length);
            foreach (char ch in clean)
                printable.Append(Char.IsControl(ch) ? ' ' : ch);
            clean = printable.ToString().Trim().Replace("|", " ");
            while (clean.IndexOf("  ", StringComparison.Ordinal) >= 0)
                clean = clean.Replace("  ", " ");
            if (clean.Length > 32)
                clean = clean.Substring(0, 32).Trim();
            return clean;
        }

        public int GetPeerVolumePercent(uint id)
        {
            lock (_stateLock)
            {
                int value;
                return _peerVolumes.TryGetValue(id, out value) ? value : 100;
            }
        }

        public void SetPeerVolumePercent(uint id, int percent)
        {
            lock (_stateLock)
                _peerVolumes[id] = Clamp(percent, 0, 200);
            UpdatePlaybackGain(id);
        }

        public bool GetPeerMuted(uint id)
        {
            lock (_stateLock)
                return _peerMutes.Contains(id);
        }

        public void SetPeerMuted(uint id, bool muted)
        {
            lock (_stateLock)
            {
                if (muted)
                    _peerMutes.Add(id);
                else
                    _peerMutes.Remove(id);
            }
            UpdatePlaybackGain(id);
        }

        public void ClearBans()
        {
            lock (_stateLock)
                _bannedIps.Clear();
            SetLastMessage("Host cleared the session ban list.");
        }

        public void KickMember(uint id, bool ban)
        {
            if (!_isHost || id == 0 || id == _localId)
                return;

            Participant target = null;
            string key = null;
            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.Id == id)
                    {
                        key = pair.Key;
                        target = pair.Value;
                        break;
                    }
                }

                if (target != null && ban)
                    _bannedIps.Add(target.Endpoint.Address.ToString());
            }

            if (target == null || key == null)
                return;

            string reason = ban ? "Banned by host." : "Kicked by host.";
            SendPacket(target.Endpoint, PacketKick, _localId, 0, Encoding.UTF8.GetBytes(reason));
            RemoveHostParticipant(key, true);
            SetLastMessage(target.Name + (ban ? " was banned." : " was kicked."));
        }

        public void StartHost(int port, string name)
        {
            StartHost(port, name, "Voice Room", null);
        }

        public void StartHost(int port, string name, string roomName)
        {
            StartHost(port, name, roomName, null);
        }

        public void StartHost(int port, string name, string roomName, IList<string> channelNames)
        {
            StartHost(port, name, roomName, channelNames, null, null);
        }

        /// <param name="hostPrivateKey">The room's long-term X25519 key (keeps its room code stable), or null for a one-off key.</param>
        /// <param name="password">Optional room password.</param>
        public void StartHost(int port, string name, string roomName, IList<string> channelNames, byte[] hostPrivateKey, string password)
        {
            _roomName = CleanRoomName(roomName);
            if (_running)
                throw new InvalidOperationException("Voice is already running.");

            ResetState();
            ConfigureHostSecurity(hostPrivateKey, password);
            _isHost = true;
            _connected = true;
            _localName = name;
            _localId = CreateParticipantId();
            _hostId = _localId;
            _reconnectAllowed = false;
            _lastMessage = String.Empty;

            lock (_stateLock)
            {
                int nextId = 1;
                if (channelNames != null)
                {
                    int i;
                    for (i = 0; i < channelNames.Count && nextId <= 16; i++)
                    {
                        string raw = channelNames[i] ?? String.Empty;
                        bool text = raw.StartsWith(TextChannelMarker, StringComparison.Ordinal) && nextId > 1; // #1 stays voice.
                        string clean = CleanChannelName(text ? raw.Substring(TextChannelMarker.Length) : raw);
                        if (clean.Length == 0)
                            continue;
                        if (text)
                            _channelKinds[nextId] = ChannelKindText;
                        _channels[nextId++] = clean;
                    }
                }
                if (_channels.Count == 0)
                {
                    _channels[1] = "General";
                    nextId = 2;
                }
                _nextChannelId = nextId;
                _localChannelId = 1;
                _viewChannelId = 1;
                _memberNames[_localId] = _localName;
                _memberChannels[_localId] = _localChannelId;
            }

            try
            {
                CreateSocket();
                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
                IPEndPoint boundEndpoint = _socket.LocalEndPoint as IPEndPoint;
                _boundPort = boundEndpoint == null ? port : boundEndpoint.Port;
                if (!HeadlessForTests)
                    OpenWaveIn();
                StartThreadsAndCapture();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <param name="hostFingerprint">From the room code; the host's key must match it.</param>
        public void StartClient(IPEndPoint hostEndpoint, string name, byte[] hostFingerprint, string password)
        {
            if (_running)
                throw new InvalidOperationException("Voice is already running.");

            ResetState();
            ConfigureClientSecurity(hostFingerprint, password);
            _isHost = false;
            _connected = false;
            _hostEndpoint = hostEndpoint;
            _localName = name;
            _localId = 0;
            _hostId = 0;
            _localChannelId = 0;
            _reconnectAllowed = true;
            _lastMessage = "Joining room...";
            _joinStartedUtc = DateTime.UtcNow;

            try
            {
                CreateSocket();
                _socket.Bind(new IPEndPoint(IPAddress.Any, LocalPortForTests));
                IPEndPoint clientEndpoint = _socket.LocalEndPoint as IPEndPoint;
                _boundPort = clientEndpoint == null ? 0 : clientEndpoint.Port;
                if (!HeadlessForTests)
                    OpenWaveIn();
                StartThreadsAndCapture();
                SendJoin();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ResetState()
        {
            ResetStreams();
            _viewChannelId = 0;
            _txBytes = 0;
            _voiceEncoder = new VoiceEncoder();
            _txPackets = 0;
            _rxPackets = 0;
            _lastHostSeenUtc = DateTime.MinValue;
            _lastJoinSentUtc = DateTime.MinValue;
            _lastKeepAliveUtc = DateTime.MinValue;
            _joinStartedUtc = DateTime.MinValue;
            _boundPort = 0;
            _hostId = 0;
            _localChannelId = 0;
            _requestedChannelId = -1;
            _nextChannelId = 2;
            _reconnectAllowed = true;

            lock (_stateLock)
            {
                _hostParticipants.Clear();
                _memberNames.Clear();
                _memberChannels.Clear();
                _channels.Clear();
                _channelKinds.Clear();
                _profiles.Clear();
                _profileBlobs.Clear();
                _profileUploads.Clear();
                _profileDownloads.Clear();
                _advertisedProfiles.Clear();
                _lastProfileChangeUtc.Clear();
                _peerVolumes.Clear();
                _peerMutes.Clear();
                _lastSpeakingUtc.Clear();
                _bannedIps.Clear();
                _lastMessage = String.Empty;
            }

            ResetChat();
            ResetSecurity();
            ResetPresence();
            DisposePlaybackStreams();
        }

        private void CreateSocket()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.ReceiveTimeout = 250;
            _socket.ReceiveBufferSize = 1024 * 1024;
            _socket.SendBufferSize = 1024 * 1024;
            DisableConnectionResetErrors(_socket);
        }

        /// <summary>
        /// On Windows, sending to a port nobody listens on any more (someone whose mæla crashed)
        /// makes later receives fail with "connection reset", flooding the receive loop with
        /// errors right when that person is trying to rejoin. UDP has no connections, so turn
        /// that reporting off (SIO_UDP_CONNRESET).
        /// </summary>
        private static void DisableConnectionResetErrors(Socket socket)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;
            try
            {
                const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
        }

        private void StartThreadsAndCapture()
        {
            _running = true;
            StartScheduler();

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "Maela UDP receive";
            _receiveThread.Start();

            _maintenanceThread = new Thread(MaintenanceLoop);
            _maintenanceThread.IsBackground = true;
            _maintenanceThread.Name = "Maela room maintenance";
            _maintenanceThread.Start();

            if (_waveIn == IntPtr.Zero)
                return; // Headless test mode.
            StartCaptureThread();
            int result = NativeMethods.waveInStart(_waveIn);
            CheckMmResult(result, "Could not start the microphone");
        }

        private void ReceiveLoop()
        {
            byte[] receiveBuffer = new byte[2048];

            while (_running)
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int length;

                try
                {
                    length = _socket.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref sender);
                }
                catch (SocketException ex)
                {
                    if (!_running)
                        break;
                    if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock)
                        continue;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (length < OuterHeaderBytes)
                    continue;

                IPEndPoint senderEndpoint = sender as IPEndPoint;
                if (senderEndpoint == null)
                    continue;

                // Screen-share media straight from another member (sealed with that pair's keys).
                if (length >= 6 && receiveBuffer[5] == OuterMedia && BitConverter.ToUInt32(receiveBuffer, 0) == Magic && receiveBuffer[4] == WireVersion)
                {
                    try { TryHandleDirectMedia(senderEndpoint, receiveBuffer, length); } catch { }
                    continue;
                }

                Packet packet;
                SecureSession viaPending;
                try
                {
                    packet = ReceiveSecure(senderEndpoint, receiveBuffer, length, out viaPending);
                }
                catch
                {
                    continue; // Malformed datagram.
                }
                if (packet == null)
                    continue;

                if (_isHost)
                    HandleHostPacket(senderEndpoint, packet, viaPending);
                else
                    HandleClientPacket(senderEndpoint, packet);
            }
        }

        private void HandleHostPacket(IPEndPoint sender, Packet packet, SecureSession viaPending)
        {
            string key = EndpointKey(sender);

            if (packet.Type == PacketJoin)
            {
                lock (_stateLock)
                {
                    if (_bannedIps.Contains(sender.Address.ToString()))
                    {
                        SendPacketVia(viaPending, sender, PacketKick, _localId, 0, Encoding.UTF8.GetBytes("Banned by host."));
                        return;
                    }
                }

                string requestedName;
                string password;
                if (!TryParseJoin(packet.Payload, out requestedName, out password))
                    return;
                if (!PasswordMatches(password, viaPending ?? SessionFor(sender, null)))
                {
                    RecordPasswordFailure(sender.Address.ToString());
                    SendPacketVia(viaPending, sender, PacketReject, 0, 0, Encoding.UTF8.GetBytes(String.IsNullOrEmpty(password) ? "This room needs a password." : "Wrong room password."));
                    return;
                }
                if (requestedName.Length == 0)
                    requestedName = "Guest";

                Participant participant = null;
                bool added = false;
                string ghostKey = null;
                int rejoinChannel = 1;

                lock (_stateLock)
                {
                    if (_hostParticipants.TryGetValue(key, out participant))
                    {
                        participant.LastSeenUtc = DateTime.UtcNow;
                        participant.Name = requestedName;
                        _memberNames[participant.Id] = participant.Name;
                        if (!_memberChannels.ContainsKey(participant.Id))
                        {
                            participant.ChannelId = 1;
                            _memberChannels[participant.Id] = 1;
                        }
                    }
                    else
                    {
                        // Someone who vanished without saying goodbye (crash, Task Manager, lost
                        // connection) and comes straight back: drop the stale copy right away
                        // instead of showing them twice until it times out.
                        ghostKey = FindGhost(sender.Address, requestedName, key);
                        if (ghostKey != null)
                        {
                            Participant ghost = _hostParticipants[ghostKey];
                            if (ghost.ChannelId > 0 && _channels.ContainsKey(ghost.ChannelId))
                                rejoinChannel = ghost.ChannelId;
                        }
                    }
                }

                if (ghostKey != null)
                    RemoveHostParticipant(ghostKey, false);

                lock (_stateLock)
                {
                    if (!_hostParticipants.TryGetValue(key, out participant))
                    {
                        if (_memberNames.Count >= MaxRoomMembers)
                        {
                            SendPacketVia(viaPending, sender, PacketReject, 0, 0, Encoding.UTF8.GetBytes("Room is full (max " + MaxRoomMembers + ")."));
                            return;
                        }

                        participant = new Participant();
                        participant.Id = CreateUniqueParticipantId();
                        participant.Name = requestedName;
                        participant.Endpoint = new IPEndPoint(sender.Address, sender.Port);
                        participant.LastSeenUtc = DateTime.UtcNow;
                        participant.ChannelId = rejoinChannel;
                        _hostParticipants[key] = participant;
                        _memberNames[participant.Id] = participant.Name;
                        _memberChannels[participant.Id] = participant.ChannelId;
                        added = true;
                    }
                }

                AdoptSession(key, participant, viaPending);
                // Tell the client which channel to (re)enter: the one it was in if it is coming back.
                int welcomeChannel;
                lock (_stateLock)
                    welcomeChannel = participant.ChannelId > 0 && _channels.ContainsKey(participant.ChannelId) ? participant.ChannelId : 1;
                SendPacket(participant.Endpoint, PacketWelcome, participant.Id, 0, BitConverter.GetBytes(welcomeChannel));
                BroadcastPeerList();

                if (added)
                    SetLastMessage(participant.Name + (ghostKey == null ? " joined." : " reconnected."));
                return;
            }

            Participant known;
            lock (_stateLock)
            {
                if (!_hostParticipants.TryGetValue(key, out known))
                    return;
                if (packet.SenderId != known.Id)
                    return;
                known.LastSeenUtc = DateTime.UtcNow;
            }

            if (packet.Type == PacketKeepAlive)
            {
                HostHandleKeepAlive(known, packet);
                return;
            }

            if (HostHandlePing(known, packet))
                return;

            if (HandleHostChatPacket(known, packet))
                return;

            if (HandleHostRoomArtPacket(known, packet))
                return;
            if (HandleHostProfilePacket(known, packet))
                return;

            if (HandleHostStreamPacket(known, packet))
                return;

            if (packet.Type == PacketLeave)
            {
                RemoveHostParticipant(key, true);
                return;
            }

            if (packet.Type == PacketChannelSwitch)
            {
                if (packet.Payload == null || packet.Payload.Length != 4)
                    return;
                int requestedChannel = BitConverter.ToInt32(packet.Payload, 0);
                lock (_stateLock)
                {
                    if (requestedChannel != 0 && (!_channels.ContainsKey(requestedChannel) || IsTextChannelLocked(requestedChannel)))
                        return;
                    known.ChannelId = requestedChannel;
                    if (requestedChannel > 0)
                        known.ViewChannelId = requestedChannel;
                    _memberChannels[known.Id] = requestedChannel;
                }
                BroadcastPeerList();
                return;
            }

            if (packet.Type == PacketAudio)
            {
                if (packet.Payload == null || packet.Payload.Length == 0 || packet.Payload.Length > VoiceEncoder.MaxPacketBytes)
                    return;

                int senderChannel;
                lock (_stateLock)
                {
                    if (!_memberChannels.TryGetValue(packet.SenderId, out senderChannel) || senderChannel <= 0)
                        return;
                }

                Interlocked.Increment(ref _rxPackets);

                if (_localChannelId == senderChannel)
                    QueueRemoteAudio(packet.SenderId, packet.Sequence, packet.Payload);

                List<IPEndPoint> targets = new List<IPEndPoint>();
                lock (_stateLock)
                {
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        Participant participant = pair.Value;
                        if (participant.Id != packet.SenderId && participant.ChannelId == senderChannel)
                            targets.Add(new IPEndPoint(participant.Endpoint.Address, participant.Endpoint.Port));
                    }
                }

                byte[] raw = BuildPacket(PacketAudio, packet.SenderId, packet.Sequence, packet.Payload);
                int i;
                for (i = 0; i < targets.Count; i++)
                    SendRaw(targets[i], raw);
            }
        }

        private void HandleClientPacket(IPEndPoint sender, Packet packet)
        {
            if (_hostEndpoint == null || !sender.Address.Equals(_hostEndpoint.Address))
                return;

            _hostEndpoint = new IPEndPoint(sender.Address, sender.Port);
            _lastHostSeenUtc = DateTime.UtcNow;

            if (packet.Type == PacketLeave)
            {
                // The host closed the room. Stop at once; rejoin automatically if it comes back.
                if (_connected)
                    LoseConnection("The host closed the room - rejoining when it's back...");
                return;
            }

            if (packet.Type == PacketWelcome)
            {
                _localId = packet.SenderId;
                _connected = true;
                _joinStartedUtc = DateTime.MinValue;
                SetLastMessage(String.Empty);
                int channel = packet.Payload != null && packet.Payload.Length >= 4 ? BitConverter.ToInt32(packet.Payload, 0) : 1;
                if (channel <= 0)
                    channel = 1;
                _viewChannelId = channel;
                _chatSyncedChannel = -1;
                ProfileReconnected();
                lock (_stateLock)
                    ApplyLocalProfileLocked();
                RequestChannel(channel);
                return;
            }

            if (packet.Type == PacketReject)
            {
                _connected = false;
                _reconnectAllowed = false; // Wrong password or full room: retrying won't help.
                string reason = packet.Payload == null ? String.Empty : Encoding.UTF8.GetString(packet.Payload);
                SetLastMessage(reason.Length == 0 ? "The host refused the connection." : reason);
                return;
            }

            if (packet.Type == PacketKick)
            {
                _connected = false;
                _reconnectAllowed = false;
                _localId = 0;
                _localChannelId = 0;
                lock (_stateLock)
                {
                    _memberNames.Clear();
                    _memberChannels.Clear();
                    _channels.Clear();
                }
                DisposePlaybackStreams();
                string reason = DecodeName(packet.Payload);
                SetLastMessage(reason.Length == 0 ? "Removed by host." : reason);
                return;
            }

            if (packet.Type == PacketPeerList)
            {
                ParsePeerList(packet.Payload);
                return;
            }

            if (HandleClientChatPacket(packet))
                return;

            if (HandleClientRoomArtPacket(packet))
                return;
            if (HandleClientProfilePacket(packet))
                return;

            if (HandleClientStreamPacket(packet))
                return;

            if (ClientHandlePong(packet))
                return;

            if (packet.Type == PacketAudio)
            {
                if (!_connected || packet.SenderId == 0 || packet.SenderId == _localId || _localChannelId <= 0)
                    return;
                if (packet.Payload == null || packet.Payload.Length == 0 || packet.Payload.Length > VoiceEncoder.MaxPacketBytes)
                    return;

                int senderChannel;
                lock (_stateLock)
                {
                    if (!_memberChannels.TryGetValue(packet.SenderId, out senderChannel) || senderChannel != _localChannelId)
                        return;
                }

                Interlocked.Increment(ref _rxPackets);
                QueueRemoteAudio(packet.SenderId, packet.Sequence, packet.Payload);
            }
        }

        private void MaintenanceLoop()
        {
            while (_running)
            {
                try
                {
                    if (_isHost)
                        HostMaintenance();
                    else
                        ClientMaintenance();
                }
                catch
                {
                }

                Thread.Sleep(250);
            }
        }

        /// <summary>
        /// Host: an existing member at the same address (same home network) with the same name,
        /// joining again from a new port. That is the same person after their old copy of mæla
        /// went away without saying goodbye. Call with _stateLock held. Returns its key, or null.
        /// </summary>
        private string FindGhost(IPAddress address, string name, string exceptKey)
        {
            foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
            {
                Participant p = pair.Value;
                if (pair.Key == exceptKey || !p.Endpoint.Address.Equals(address))
                    continue;
                if (String.Equals(p.Name, name, StringComparison.Ordinal))
                    return pair.Key;
            }
            return null;
        }

        private void HostMaintenance()
        {
            DateTime now = DateTime.UtcNow;
            PruneSecurityState();
            HostStreamMaintenance(now);
            StreamLinkMaintenance(now);
            List<string> expired = new List<string>();

            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if ((now - pair.Value.LastSeenUtc).TotalSeconds > 10.0)
                        expired.Add(pair.Key);
                }
            }

            int i;
            for (i = 0; i < expired.Count; i++)
                RemoveHostParticipant(expired[i], false);

            // Peer lists travel over UDP, so a lost one would leave clients with a stale view.
            // After a change, repeat it twice more half a second apart; otherwise refresh every 3 s.
            if (expired.Count > 0)
                BroadcastPeerList();
            else if (_peerListRepeats > 0 && (now - _lastPeerListBroadcastUtc).TotalMilliseconds >= 500.0)
                SendPeerListNow(true);
            else if ((now - _lastPeerListBroadcastUtc).TotalSeconds >= 3.0)
                SendPeerListNow(false);
        }

        /// <summary>Client: drop the current connection and keep trying to rejoin with a fresh handshake.</summary>
        private void LoseConnection(string message)
        {
            ResetStreams();
            _connected = false;
            _localId = 0;
            _localChannelId = 0;
            lock (_stateLock)
            {
                _memberNames.Clear();
                _memberChannels.Clear();
                _channels.Clear();
            }
            DisposePlaybackStreams();
            DropClientSession(); // Reconnect with a fresh handshake.
            _joinStartedUtc = DateTime.UtcNow;
            SetLastMessage(message);
        }

        private void ClientMaintenance()
        {
            DateTime now = DateTime.UtcNow;

            if (_connected && _lastHostSeenUtc != DateTime.MinValue && (now - _lastHostSeenUtc).TotalSeconds > 8.0)
                LoseConnection("Connection lost - reconnecting...");

            if (!_connected)
            {
                if (!_reconnectAllowed)
                    return;

                if (_joinStartedUtc != DateTime.MinValue && (now - _joinStartedUtc).TotalSeconds >= 6.0)
                    SetLastMessage("No response from host - check that the host room is online and Windows Firewall allows m\u00E6la. Retrying...");

                if ((now - _lastJoinSentUtc).TotalMilliseconds >= 1000.0)
                    SendJoin();
            }
            else
            {
                if (_requestedChannelId >= 0)
                {
                    if (_requestedChannelId == _localChannelId || _channelRequestAttempts >= 10)
                        _requestedChannelId = -1; // Confirmed, or the host keeps refusing (e.g. channel deleted).
                    else if ((now - _channelRequestSentUtc).TotalMilliseconds >= 600.0)
                        SendChannelRequest();
                }

                bool keepAliveDue = (now - _lastKeepAliveUtc).TotalMilliseconds >= 1500.0;
                if (keepAliveDue)
                {
                    _lastKeepAliveUtc = now;
                    SendPacket(_hostEndpoint, PacketKeepAlive, _localId, 0, new byte[] { LocalFlags });
                    SendPing();
                }
                ClientChatMaintenance(now, keepAliveDue);
                ClientProfileMaintenance(now);
                StreamLinkMaintenance(now);
            }
        }

        /// <summary>Asks the host to move us; re-sent by maintenance until the peer list confirms it.</summary>
        private void RequestChannel(int channelId)
        {
            _requestedChannelId = channelId;
            _channelRequestAttempts = 0;
            SendChannelRequest();
        }

        private void SendChannelRequest()
        {
            int channelId = _requestedChannelId;
            if (channelId < 0)
                return;
            _channelRequestAttempts++;
            _channelRequestSentUtc = DateTime.UtcNow;
            if (_hostEndpoint != null)
                SendPacket(_hostEndpoint, PacketChannelSwitch, _localId, 0, BitConverter.GetBytes(channelId));
        }

        private void SendJoin()
        {
            if (!_running || _hostEndpoint == null)
                return;

            _lastJoinSentUtc = DateTime.UtcNow;
            SendJoinSecure();
        }

        private void RemoveHostParticipant(string key, bool broadcastNow)
        {
            Participant removed = null;
            lock (_stateLock)
            {
                if (_hostParticipants.TryGetValue(key, out removed))
                {
                    _hostParticipants.Remove(key);
                    if (removed.Session != null)
                    {
                        // Disposed later: sends to this participant may still be in flight.
                        removed.Session = null;
                    }
                    _memberNames.Remove(removed.Id);
                    _memberChannels.Remove(removed.Id);
                    _peerVolumes.Remove(removed.Id);
                    _peerMutes.Remove(removed.Id);
                    _lastSpeakingUtc.Remove(removed.Id);
                    _memberFlags.Remove(removed.Id);
                    ForgetProfileLocked(removed.Id);
                }
            }

            if (removed != null)
            {
                DisposePlaybackStream(removed.Id);
                SetLastMessage(removed.Name + " left.");
                if (broadcastNow)
                    BroadcastPeerList();
            }
        }

        /// <summary>Sends the member list after a change, and schedules quick repeats.</summary>
        private void BroadcastPeerList()
        {
            _peerListRepeats = 2;
            SendPeerListNow(false);
        }

        private void SendPeerListNow(bool isRepeat)
        {
            if (isRepeat && _peerListRepeats > 0)
                _peerListRepeats--;
            _lastPeerListBroadcastUtc = DateTime.UtcNow;
            byte[] payload = BuildPeerListPayload();
            List<IPEndPoint> targets = new List<IPEndPoint>();

            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    targets.Add(new IPEndPoint(pair.Value.Endpoint.Address, pair.Value.Endpoint.Port));
            }

            int i;
            for (i = 0; i < targets.Count; i++)
                SendPacket(targets[i], PacketPeerList, _localId, 0, payload);
        }

        private byte[] BuildPeerListPayload()
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);

            lock (_stateLock)
            {
                writer.Write(_localId);

                List<int> channelIds = new List<int>(_channels.Keys);
                channelIds.Sort();
                writer.Write((byte)Math.Min(255, channelIds.Count));
                int c;
                for (c = 0; c < channelIds.Count; c++)
                {
                    int channelId = channelIds[c];
                    string channelName = _channels[channelId];
                    byte[] channelBytes = Encoding.UTF8.GetBytes(channelName == null ? String.Empty : channelName);
                    if (channelBytes.Length > 80)
                    {
                        byte[] trimmedChannel = new byte[80];
                        Buffer.BlockCopy(channelBytes, 0, trimmedChannel, 0, 80);
                        channelBytes = trimmedChannel;
                    }
                    writer.Write(channelId);
                    writer.Write((byte)channelBytes.Length);
                    writer.Write(channelBytes);
                }

                writer.Write((byte)Math.Min(255, _memberNames.Count));
                foreach (KeyValuePair<uint, string> pair in _memberNames)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(pair.Value == null ? String.Empty : pair.Value);
                    if (nameBytes.Length > 80)
                    {
                        byte[] trimmed = new byte[80];
                        Buffer.BlockCopy(nameBytes, 0, trimmed, 0, 80);
                        nameBytes = trimmed;
                    }

                    int channelId;
                    if (!_memberChannels.TryGetValue(pair.Key, out channelId))
                        channelId = 0;

                    writer.Write(pair.Key);
                    writer.Write(channelId);
                    writer.Write((byte)nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write(FlagsOf(pair.Key));
                }

                // Extensions. Older versions stop reading after the member list, so anything
                // added here is ignored by them. Each section: tag byte, then its data.
                List<int> textIds = new List<int>();
                foreach (KeyValuePair<int, byte> kind in _channelKinds)
                {
                    if (kind.Value == ChannelKindText && _channels.ContainsKey(kind.Key))
                        textIds.Add(kind.Key);
                }
                if (textIds.Count > 0)
                {
                    writer.Write(PeerListSectionTextChannels);
                    writer.Write((byte)Math.Min(255, textIds.Count));
                    for (int t = 0; t < textIds.Count && t < 255; t++)
                        writer.Write(textIds[t]);
                }
                WriteProfileSection(writer);
                WriteStreamSection(writer);

                // Last: older versions stop reading at a section they don't know.
                byte[] roomName = Encoding.UTF8.GetBytes(_roomName ?? String.Empty);
                if (roomName.Length > 0 && roomName.Length <= 255)
                {
                    writer.Write(PeerListSectionRoomName);
                    writer.Write((byte)roomName.Length);
                    writer.Write(roomName);
                }
                WriteRoomArtSection(writer);
            }

            writer.Flush();
            return stream.ToArray();
        }

        private const byte PeerListSectionTextChannels = 1;
        private const byte PeerListSectionRoomName = 4;
        private const int MaxRoomNameChars = 40;
        private volatile string _roomName = String.Empty;

        /// <summary>The name the host gave this room, for everyone in it. Empty until it's known.</summary>
        public string RoomName { get { return _roomName ?? String.Empty; } }

        /// <summary>Hosts choose freely, so invisible and direction-changing characters are removed.</summary>
        internal static string CleanRoomName(string name)
        {
            if (String.IsNullOrEmpty(name))
                return String.Empty;
            StringBuilder clean = new StringBuilder();
            foreach (char c in name)
            {
                if (Char.IsControl(c) || Char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                    continue;
                clean.Append(c);
            }
            string result = clean.ToString().Trim();
            if (result.Length > MaxRoomNameChars)
                result = result.Substring(0, MaxRoomNameChars).Trim();
            if (result.Length > 0 && Char.IsHighSurrogate(result[result.Length - 1]))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        private void ParsePeerList(byte[] payload)
        {
            if (payload == null || payload.Length < 6)
                return;

            Dictionary<int, string> parsedChannels = new Dictionary<int, string>();
            Dictionary<uint, string> parsedNames = new Dictionary<uint, string>();
            Dictionary<uint, int> parsedMemberChannels = new Dictionary<uint, int>();
            Dictionary<uint, byte> parsedFlags = new Dictionary<uint, byte>();
            int offset = 0;

            if (offset + 4 > payload.Length)
                return;
            uint parsedHostId = BitConverter.ToUInt32(payload, offset);
            offset += 4;

            if (offset >= payload.Length)
                return;
            int channelCount = payload[offset++];
            int i;
            for (i = 0; i < channelCount; i++)
            {
                if (offset + 5 > payload.Length)
                    return;
                int channelId = BitConverter.ToInt32(payload, offset);
                offset += 4;
                int channelNameLength = payload[offset++];
                if (offset + channelNameLength > payload.Length)
                    return;
                string channelName = Encoding.UTF8.GetString(payload, offset, channelNameLength);
                offset += channelNameLength;
                if (channelId > 0)
                    parsedChannels[channelId] = channelName;
            }

            if (offset >= payload.Length)
                return;
            int memberCount = payload[offset++];
            for (i = 0; i < memberCount; i++)
            {
                if (offset + 9 > payload.Length)
                    return;
                uint id = BitConverter.ToUInt32(payload, offset);
                offset += 4;
                int channelId = BitConverter.ToInt32(payload, offset);
                offset += 4;
                int nameLength = payload[offset++];
                if (offset + nameLength > payload.Length)
                    return;
                string name = Encoding.UTF8.GetString(payload, offset, nameLength);
                offset += nameLength;
                if (offset + 1 > payload.Length)
                    return;
                parsedFlags[id] = payload[offset++];
                parsedNames[id] = name;
                parsedMemberChannels[id] = channelId;
            }

            // Extension sections (see BuildPeerListPayload). Unknown sections end parsing.
            HashSet<int> parsedTextChannels = new HashSet<int>();
            Dictionary<uint, uint> parsedProfiles = new Dictionary<uint, uint>();
            Dictionary<uint, LiveStreamInfo> parsedStreams = new Dictionary<uint, LiveStreamInfo>();
            while (offset < payload.Length)
            {
                byte section = payload[offset++];
                if (section == PeerListSectionStreams)
                {
                    offset = ReadStreamSection(payload, offset, parsedStreams);
                    if (offset < 0)
                        break;
                    continue;
                }
                if (section == PeerListSectionProfiles)
                {
                    offset = ReadProfileSection(payload, offset, parsedProfiles);
                    if (offset < 0)
                        break;
                    continue;
                }
                if (section == PeerListSectionRoomArt)
                {
                    offset = ReadRoomArtSection(payload, offset);
                    if (offset < 0)
                        break;
                    continue;
                }
                if (section == PeerListSectionRoomName)
                {
                    if (offset >= payload.Length || offset + 1 + payload[offset] > payload.Length)
                        break;
                    int roomNameLength = payload[offset++];
                    _roomName = CleanRoomName(Encoding.UTF8.GetString(payload, offset, roomNameLength));
                    offset += roomNameLength;
                    continue;
                }
                if (section != PeerListSectionTextChannels || offset >= payload.Length)
                    break;
                int textCount = payload[offset++];
                if (offset + textCount * 4 > payload.Length)
                    break;
                for (i = 0; i < textCount; i++)
                {
                    int textId = BitConverter.ToInt32(payload, offset);
                    offset += 4;
                    if (parsedChannels.ContainsKey(textId))
                        parsedTextChannels.Add(textId);
                }
            }

            List<uint> removedStreams = new List<uint>();
            List<StreamLink> endedLinks = null;
            int oldLocalChannel;
            lock (_stateLock)
            {
                oldLocalChannel = _localChannelId;
                foreach (KeyValuePair<uint, string> oldPair in _memberNames)
                {
                    if (!parsedNames.ContainsKey(oldPair.Key) && oldPair.Key != _localId)
                        removedStreams.Add(oldPair.Key);
                }

                for (i = 0; i < removedStreams.Count; i++)
                {
                    _peerVolumes.Remove(removedStreams[i]);
                    _peerMutes.Remove(removedStreams[i]);
                    _lastSpeakingUtc.Remove(removedStreams[i]);
                }

                _channels.Clear();
                foreach (KeyValuePair<int, string> channelPair in parsedChannels)
                    _channels[channelPair.Key] = channelPair.Value;
                _channelKinds.Clear();
                foreach (int textId in parsedTextChannels)
                    _channelKinds[textId] = ChannelKindText;
                ApplyAdvertisedProfilesLocked(parsedProfiles);
                endedLinks = ApplyLiveStreamsLocked(parsedStreams);
                if (_viewChannelId != 0 && !_channels.ContainsKey(_viewChannelId))
                    _viewChannelId = _channels.ContainsKey(1) ? 1 : 0;

                _memberNames.Clear();
                _memberChannels.Clear();
                _hostId = parsedHostId;
                foreach (KeyValuePair<uint, string> pair in parsedNames)
                    _memberNames[pair.Key] = pair.Value;
                foreach (KeyValuePair<uint, int> pair in parsedMemberChannels)
                    _memberChannels[pair.Key] = pair.Value;
                _memberFlags.Clear();
                foreach (KeyValuePair<uint, byte> pair in parsedFlags)
                {
                    if (pair.Key != _localId)
                        _memberFlags[pair.Key] = pair.Value;
                }

                int localChannel;
                _localChannelId = _memberChannels.TryGetValue(_localId, out localChannel) ? localChannel : 0;
            }

            if (endedLinks != null)
                foreach (StreamLink ended in endedLinks)
                    ended.Dispose();
            if (_localStreamId != 0 && _localChannelId <= 0)
                StopScreenShare(); // Leaving voice ends your stream.
            if (oldLocalChannel != _localChannelId)
                DisposePlaybackStreams();
            else
            {
                for (i = 0; i < removedStreams.Count; i++)
                    DisposePlaybackStream(removedStreams[i]);
            }
        }

        private void OpenWaveIn()
        {
            WAVEFORMATEX format = CreateFormat();
            _waveInProc = WaveInCallback;

            int result = NativeMethods.waveInOpen(out _waveIn, _inputDeviceId, ref format, _waveInProc, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION);
            CheckMmResult(result, "Could not open the default microphone at 48 kHz");

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            int i;
            for (i = 0; i < CaptureBufferCount; i++)
            {
                NativeBuffer buffer = new NativeBuffer(AudioBytesPerFrame, headerSize);
                _captureBuffers.Add(buffer);

                result = NativeMethods.waveInPrepareHeader(_waveIn, buffer.Header, (uint)headerSize);
                CheckMmResult(result, "Could not prepare a microphone buffer");
                buffer.Prepared = true;

                result = NativeMethods.waveInAddBuffer(_waveIn, buffer.Header, (uint)headerSize);
                CheckMmResult(result, "Could not queue a microphone buffer");
            }
        }

        /// <summary>
        /// Echo cancellation, mic gain, silence gate and Opus encoding for one 10 ms frame,
        /// then sends whatever the encoder decided should go out.
        /// </summary>
        private void ProcessCapturedFrame(byte[] pcm, long captureEnd)
        {
            if (_echoCancellation)
            {
                try
                {
                    if (_echoResetPending)
                    {
                        _echoResetPending = false;
                        _echoCanceller.Reset();
                    }
                    _echoCanceller.Process(pcm, captureEnd);
                }
                catch
                {
                    // Never let audio processing take the microphone down.
                }
            }
            ApplyNoiseSuppression(pcm);
            ApplyAutoGain(pcm);
            ScalePcm16InPlace(pcm, _micVolumePercent / 100.0f);

            List<EncodedVoice> packets = _encodedScratch;
            packets.Clear();
            try
            {
                _voiceEncoder.Push(pcm, packets);
            }
            catch
            {
                _voiceEncoder = new VoiceEncoder(); // Recover from a codec fault.
                return;
            }

            if (_voiceEncoder.IsSpeaking)
            {
                lock (_stateLock)
                    _lastSpeakingUtc[_localId] = DateTime.UtcNow;
            }
            int i;
            for (i = 0; i < packets.Count; i++)
                SendLocalAudio(packets[i].Sequence, packets[i].Data);
        }

        /// <summary>Tests only: feeds one 10 ms frame as if it came from the microphone.</summary>
        internal void InjectCapturedFrameForTests(byte[] pcm)
        {
            if (_running && MicrophoneOpen)
                ProcessCapturedFrame(pcm, _farEnd.AdvanceCapture(pcm.Length / 2));
        }

        /// <summary>Tests only: makes the encoder send every frame regardless of the silence gate.</summary>
        internal bool SendSilenceForTests
        {
            get { return _voiceEncoder.AlwaysSend; }
            set { _voiceEncoder.AlwaysSend = value; }
        }

        /// <summary>Tests only: receives every decoded remote frame (sender id, 20 ms PCM).</summary>
        internal Action<uint, byte[]> DecodedAudioForTests { get; set; }

        private void SendLocalAudio(uint sequence, byte[] opus)
        {
            uint senderId = _localId;
            int localChannel = _localChannelId;
            if (senderId == 0 || localChannel <= 0)
                return;

            byte[] raw = BuildPacket(PacketAudio, senderId, sequence, opus);

            if (_isHost)
            {
                List<IPEndPoint> targets = new List<IPEndPoint>();
                lock (_stateLock)
                {
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        if (pair.Value.ChannelId == localChannel)
                            targets.Add(new IPEndPoint(pair.Value.Endpoint.Address, pair.Value.Endpoint.Port));
                    }
                }

                int i;
                for (i = 0; i < targets.Count; i++)
                    SendRaw(targets[i], raw);
            }
            else if (_connected && _hostEndpoint != null)
            {
                SendRaw(_hostEndpoint, raw);
            }
        }

        /// <summary>Decodes a remote speaker's Opus packet and queues the audio. Receive thread only.</summary>
        private void QueueRemoteAudio(uint senderId, uint sequence, byte[] opus)
        {
            PlaybackStream stream;
            VoiceDecoder decoder;
            lock (_stateLock)
            {
                // Senders only transmit while talking, so a packet means they are speaking.
                _lastSpeakingUtc[senderId] = DateTime.UtcNow;
                if (!_voiceDecoders.TryGetValue(senderId, out decoder))
                {
                    decoder = new VoiceDecoder();
                    _voiceDecoders[senderId] = decoder;
                }
                _playbackStreams.TryGetValue(senderId, out stream);
            }

            if (stream == null && !HeadlessForTests)
                stream = OpenPlaybackStream(senderId);

            List<byte[]> frames = _decodedScratch;
            frames.Clear();
            decoder.Decode(sequence, opus, frames);

            Action<uint, byte[]> testSink = DecodedAudioForTests;
            int i;
            for (i = 0; i < frames.Count; i++)
            {
                if (stream != null)
                    stream.Enqueue(frames[i]);
                if (testSink != null)
                    testSink(senderId, frames[i]);
            }
        }

        /// <summary>
        /// Opens a speaker stream for one sender. The device is opened with no lock held:
        /// waveOutOpen can wait on WinMM's internal lock, and holding _stateLock while it does
        /// is how audio deadlocks happen.
        /// </summary>
        private PlaybackStream OpenPlaybackStream(uint senderId)
        {
            PlaybackStream created;
            try { created = new PlaybackStream(CreateFormat(), _outputDeviceId, _farEnd); }
            catch { return null; }

            PlaybackStream existing = null;
            bool added = false;
            lock (_stateLock)
            {
                if (_running && !_playbackStreams.TryGetValue(senderId, out existing))
                {
                    _playbackStreams[senderId] = created;
                    created.SetGain(senderId == CueStreamId ? _masterVolumePercent / 100.0f : GetEffectivePlaybackGainLocked(senderId));
                    added = true;
                }
            }
            if (added)
                return created;
            created.Dispose(); // Shutting down, or another thread opened one first.
            return existing;
        }

        private float GetEffectivePlaybackGainLocked(uint senderId)
        {
            if (_deafened || _peerMutes.Contains(senderId))
                return 0.0f;

            int peerVolume;
            if (!_peerVolumes.TryGetValue(senderId, out peerVolume))
                peerVolume = 100;

            return (_masterVolumePercent / 100.0f) * (peerVolume / 100.0f);
        }

        private void UpdatePlaybackGain(uint senderId)
        {
            PlaybackStream stream = null;
            float gain = 1.0f;
            lock (_stateLock)
            {
                gain = GetEffectivePlaybackGainLocked(senderId);
                _playbackStreams.TryGetValue(senderId, out stream);
            }
            if (stream != null)
                stream.SetGain(gain);
        }

        private void UpdateAllPlaybackGains()
        {
            List<KeyValuePair<PlaybackStream, float>> updates = new List<KeyValuePair<PlaybackStream, float>>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, PlaybackStream> pair in _playbackStreams)
                {
                    if (pair.Key != CueStreamId)
                        updates.Add(new KeyValuePair<PlaybackStream, float>(pair.Value, GetEffectivePlaybackGainLocked(pair.Key)));
                }
            }

            int i;
            for (i = 0; i < updates.Count; i++)
                updates[i].Key.SetGain(updates[i].Value);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static void ScalePcm16InPlace(byte[] pcm, float gain)
        {
            if (pcm == null || pcm.Length < 2 || Math.Abs(gain - 1.0f) < 0.001f)
                return;

            int i;
            for (i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int scaled = (int)(sample * gain);
                if (scaled > Int16.MaxValue)
                    scaled = Int16.MaxValue;
                else if (scaled < Int16.MinValue)
                    scaled = Int16.MinValue;

                pcm[i] = (byte)(scaled & 0xFF);
                pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        private void DisposePlaybackStream(uint senderId)
        {
            PlaybackStream stream = null;
            lock (_stateLock)
            {
                if (_playbackStreams.TryGetValue(senderId, out stream))
                    _playbackStreams.Remove(senderId);
                _voiceDecoders.Remove(senderId);
            }

            if (stream != null)
                stream.Dispose();
        }

        private void DisposePlaybackStreams()
        {
            List<PlaybackStream> streams = new List<PlaybackStream>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, PlaybackStream> pair in _playbackStreams)
                    streams.Add(pair.Value);
                _playbackStreams.Clear();
                _voiceDecoders.Clear();
            }

            int i;
            for (i = 0; i < streams.Count; i++)
                streams[i].Dispose();
        }

        private void SendPacket(IPEndPoint target, byte type, uint senderId, uint sequence, byte[] payload)
        {
            SendRaw(target, BuildPacket(type, senderId, sequence, payload));
        }

        private void SendPacketVia(SecureSession session, IPEndPoint target, byte type, uint senderId, uint sequence, byte[] payload)
        {
            SendSealed(SessionFor(target, session), target, BuildPacket(type, senderId, sequence, payload));
        }

        /// <summary>Encrypts an inner packet for <paramref name="target"/> and sends it.</summary>
        private void SendRaw(IPEndPoint target, byte[] raw)
        {
            if (target == null || raw == null)
                return;
            SendSealed(SessionFor(target, null), target, raw);
        }

        private void SendSealed(SecureSession session, IPEndPoint target, byte[] raw)
        {
            if (session == null || target == null || raw == null)
                return; // No secure session yet: never send in the clear.
            byte[] sealedPacket;
            try
            {
                sealedPacket = session.Send.Seal(OuterHeader(OuterData), raw);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            SendDatagram(target, sealedPacket);
        }

        /// <summary>Tests only: sees every datagram exactly as it goes on the wire.</summary>
        internal Action<byte[]> WireTapForTests { get; set; }

        private void SendDatagram(IPEndPoint target, byte[] raw)
        {
            if (!_running || _socket == null || target == null || raw == null)
                return;
            Action<byte[]> tap = WireTapForTests;
            if (tap != null)
                tap(raw);
            if (PacketLossForTests > 0.0)
            {
                lock (_testLossRandom)
                {
                    if (_testLossRandom.NextDouble() < PacketLossForTests)
                        return;
                }
            }

            try
            {
                lock (_sendLock)
                {
                    if (_socket != null)
                        _socket.SendTo(raw, target);
                }
                Interlocked.Increment(ref _txPackets);
                Interlocked.Add(ref _txBytes, raw.Length + 28); // + IPv4/UDP headers
            }
            catch
            {
            }
        }

        private static byte[] BuildPacket(byte type, uint senderId, uint sequence, byte[] payload)
        {
            int payloadLength = payload == null ? 0 : payload.Length;
            byte[] data = new byte[HeaderBytes + payloadLength];

            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, data, 0, 4);
            data[4] = ProtocolVersion;
            data[5] = type;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)payloadLength), 0, data, 6, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(senderId), 0, data, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, data, 12, 4);

            if (payloadLength > 0)
                Buffer.BlockCopy(payload, 0, data, HeaderBytes, payloadLength);

            return data;
        }

        private static bool TryParsePacket(byte[] data, int length, out Packet packet)
        {
            packet = null;
            if (data == null || length < HeaderBytes)
                return false;
            if (BitConverter.ToUInt32(data, 0) != Magic)
                return false;
            if (data[4] != ProtocolVersion)
                return false;

            int payloadLength = BitConverter.ToUInt16(data, 6);
            if (payloadLength < 0 || HeaderBytes + payloadLength != length)
                return false;

            Packet result = new Packet();
            result.Type = data[5];
            result.SenderId = BitConverter.ToUInt32(data, 8);
            result.Sequence = BitConverter.ToUInt32(data, 12);
            result.Payload = new byte[payloadLength];
            if (payloadLength > 0)
                Buffer.BlockCopy(data, HeaderBytes, result.Payload, 0, payloadLength);
            packet = result;
            return true;
        }

        private uint CreateUniqueParticipantId()
        {
            uint id;
            do
            {
                id = CreateParticipantId();
            }
            while (id == _localId || _memberNames.ContainsKey(id));
            return id;
        }

        private static uint CreateParticipantId()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            uint id = BitConverter.ToUInt32(bytes, 0);
            return id == 0 ? 1U : id;
        }

        private static string DecodeName(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return String.Empty;
            string value = Encoding.UTF8.GetString(payload).Trim();
            if (value.Length > 24)
                value = value.Substring(0, 24);
            return value;
        }

        private static string EndpointKey(IPEndPoint endpoint)
        {
            return endpoint.Address.ToString() + ":" + endpoint.Port;
        }

        private void SetLastMessage(string message)
        {
            lock (_stateLock)
                _lastMessage = message == null ? String.Empty : message;
        }

        private static WAVEFORMATEX CreateFormat()
        {
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = 1;
            format.nChannels = Channels;
            format.nSamplesPerSec = SampleRate;
            format.wBitsPerSample = BitsPerSample;
            format.nBlockAlign = (ushort)(Channels * BitsPerSample / 8);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
            format.cbSize = 0;
            return format;
        }

        private static void CheckMmResult(int result, string message)
        {
            if (result != NativeMethods.MMSYSERR_NOERROR)
                throw new InvalidOperationException(message + ". WinMM error code: " + result);
        }

        public void Dispose()
        {
            // Always say goodbye before closing, so nobody is left with a stale connection.
            // UDP can drop a packet, so it goes out three times; repeats are ignored.
            if (!_isHost && _running && _connected && _hostEndpoint != null && _localId != 0)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { SendPacket(_hostEndpoint, PacketLeave, _localId, 0, new byte[0]); }
                    catch { }
                }
            }
            else if (_isHost && _running)
            {
                // Host closing: tell every member straight away instead of letting them time out.
                List<IPEndPoint> members = new List<IPEndPoint>();
                lock (_stateLock)
                {
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                        members.Add(pair.Value.Endpoint);
                }
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    foreach (IPEndPoint member in members)
                    {
                        try { SendPacket(member, PacketLeave, _localId, 0, new byte[0]); }
                        catch { }
                    }
                }
            }

            StopScheduler();
            _running = false;
            _connected = false;

            Socket socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try { socket.Close(); } catch { }
            }

            Thread receiveThread = _receiveThread;
            _receiveThread = null;
            if (receiveThread != null && receiveThread.IsAlive && receiveThread != Thread.CurrentThread)
            {
                try { receiveThread.Join(800); } catch { }
            }

            Thread maintenanceThread = _maintenanceThread;
            _maintenanceThread = null;
            if (maintenanceThread != null && maintenanceThread.IsAlive && maintenanceThread != Thread.CurrentThread)
            {
                try { maintenanceThread.Join(800); } catch { }
            }

            ResetStreams();

            // Stop the capture thread first, so no buffer is handed back or read while closing.
            IntPtr waveIn = StopCapture();
            if (waveIn != IntPtr.Zero)
            {
                try { NativeMethods.waveInStop(waveIn); } catch { }
                try { NativeMethods.waveInReset(waveIn); } catch { }

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                int i;
                for (i = 0; i < _captureBuffers.Count; i++)
                {
                    NativeBuffer buffer = _captureBuffers[i];
                    if (buffer.Prepared)
                    {
                        try { NativeMethods.waveInUnprepareHeader(waveIn, buffer.Header, (uint)headerSize); } catch { }
                        buffer.Prepared = false;
                    }
                    buffer.Free();
                }
                _captureBuffers.Clear();

                try { NativeMethods.waveInClose(waveIn); } catch { }
            }

            DisposePlaybackStreams();

            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.Session != null)
                        pair.Value.Session.Dispose();
                    pair.Value.Session = null;
                }
                _hostParticipants.Clear();
                _memberNames.Clear();
                _memberChannels.Clear();
                _channels.Clear();
            }
            ResetSecurity();
        }

        private sealed class Packet
        {
            public byte Type;
            public uint SenderId;
            public uint Sequence;
            public byte[] Payload;
        }

        private sealed class Participant
        {
            public uint Id;
            public string Name;
            public IPEndPoint Endpoint;
            public DateTime LastSeenUtc;
            public int ChannelId;
            public int ViewChannelId;   // The channel whose chat they have open (may differ from voice).
            public DateTime LastChatUtc;
            public SecureSession Session;
            public readonly Queue<uint> RecentChatIds = new Queue<uint>();
        }
    }

    internal sealed class PlaybackStream : IDisposable
    {
        private const int PrimeFrameCount = 2;        // 2 x 20 ms jitter buffer at the start of each talk spurt.
        private const int MaxOutstandingFrames = 10;  // Cap queued audio at 200 ms so latency can't creep up.

        private readonly object _lock = new object();
        private readonly Dictionary<IntPtr, NativeBuffer> _buffers = new Dictionary<IntPtr, NativeBuffer>();
        private readonly List<byte[]> _primeFrames = new List<byte[]>();
        private IntPtr _waveOut = IntPtr.Zero;
        private WaveOutProc _waveOutProc;
        private bool _disposed;
        private bool _primed;
        private int _outstandingFrames;
        private volatile float _gain = 1.0f;
        private readonly FarEndReference _reference;
        private readonly bool _stereo;
        private long _renderPosition;

        public PlaybackStream(WAVEFORMATEX format, uint outputDeviceId, FarEndReference reference)
        {
            _reference = reference;
            _stereo = format.nChannels == 2;
            _waveOutProc = WaveOutCallback;
            int result = NativeMethods.waveOutOpen(out _waveOut, outputDeviceId, ref format, _waveOutProc, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION);
            if (result != NativeMethods.MMSYSERR_NOERROR)
                throw new InvalidOperationException("Could not open the default speakers. WinMM error code: " + result);
        }

        public void SetGain(float gain)
        {
            if (gain < 0.0f)
                gain = 0.0f;
            if (gain > 3.0f)
                gain = 3.0f;
            _gain = gain;
        }

        /// <summary>Plays a short clip straight away (no jitter buffering).</summary>
        public void PlayClip(byte[] pcm)
        {
            if (_disposed || pcm == null || pcm.Length == 0)
                return;
            QueueBuffer(pcm);
        }

        /// <summary>Queues one decoded PCM frame for playback.</summary>
        public void Enqueue(byte[] pcm)
        {
            if (_disposed || pcm == null || pcm.Length == 0)
                return;

            // Speakers stop sending while quiet, so the queue drains between sentences.
            // Re-prime after that so each new talk spurt gets a small jitter buffer again.
            if (_primed)
            {
                lock (_lock)
                {
                    if (_outstandingFrames == 0)
                        _primed = false;
                }
            }
            EnqueueFrame(pcm);
        }

        private void EnqueueFrame(byte[] pcm)
        {
            if (!_primed)
            {
                _primeFrames.Add(pcm);
                if (_primeFrames.Count < PrimeFrameCount)
                    return;

                int i;
                for (i = 0; i < _primeFrames.Count; i++)
                    QueueBuffer(_primeFrames[i]);
                _primeFrames.Clear();
                _primed = true;
                return;
            }

            QueueBuffer(pcm);
        }

        private void QueueBuffer(byte[] pcm)
        {
            lock (_lock)
            {
                if (_disposed || _waveOut == IntPtr.Zero)
                    return;
                if (_outstandingFrames >= MaxOutstandingFrames)
                    return;
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            NativeBuffer buffer = new NativeBuffer(pcm.Length, headerSize);
            float gain = _gain;
            byte[] played = pcm;
            if (Math.Abs(gain - 1.0f) >= 0.001f)
            {
                played = new byte[pcm.Length];
                Buffer.BlockCopy(pcm, 0, played, 0, pcm.Length);
                ScalePcm16InPlace(played, gain);
            }
            Marshal.Copy(played, 0, buffer.Data, played.Length);
            buffer.WaveHandle = _waveOut;

            int result = NativeMethods.waveOutPrepareHeader(_waveOut, buffer.Header, (uint)headerSize);
            if (result != NativeMethods.MMSYSERR_NOERROR)
            {
                buffer.Free();
                return;
            }

            buffer.Prepared = true;
            lock (_lock)
            {
                if (_disposed || _waveOut == IntPtr.Zero)
                {
                    try { NativeMethods.waveOutUnprepareHeader(buffer.WaveHandle, buffer.Header, (uint)headerSize); } catch { }
                    buffer.Free();
                    return;
                }
                if (_reference != null)
                {
                    // WinMM plays queued buffers back to back. When the queue has drained the
                    // next buffer starts "now", so re-anchor to the capture clock.
                    long now = _reference.CaptureClock;
                    if (_outstandingFrames == 0 || _renderPosition < now)
                        _renderPosition = now;
                    byte[] heard = _stereo ? DownmixToMono(played) : played; // The reference is mono.
                    _reference.Write(_renderPosition, heard);
                    _renderPosition += heard.Length / 2;
                }
                _buffers[buffer.Header] = buffer;
                _outstandingFrames++;
            }

            result = NativeMethods.waveOutWrite(_waveOut, buffer.Header, (uint)headerSize);
            if (result != NativeMethods.MMSYSERR_NOERROR)
                CleanupBuffer(buffer.Header);
        }

        private static byte[] DownmixToMono(byte[] stereo)
        {
            byte[] mono = new byte[stereo.Length / 2];
            for (int i = 0; i + 3 < stereo.Length; i += 4)
            {
                int mixed = ((short)(stereo[i] | (stereo[i + 1] << 8)) + (short)(stereo[i + 2] | (stereo[i + 3] << 8))) / 2;
                mono[i / 2] = (byte)mixed;
                mono[i / 2 + 1] = (byte)(mixed >> 8);
            }
            return mono;
        }

        private void WaveOutCallback(IntPtr hwo, uint message, IntPtr instance, IntPtr headerPtr, IntPtr reserved)
        {
            if (message != NativeMethods.WOM_DONE || headerPtr == IntPtr.Zero)
                return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                CleanupBuffer((IntPtr)state);
            }, headerPtr);
        }

        private void CleanupBuffer(IntPtr headerPtr)
        {
            NativeBuffer buffer = null;
            lock (_lock)
            {
                if (_buffers.TryGetValue(headerPtr, out buffer))
                {
                    _buffers.Remove(headerPtr);
                    if (_outstandingFrames > 0)
                        _outstandingFrames--;
                }
            }

            if (buffer == null)
                return;

            if (buffer.Prepared && buffer.WaveHandle != IntPtr.Zero)
            {
                try { NativeMethods.waveOutUnprepareHeader(buffer.WaveHandle, buffer.Header, (uint)Marshal.SizeOf(typeof(WAVEHDR))); } catch { }
                buffer.Prepared = false;
            }
            buffer.Free();
        }

        private static void ScalePcm16InPlace(byte[] pcm, float gain)
        {
            int i;
            for (i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int scaled = (int)(sample * gain);
                if (scaled > Int16.MaxValue)
                    scaled = Int16.MaxValue;
                else if (scaled < Int16.MinValue)
                    scaled = Int16.MinValue;
                pcm[i] = (byte)(scaled & 0xFF);
                pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        public void Dispose()
        {
            IntPtr waveOut;
            List<NativeBuffer> buffers;

            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                waveOut = _waveOut;
                _waveOut = IntPtr.Zero;
                buffers = new List<NativeBuffer>(_buffers.Values);
                _buffers.Clear();
                _outstandingFrames = 0;
                _primeFrames.Clear();
            }

            if (waveOut != IntPtr.Zero)
            {
                try { NativeMethods.waveOutReset(waveOut); } catch { }

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                int i;
                for (i = 0; i < buffers.Count; i++)
                {
                    NativeBuffer buffer = buffers[i];
                    if (buffer.Prepared)
                    {
                        try { NativeMethods.waveOutUnprepareHeader(waveOut, buffer.Header, (uint)headerSize); } catch { }
                        buffer.Prepared = false;
                    }
                    buffer.Free();
                }

                try { NativeMethods.waveOutClose(waveOut); } catch { }
            }
        }
    }

    internal sealed class NativeBuffer
    {
        public IntPtr Data;
        public IntPtr Header;
        public IntPtr WaveHandle;
        public bool Prepared;

        public NativeBuffer(int dataLength, int headerSize)
        {
            Data = Marshal.AllocHGlobal(dataLength);
            Header = Marshal.AllocHGlobal(headerSize);
            WaveHandle = IntPtr.Zero;
            Prepared = false;

            byte[] zeros = new byte[dataLength];
            Marshal.Copy(zeros, 0, Data, dataLength);

            WAVEHDR header = new WAVEHDR();
            header.lpData = Data;
            header.dwBufferLength = (uint)dataLength;
            header.dwBytesRecorded = 0;
            header.dwUser = IntPtr.Zero;
            header.dwFlags = 0;
            header.dwLoops = 0;
            header.lpNext = IntPtr.Zero;
            header.reserved = IntPtr.Zero;
            Marshal.StructureToPtr(header, Header, false);
        }

        public void Free()
        {
            if (Header != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Header);
                Header = IntPtr.Zero;
            }
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    internal delegate void WaveInProc(IntPtr hwi, uint message, IntPtr instance, IntPtr param1, IntPtr param2);
    internal delegate void WaveOutProc(IntPtr hwo, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    internal static class NativeMethods
    {
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint CALLBACK_FUNCTION = 0x00030000;
        public const uint WIM_DATA = 0x03C0;
        public const uint WOM_DONE = 0x03BD;
        public const int MMSYSERR_NOERROR = 0;

        [DllImport("winmm.dll")]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveInGetDevCaps(uint deviceId, out WAVEINCAPS caps, uint size);

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveOutGetDevCaps(uint deviceId, out WAVEOUTCAPS caps, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr waveIn, uint deviceId, ref WAVEFORMATEX format, WaveInProc callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInStart(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInStop(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInReset(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInClose(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr waveOut, uint deviceId, ref WAVEFORMATEX format, WaveOutProc callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr waveOut);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr waveOut);
    }
}

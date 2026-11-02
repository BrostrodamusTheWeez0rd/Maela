using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Maela
{
    /// <summary>
    /// Opens the Spatial window on its own thread, so a stall or crash in it cannot hold up the
    /// main window or a call. See MainForm.Spatial.cs for how it gets its data.
    /// </summary>
    internal static class SpatialTest
    {
        public static void ShowProfile(SpatialProfile profile)
        {
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.BeginInvoke((Action)delegate { if (_window != null) _window.ShowProfile(profile); });
        }

        private static readonly object Gate = new object();
        private static Dispatcher _dispatcher;
        private static SpatialWindow _window;
        private static int _sendsWaiting;
        private static bool _closingOnPurpose;
        private static volatile bool _onScreen, _active;
        private static IntPtr _handle;

        public static bool IsOpen { get { return _window != null; } }
        public static bool WindowOnScreen { get { return _onScreen; } }
        public static bool WindowActive { get { return _active; } }
        public static IntPtr WindowHandle { get { return _handle; } }

        internal static void NoteWindowState(bool onScreen, bool active, IntPtr handle)
        {
            _onScreen = onScreen;
            _active = active;
            _handle = handle;
        }

        public static void ShowMenu(List<SpatialMenuEntry> entries)
        {
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.BeginInvoke((Action)delegate { if (_window != null) _window.ShowMenu(entries); });
        }

        public static void HideWindow()
        {
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.BeginInvoke((Action)delegate { if (_window != null) _window.Hide(); });
        }

        /// <summary>Switching to Classic: close it for real, keeping the app running.</summary>
        public static void CloseWindow()
        {
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher == null)
                return;
            _closingOnPurpose = true;
            dispatcher.BeginInvoke((Action)delegate { if (_window != null) _window.CloseForGood(); });
        }

        public static void Open(MainForm host)
        {
            lock (Gate)
            {
                if (_dispatcher != null)
                {
                    _dispatcher.BeginInvoke((Action)delegate
                    {
                        if (_window != null)
                        {
                            _window.Show();
                            if (_window.WindowState == WindowState.Minimized)
                                _window.WindowState = WindowState.Normal;
                            _window.Activate();
                        }
                    });
                    return;
                }
                Thread thread = new Thread(delegate() { Run(host); });
                thread.Name = "Spatial";
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }

        /// <summary>Latest state from the UI timer. If the window is behind, this one is dropped.</summary>
        public static void Send(SpatialSnapshot snapshot)
        {
            Dispatcher dispatcher = _dispatcher;
            SpatialWindow window = _window;
            if (dispatcher == null || window == null || Interlocked.CompareExchange(ref _sendsWaiting, 1, 0) != 0)
                return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                Interlocked.Exchange(ref _sendsWaiting, 0);
                window.Apply(snapshot);
            });
        }

        private static void Run(MainForm host)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            lock (Gate)
                _dispatcher = dispatcher;
            dispatcher.UnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                Program.LogCrash(e.Exception);
                MessageBox.Show("The Spatial window ran into a problem, so mæla is switching to the Lightweight layout. Your room or call keeps running.\n\n" + e.Exception.Message, "mæla");
                dispatcher.InvokeShutdown();
            };
            try
            {
                SpatialWindow window = new SpatialWindow(host);
                window.Closed += delegate { dispatcher.InvokeShutdown(); };
                _window = window;
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show("The Spatial window could not open, so mæla is switching to the Lightweight layout.\n\n" + ex.Message, "mæla");
            }
            lock (Gate)
            {
                _dispatcher = null;
                _window = null;
            }
            NoteWindowState(false, false, IntPtr.Zero);
            Interlocked.Exchange(ref _sendsWaiting, 0);
            if (!_closingOnPurpose)
                host.SpatialWindowFailed();
            _closingOnPurpose = false;
        }
    }

    // A copy of the app's state, made on the main UI thread and only read after that.

    internal sealed class SpatialRoom
    {
        public object Key;
        public string Name;
        public string Circle;
        public bool Owned, Active, Online, Checking;
        public int Members, Max;
        public ImageSource Picture;
    }

    internal sealed class SpatialFriend
    {
        public string Id, Name, Status, Circle;
        public bool IsFriend, WantsToBeFriends;
        public int Unread;
    }

    internal sealed class SpatialPerson
    {
        public uint Id;
        public string Name;
        public bool IsLocal, Speaking, Muted, Deafened, Live;
        public int ChannelId;
        public ImageSource Picture;
    }

    internal sealed class SpatialRoomChannel
    {
        public int Id;
        public string Name;
        public bool IsText;
        public readonly List<SpatialPerson> People = new List<SpatialPerson>();
    }

    internal sealed class SpatialLine
    {
        public string Who, Text, Note;
        public ImageSource Picture;
        public DateTime TimeUtc;
        public ulong ReactKey; // The message's sequence or id. 0: it can't be reacted to yet.
        public readonly List<SpatialReaction> Reactions = new List<SpatialReaction>();
    }

    /// <summary>One line of a right-click menu, copied from the Classic menu that does the work.</summary>
    internal sealed class SpatialMenuEntry
    {
        public int Id;
        public string Text;
        public bool Enabled, Checked, Separator;
        public ImageSource Picture;
        public List<SpatialMenuEntry> Children;
    }

    internal sealed class SpatialReaction
    {
        public string Emoji;
        public ImageSource Picture;
        public int Count;
        public bool Mine;
    }

    /// <summary>Everything a profile card shows and needs, for one person in the room.</summary>
    internal sealed class SpatialProfile
    {
        public uint Id;
        public string Name, Status, State;
        public ImageSource Picture;
        public bool IsLocal, IsHost, ViewerIsHost, MutedForMe, CanWatch;
        public string LiveText;          // "Watch stream · 1080p60", or null if they aren't live.
        public int Volume = 100;         // Just for you, 0 to 200.
        public string MyStatus;          // Your own card: Online, Away, Busy or Offline.
        public int ChannelId;
        public readonly List<SpatialRoomChannel> VoiceChannels = new List<SpatialRoomChannel>();
    }

    /// <summary>A circle you sort rooms or friends into. Favourites and the main one can't be deleted.</summary>
    internal sealed class SpatialCircle
    {
        public const string FavouritesId = "fav", MainId = "main";
        public string Id, Name;
        public bool HasPosition; // Stays where you dragged it. Otherwise it's placed automatically.
        public double X, Y;
        public bool Protected { get { return Id == FavouritesId || Id == MainId; } }
    }

    internal sealed class SpatialSnapshot
    {
        public List<SpatialCircle> RoomCircles = new List<SpatialCircle>();
        public List<SpatialCircle> FriendCircles = new List<SpatialCircle>();
        public string MyName, MyStatus, RoomName, Message, ThemeId;
        public ImageSource MyPicture;
        public bool Muted, Deafened, Noise, Running, Connected, IsHost, InVoice, Sharing;
        public int Ping, LocalChannel, ChatChannel;
        public readonly List<SpatialRoom> Rooms = new List<SpatialRoom>();
        public readonly List<SpatialFriend> Friends = new List<SpatialFriend>();
        public readonly List<SpatialRoomChannel> Channels = new List<SpatialRoomChannel>();
        public readonly List<SpatialLine> Chat = new List<SpatialLine>();
        public readonly List<SpatialLine> FriendChat = new List<SpatialLine>();
        public string ChatKey = String.Empty, FriendChatId, FriendChatKey = String.Empty;
        public string StructureKey = String.Empty, TalkKey = String.Empty, DockKey = String.Empty;
    }

    /// <summary>A room or friend in the space.</summary>
    internal sealed class SpatialItem
    {
        public SpatialRoom Room;
        public SpatialFriend Friend;
        public string Name;
        public Color Color;
        public double X, Y, Z;
        public SpatialGroup Group;
        public string Circle
        {
            get
            {
                string circle = Room != null ? Room.Circle : Friend.Circle;
                return String.IsNullOrEmpty(circle) ? SpatialCircle.MainId : circle;
            }
            set
            {
                if (Room != null) Room.Circle = value;
                else Friend.Circle = value;
            }
        }
    }

    /// <summary>A circle as laid out in the space.</summary>
    internal sealed class SpatialGroup
    {
        public string Id, Title;
        public int Count;
        public double X, Y, Radius, LabelY;
        public double Origin; // Where this tab's space starts, so saved positions stay relative to it.
    }

    /// <summary>Something drawn in the space, placed by projecting its position through the camera.</summary>
    internal sealed class SpatialNode
    {
        public FrameworkElement Element;
        public ScaleTransform Scale;
        public TranslateTransform Move;
        public double X, Y, Z;
        public double FromX, FromY, FromZ;
        public double AnchorX, AnchorY;
        public double Progress = 1;
        public double Delay;
        public Action Click;
        public Action RightClick;
        public SpatialItem Item;    // Draggable into another circle.
        public SpatialGroup Group;  // Moves with this circle.
        public bool IsGroupLabel;   // Dragging this moves the circle.
        public Line Link;
        public double Extent; // Big things stay drawn until all of them are off screen.
        public double WrapRadius; // Rooms and channels: the ball's radius, for the dots to wrap.
        public ScaleTransform BallScale; // Grows the ball, not its name, while it's targeted.
        public SpatialPerson Person;     // Someone in a voice channel: click for their card.
        public int ChannelId;            // A channel's id, so people can be dropped onto it.
        public bool VoiceChannel;
        public double Grow;              // 0 to 1: how far it has grown.
        public double ScreenX, ScreenY, ScreenScale; // Where it was last drawn.
        public bool OnScreen = true;
    }

    /// <summary>The drifting dots and the lines that reach for the pointer.</summary>
    internal sealed class SpatialStars : FrameworkElement
    {
        private const int Count = 170;
        private const double Reach = 190;
        private readonly double[] _x = new double[Count], _y = new double[Count], _d = new double[Count];
        private readonly double[] _nearX = new double[Count], _nearY = new double[Count], _nearP = new double[Count];
        private readonly double[] _edgeX = new double[Count], _edgeY = new double[Count];
        private readonly Brush[] _dots = new Brush[8];
        private readonly Pen[] _toPointer = new Pen[8];
        private readonly Pen[] _between = new Pen[8];
        public Point Pointer = new Point(-9999, -9999);
        public double DriftX, DriftY;

        // What the lines reach for: the pointer, or a ball's edge when one is wrapped. It glides
        // between them just as the lines follow the pointer, and grows to the ball's size.
        public bool Wrapping;
        public Point WrapTarget;
        public double WrapTargetRadius;
        private Point _focus = new Point(-9999, -9999);
        private double _focusRadius;
        private double _arrived; // 0 to 1: how far the focus has got onto the ball. The highlight follows this.

        public double Arrived { get { return _arrived; } }
        private readonly Pen[] _wrapPens = new Pen[8];  // Brighter lines while a ball is wrapped.
        private readonly Pen[] _haloPens = new Pen[8];  // The soft glow just outside it.

        public SpatialStars()
        {
            IsHitTestVisible = false;
            Random random = new Random(11);
            int i;
            for (i = 0; i < Count; i++)
            {
                _x[i] = random.NextDouble();
                _y[i] = random.NextDouble();
                _d[i] = 0.2 + random.NextDouble() * 0.8;
            }
            SetColours(Color.FromRgb(0x00, 0xE5, 0xF5), Color.FromRgb(0xB3, 0x5C, 0xFF));
        }

        /// <summary>Dots and pointer lines in the accent, lines between dots in the second accent.</summary>
        public void SetColours(Color accent, Color accent2)
        {
            int i;
            for (i = 0; i < 8; i++)
            {
                SolidColorBrush dot = new SolidColorBrush(Color.FromArgb((byte)(35 + i * 30), accent.R, accent.G, accent.B));
                dot.Freeze();
                _dots[i] = dot;
                SolidColorBrush a = new SolidColorBrush(Color.FromArgb((byte)(i * 22), accent.R, accent.G, accent.B));
                a.Freeze();
                _toPointer[i] = new Pen(a, 1);
                _toPointer[i].Freeze();
                SolidColorBrush b = new SolidColorBrush(Color.FromArgb((byte)(i * 26), accent2.R, accent2.G, accent2.B));
                b.Freeze();
                _between[i] = new Pen(b, 1);
                _between[i].Freeze();
                SolidColorBrush bright = new SolidColorBrush(Color.FromArgb((byte)(50 + i * 29), accent.R, accent.G, accent.B));
                bright.Freeze();
                _wrapPens[i] = new Pen(bright, 1.7);
                _wrapPens[i].Freeze();
                SolidColorBrush glow = new SolidColorBrush(Color.FromArgb((byte)(i * 9), accent.R, accent.G, accent.B));
                glow.Freeze();
                _haloPens[i] = new Pen(glow, 10);
                _haloPens[i].Freeze();
            }
            InvalidateVisual();
        }

        public void Step(double seconds)
        {
            int i;
            for (i = 0; i < Count; i++)
            {
                _y[i] -= 0.0066 * seconds * _d[i];
                if (_y[i] < 0)
                    _y[i] = 1;
            }
            Point target = Wrapping ? WrapTarget : Pointer;
            double targetRadius = Wrapping ? WrapTargetRadius : 0;
            bool away = target.X < -9000 || _focus.X < -9000;
            bool settled = !Wrapping && _focusRadius < 0.5 && Math.Abs(_focus.X - target.X) + Math.Abs(_focus.Y - target.Y) < 3;
            if (away || settled)
            {
                // Plain pointer: follow it exactly, as before.
                _focus = target;
                _focusRadius = targetRadius;
            }
            else
            {
                double glide = Math.Min(1, seconds * 5);
                _focus = new Point(_focus.X + (target.X - _focus.X) * glide, _focus.Y + (target.Y - _focus.Y) * glide);
                _focusRadius += (targetRadius - _focusRadius) * glide;
            }
            // The highlight builds as the focus reaches the ball, and fades as it leaves.
            double arrived = 0;
            if (Wrapping && WrapTargetRadius > 0)
            {
                double fx = _focus.X - WrapTarget.X, fy = _focus.Y - WrapTarget.Y;
                double distance = Math.Sqrt(fx * fx + fy * fy) / (WrapTargetRadius + 40);
                arrived = Math.Max(0, 1 - distance) * Math.Min(1, _focusRadius / WrapTargetRadius);
            }
            _arrived += (arrived - _arrived) * Math.Min(1, seconds * 8);
            if (_arrived < 0.005)
                _arrived = 0;
            InvalidateVisual();
        }

        private static int Level(double value)
        {
            int level = (int)(value * 8);
            return level < 0 ? 0 : (level > 7 ? 7 : level);
        }

        /// <summary>
        /// Dots near the focus lean towards it and draw lines to it. Normally the focus is the
        /// pointer. Over a room or channel it glides onto the ball and grows to its size, so the
        /// dots reach for the nearest point on its edge instead, and the lines land all round it.
        /// </summary>
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double reach = Reach + _focusRadius; // A bigger ball draws from further out.
            double lean = 0.14 + 0.12 * _arrived; // And holds its dots a little closer.
            int near = 0, i, j;
            for (i = 0; i < Count; i++)
            {
                double sx = Wrap(_x[i] - DriftX * 0.05 * _d[i]) * w;
                double sy = Wrap(_y[i] - DriftY * 0.05 * _d[i]) * h;
                double dx = sx - _focus.X, dy = sy - _focus.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy), pull = 0;
                double gap = Math.Max(0, dist - _focusRadius);
                if (gap < reach && dist > 0.001)
                {
                    pull = 1 - gap / reach;
                    // The nearest point on the ball's edge; with no ball, the pointer itself.
                    double ex = _focus.X + dx / dist * _focusRadius, ey = _focus.Y + dy / dist * _focusRadius;
                    sx += (ex - sx) * pull * lean;
                    sy += (ey - sy) * pull * lean;
                    _nearX[near] = sx;
                    _nearY[near] = sy;
                    _nearP[near] = pull;
                    _edgeX[near] = ex;
                    _edgeY[near] = ey;
                    near++;
                }
                double radius = _d[i] * 1.5 + pull * 1.4;
                dc.DrawEllipse(_dots[Level(0.15 + _d[i] * 0.5 + pull * 0.5)], null, new Point(sx, sy), radius, radius);
            }
            if (_arrived > 0)
                dc.DrawEllipse(null, _haloPens[Level(_arrived * 0.9)], _focus, _focusRadius + 7, _focusRadius + 7);

            // Only dots near the focus link up, so this is a handful of lines, not every dot against every dot.
            Pen[] reaching = _arrived > 0.5 ? _wrapPens : _toPointer;
            double link = 110 + _focusRadius * 0.6; // Round a ball, longer links so the net closes.
            for (i = 0; i < near; i++)
            {
                dc.DrawLine(reaching[Level(_nearP[i] * 0.9)], new Point(_nearX[i], _nearY[i]), new Point(_edgeX[i], _edgeY[i]));
                for (j = i + 1; j < near; j++)
                {
                    double ex = _nearX[i] - _nearX[j], ey = _nearY[i] - _nearY[j], q = Math.Sqrt(ex * ex + ey * ey);
                    if (q < link)
                        dc.DrawLine(_between[Level((1 - q / link) * Math.Min(_nearP[i], _nearP[j]) * 1.2)], new Point(_nearX[i], _nearY[i]), new Point(_nearX[j], _nearY[j]));
                }
            }
        }

        private static double Wrap(double value)
        {
            return value - Math.Floor(value);
        }
    }

    internal sealed class SpatialWindow : Window
    {
        private const double Perspective = 1200;
        private const double FriendSpace = 7000;
        private const double NavWidth = 268;

        // The palette follows the app's colour theme. Only this window's thread reads or sets it.
        private static Color Void, SolidBg, Raise, LineCol, TextCol, Text2, Muted, AccentCol, Accent2, Warm, Good, Bad, OnAccent;
        private static bool _darkPalette;
        private string _themeId;

        /// <summary>The same colour, see-through: panels over the space.</summary>
        private static Color Fade(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Color FromTheme(System.Drawing.Color colour)
        {
            return Color.FromRgb(colour.R, colour.G, colour.B);
        }

        /// <summary>
        /// Spatial's palette comes from the same theme Classic uses, so a colour option only has to
        /// be defined once. The space itself sits a little darker (or lighter) than Classic's rail.
        /// </summary>
        private static void UsePalette(string themeId)
        {
            Theme theme = Theme.ById(themeId);
            _darkPalette = theme.Dark;
            Void = _darkPalette ? Mix(FromTheme(theme.Rail), Colors.Black, 0.4) : Mix(FromTheme(theme.Rail), Colors.White, 0.35);
            SolidBg = FromTheme(theme.Side);
            Raise = FromTheme(theme.Surface);
            TextCol = FromTheme(theme.Text);
            Text2 = FromTheme(theme.Text2);
            Muted = FromTheme(theme.Muted);
            LineCol = Fade(TextCol, _darkPalette ? (byte)0x24 : (byte)0x30);
            AccentCol = FromTheme(theme.Logo);
            Accent2 = FromTheme(theme.Second);
            Warm = FromTheme(theme.Warm);
            Good = FromTheme(theme.Good);
            Bad = FromTheme(theme.Danger);
            OnAccent = FromTheme(theme.OnAccent);
        }

        private static System.Windows.Media.Imaging.BitmapSource _markPixels;
        private ImageSource _mark;

        /// <summary>
        /// The mæla logo in the theme's accent. Only the hue changes, so the bubble keeps its
        /// shading and the bars stay white. In Maela Red it looks like the original.
        /// </summary>
        private static ImageSource ThemedMark(Color accent)
        {
            try
            {
                if (_markPixels == null)
                {
                    using (System.IO.Stream stream = typeof(SpatialWindow).Assembly.GetManifestResourceStream("MaelaMark.png"))
                    {
                        if (stream == null)
                            return null;
                        System.Windows.Media.Imaging.BitmapDecoder decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        System.Windows.Media.Imaging.BitmapSource frame = decoder.Frames[0];
                        // Small enough to recolour instantly, still sharp at twice the size it's shown.
                        double scale = 64.0 / frame.PixelWidth;
                        System.Windows.Media.Imaging.BitmapSource small = new System.Windows.Media.Imaging.TransformedBitmap(frame, new ScaleTransform(scale, scale));
                        _markPixels = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                        _markPixels.Freeze();
                    }
                }
                int width = _markPixels.PixelWidth, height = _markPixels.PixelHeight, stride = width * 4, i;
                byte[] pixels = new byte[stride * height];
                _markPixels.CopyPixels(pixels, stride, 0);
                Branding.Recolour(pixels, System.Drawing.Color.FromArgb(accent.R, accent.G, accent.B));
                System.Windows.Media.Imaging.BitmapSource result = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                result.Freeze();
                return result;
            }
            catch
            {
                return null; // The name still shows without it.
            }
        }

        /// <summary>Repaints everything after the theme changes in Settings.</summary>
        private void ApplyTheme(string themeId)
        {
            _themeId = themeId;
            UsePalette(themeId);
            _mark = ThemedMark(AccentCol);
            if (_mark != null)
                Icon = _mark; // Taskbar and title bar.
            Background = new SolidColorBrush(Void);
            Foreground = new SolidColorBrush(TextCol);
            _stars.SetColours(AccentCol, Accent2);
            _nav.Background = new SolidColorBrush(Fade(SolidBg, 0xE6));
            _nav.BorderBrush = new SolidColorBrush(LineCol);
            _map.Background = new SolidColorBrush(Fade(SolidBg, 0xE0));
            _prompt.Background = new SolidColorBrush(Fade(SolidBg, 0xEE));
            _prompt.BorderBrush = new SolidColorBrush(LineCol);
            _dock.Background = new SolidColorBrush(SolidBg);
            _dock.BorderBrush = new SolidColorBrush(LineCol);
            foreach (TextBox box in new[] { _search, _input, _chatSearch })
            {
                box.Foreground = new SolidColorBrush(TextCol);
                box.CaretBrush = new SolidColorBrush(AccentCol);
            }
            _search.Background = new SolidColorBrush(Raise);
            _chatSearch.Background = new SolidColorBrush(Raise);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int dark = _darkPalette ? 1 : 0;
                try { DwmSetWindowAttribute(handle, 20, ref dark, 4); } catch { }
            }
            if (_world.Effect != null)
                _world.Effect = new BlurEffect { Radius = 8 };
            _lastDock = null;
            RebuildScene(false);
            RefreshAll();
        }
        private static readonly Color[] Palette =
        {
            Color.FromRgb(229, 72, 77), Color.FromRgb(47, 191, 143), Color.FromRgb(240, 178, 50), Color.FromRgb(124, 108, 255),
            Color.FromRgb(228, 92, 164), Color.FromRgb(0, 168, 252), Color.FromRgb(242, 128, 55), Color.FromRgb(26, 188, 156)
        };
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly MainForm _host;
        private SpatialSnapshot _state = new SpatialSnapshot();
        private readonly List<SpatialItem> _rooms = new List<SpatialItem>();
        private readonly List<SpatialItem> _friends = new List<SpatialItem>();
        private readonly List<SpatialNode> _nodes = new List<SpatialNode>();
        private readonly List<KeyValuePair<uint, UIElement>> _sceneRings = new List<KeyValuePair<uint, UIElement>>();
        private readonly List<KeyValuePair<uint, UIElement>> _panelRings = new List<KeyValuePair<uint, UIElement>>();

        private readonly Grid _root = new Grid();
        private readonly SpatialStars _stars = new SpatialStars();
        private readonly Canvas _world = new Canvas();
        private readonly Grid _overlay = new Grid();
        private readonly StackPanel _top = new StackPanel();
        private readonly Border _nav = new Border();
        private readonly StackPanel _navList = new StackPanel();
        private readonly TextBox _search = new TextBox();
        private readonly Border _dock = new Border();
        private readonly Grid _panelHost = new Grid();
        private readonly StackPanel _mapTools = new StackPanel();
        private readonly Border _prompt = new Border();
        private readonly TextBox _input = new TextBox();
        private readonly StackPanel _messages = new StackPanel();
        private ScrollViewer _chatScroll;
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Render);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private bool _roomsTab = true;
        private object _openRoomKey;        // The room being looked at, or null for the whole space.
        private int _openChannel;
        private string _openFriend;
        private bool _pendingFirstFit = true;

        private double _camX, _camY, _camZ, _toX, _toY, _toZ;
        private double _tiltX, _tiltY, _toTiltX, _toTiltY;
        private double _navShift = NavWidth, _toNavShift = NavWidth;
        private readonly double[] _saved = new double[6]; // The camera in each tab: x, y, z, twice.
        private readonly bool[] _visited = { true, false };
        private double _beforeX, _beforeY, _beforeZ;
        private double _lastFrame;
        private double _lastPointerMove = -10;
        private bool _dirty = true;
        private bool _flat;
        private bool _onRefresh;
        private TimeSpan _lastRefreshTime;

        private SpatialNode _hovered; // The room or channel the dots are wrapping.
        private bool _panning, _panMoved;
        private Point _panLast;
        private SpatialNode _pressed;


        public SpatialWindow(MainForm host)
        {
            _host = host;
            _themeId = Theme.Current.Id;
            UsePalette(_themeId);
            _mark = ThemedMark(AccentCol);
            Title = "mæla";
            if (_mark != null)
                Icon = _mark; // Taskbar and title bar, in the theme's colour.
            else
            {
                try
                {
                    using (System.Drawing.Icon appIcon = Branding.LoadAppIcon())
                        Icon = Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
                catch { /* The default icon is fine. */ }
            }
            Width = 1280;
            Height = 800;
            MinWidth = 900;
            MinHeight = 560;
            Background = new SolidColorBrush(Void);
            FontFamily = UiFont;
            Foreground = new SolidColorBrush(TextCol);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BuildChrome();
            _stars.SetColours(AccentCol, Accent2); // The saved theme's colours, not the default cyan.

            SourceInitialized += delegate
            {
                int dark = 1;
                IntPtr handle = new WindowInteropHelper(this).Handle;
                dark = _darkPalette ? 1 : 0;
                try { DwmSetWindowAttribute(handle, 20, ref dark, 4); } catch { }
                _host.SpatialWindowReady(handle);
                ReportPlacement();
            };
            Activated += delegate { ReportState(); };
            Deactivated += delegate { ReportState(); };
            StateChanged += delegate { ReportState(); };
            IsVisibleChanged += delegate { ReportState(); };
            LocationChanged += delegate { ReportPlacement(); };
            SizeChanged += delegate { ReportPlacement(); };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (_closeForGood)
                    return;
                e.Cancel = true;
                _host.SpatialCloseRequested();
            };
            PreviewMouseMove += PointerMoved;
            MouseLeave += delegate
            {
                _stars.Pointer = new Point(-9999, -9999);
                _hovered = null;
            };
            PreviewKeyDown += KeyPressed;
            SizeChanged += delegate { _dirty = true; };

            // Idle ticks come from this timer, about 30 a second. Anything moving draws on the
            // display's own refresh instead (see WantRefresh), so it runs at the monitor's full rate.
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += delegate { if (!_onRefresh) Frame(); };
            _timer.Start();
            Closed += delegate
            {
                _timer.Stop();
                WantRefresh(false);
                _host.SpatialOpenFriend(null);
            };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        private bool _closeForGood;

        public void CloseForGood()
        {
            _closeForGood = true;
            Close();
        }

        private void ReportState()
        {
            SpatialTest.NoteWindowState(IsVisible && WindowState != WindowState.Minimized, IsActive, new WindowInteropHelper(this).Handle);
        }

        private void ReportPlacement()
        {
            if (WindowState == WindowState.Minimized)
                return;
            NativeRect rect;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out rect))
                _host.SpatialWindowMoved(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        // ---- Real data ----

        private string _lastStructure, _lastTalk, _lastDock, _lastChat, _lastFriendChat;

        public void Apply(SpatialSnapshot state)
        {
            _state = state;
            if (state.ThemeId != null && state.ThemeId != _themeId)
                ApplyTheme(state.ThemeId);
            if (state.StructureKey != _lastStructure)
            {
                _lastStructure = state.StructureKey;
                RebuildItems();
                SpatialItem open = OpenRoomItem();
                if (_openRoomKey != null && open == null)
                {
                    // The room went away.
                    _openChannel = 0;
                    GoHome();
                }
                else if (_openChannel != 0 && (open == null || !open.Room.Active || FindChannel(_openChannel) == null))
                {
                    _openChannel = 0;
                    SetFlat(_openFriend != null);
                }
                if (open != null && !_flat)
                {
                    _toX = open.X;
                    _toY = open.Y;
                    _toZ = 120 - open.Z;
                }
                if (_pendingFirstFit)
                {
                    _pendingFirstFit = false;
                    ShowAll();
                    _camX = _toX; _camY = _toY; _camZ = _toZ;
                }
                RebuildScene(false);
                RefreshAll();
            }
            if (state.TalkKey != _lastTalk)
            {
                _lastTalk = state.TalkKey;
                MarkTalking();
            }
            if (state.DockKey != _lastDock)
            {
                _lastDock = state.DockKey;
                RefreshDock();
                RefreshPrompt();
            }
            string chatKey = _openFriend != null ? state.FriendChatKey : state.ChatKey;
            if (_flat && chatKey != (_openFriend != null ? _lastFriendChat : _lastChat))
            {
                if (_openFriend != null)
                    _lastFriendChat = chatKey;
                else
                    _lastChat = chatKey;
                RefreshMessages();
            }
        }

        private SpatialItem OpenRoomItem()
        {
            foreach (SpatialItem item in _rooms)
                if (Object.Equals(item.Room.Key, _openRoomKey)) return item;
            return null;
        }

        private SpatialRoomChannel FindChannel(int id)
        {
            foreach (SpatialRoomChannel channel in _state.Channels)
                if (channel.Id == id) return channel;
            return null;
        }

        private SpatialFriend FindFriend(string id)
        {
            foreach (SpatialFriend friend in _state.Friends)
                if (friend.Id == id) return friend;
            return null;
        }

        private void RebuildItems()
        {
            _rooms.Clear();
            foreach (SpatialRoom room in _state.Rooms)
            {
                SpatialItem item = new SpatialItem();
                item.Room = room;
                item.Name = room.Name;
                item.Color = ColorFor(room.Name);
                _rooms.Add(item);
            }
            _friends.Clear();
            foreach (SpatialFriend friend in _state.Friends)
            {
                SpatialItem item = new SpatialItem();
                item.Friend = friend;
                item.Name = friend.Name;
                item.Color = ColorFor(friend.Name);
                _friends.Add(item);
            }
            LayoutGroups(_rooms, _roomGroups, _state.RoomCircles, 0);
            LayoutGroups(_friends, _friendGroups, _state.FriendCircles, FriendSpace);
        }

        private readonly List<SpatialGroup> _roomGroups = new List<SpatialGroup>();
        private readonly List<SpatialGroup> _friendGroups = new List<SpatialGroup>();

        private List<SpatialGroup> Groups { get { return _roomsTab ? _roomGroups : _friendGroups; } }

        private static void LayoutGroups(List<SpatialItem> items, List<SpatialGroup> groups, List<SpatialCircle> circles, double originX)
        {
            groups.Clear();
            double x = 0, y = 0, rowHeight = 0;
            foreach (SpatialCircle circle in circles)
            {
                string id = circle.Id;
                // Anything pointing at a circle that no longer exists goes in the main one.
                List<SpatialItem> members = items.FindAll(delegate(SpatialItem item)
                {
                    string own = item.Circle;
                    return own == id || (id == SpatialCircle.MainId && !circles.Exists(delegate(SpatialCircle c) { return c.Id == own; }));
                });
                int n = Math.Max(members.Count, 1), i;
                SpatialGroup group = new SpatialGroup();
                group.Id = id;
                group.Title = (circle.Name ?? String.Empty).ToUpperInvariant();
                group.Origin = originX;
                group.Count = members.Count;
                group.Radius = 115 * Math.Sqrt(n) * 1.2 + 110;
                if (circle.HasPosition)
                {
                    group.X = originX + circle.X;
                    group.Y = circle.Y;
                }
                else
                {
                    if (x > 0 && x + 2 * group.Radius > 2900)
                    {
                        x = 0;
                        y += rowHeight + 70;
                        rowHeight = 0;
                    }
                    group.X = originX + x + group.Radius;
                    group.Y = y + group.Radius;
                    x += 2 * group.Radius + 70;
                    rowHeight = Math.Max(rowHeight, 2 * group.Radius);
                }
                group.LabelY = group.Y - group.Radius + 36;
                groups.Add(group);
                for (i = 0; i < members.Count; i++)
                {
                    double angle = i * 2.399963, reach = 115 * Math.Sqrt(i + 0.35);
                    members[i].X = group.X + Math.Cos(angle) * reach * 1.2;
                    members[i].Y = group.Y + 28 + Math.Sin(angle) * reach;
                    members[i].Z = (Hash(members[i].Name) % 200) - 100;
                    members[i].Group = group;
                }
            }
        }

        private static Color ColorFor(string name)
        {
            int hash = 17, i;
            string value = name ?? String.Empty;
            for (i = 0; i < value.Length; i++)
                hash = unchecked(hash * 31 + Char.ToLowerInvariant(value[i]));
            return Palette[(hash & 0x7FFFFFFF) % Palette.Length];
        }

        private static string Initials(string name)
        {
            string[] parts = (name ?? "?").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "?";
            return (parts[0].Substring(0, 1) + (parts.Length > 1 ? parts[1].Substring(0, 1) : String.Empty)).ToUpperInvariant();
        }

        private static int Hash(string text)
        {
            int h = 7, i;
            for (i = 0; i < text.Length; i++)
                h = (h * 31 + text[i]) & 0xFFFF;
            return h;
        }

        private List<SpatialItem> Current { get { return _roomsTab ? _rooms : _friends; } }

        // ---- Camera ----

        private static double ZoomOf(double z) { return Perspective / (Perspective - z); }
        private static double ZFor(double scale) { return Perspective - Perspective / scale; }

        private Size SpaceSize()
        {
            double w = _world.ActualWidth > 0 ? _world.ActualWidth : Width;
            double h = _world.ActualHeight > 0 ? _world.ActualHeight : Height - 72;
            return new Size(Math.Max(100, w - _toNavShift), h);
        }

        private void ShowAll()
        {
            if (Groups.Count == 0)
                return;
            double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            foreach (SpatialGroup group in Groups)
            {
                x0 = Math.Min(x0, group.X - group.Radius); x1 = Math.Max(x1, group.X + group.Radius);
                y0 = Math.Min(y0, group.Y - group.Radius); y1 = Math.Max(y1, group.Y + group.Radius);
            }
            Size space = SpaceSize();
            double s = Math.Min(Math.Min(space.Width / (x1 - x0 + 80), space.Height / (y1 - y0 + 80)), 1);
            _toX = (x0 + x1) / 2;
            _toY = (y0 + y1) / 2;
            _toZ = ZFor(Math.Max(0.12, s));
            _dirty = true;
        }

        private void FocusGroup(SpatialGroup group)
        {
            Size space = SpaceSize();
            double s = Math.Min(Math.Min(space.Width / (2 * group.Radius + 40), space.Height / (2 * group.Radius + 40)), 1);
            _toX = group.X;
            _toY = group.Y;
            _toZ = ZFor(Math.Max(0.2, s));
            _dirty = true;
        }

        private void ZoomBy(double factor, Point? at)
        {
            double s0 = ZoomOf(_toZ), s = Math.Max(0.12, Math.Min(1.5, s0 * factor));
            if (at.HasValue)
            {
                double dx = at.Value.X - CentreX(), dy = at.Value.Y - _world.ActualHeight / 2;
                _toX += dx / s0 - dx / s;
                _toY += dy / s0 - dy / s;
            }
            _toZ = ZFor(s);
            _dirty = true;
        }

        private double CentreX()
        {
            return _navShift + (_world.ActualWidth - _navShift) / 2;
        }

        // ---- The frame loop ----

        private void WantRefresh(bool want)
        {
            if (want == _onRefresh)
                return;
            _onRefresh = want;
            if (want)
                CompositionTarget.Rendering += DisplayRefresh;
            else
                CompositionTarget.Rendering -= DisplayRefresh;
        }

        private void DisplayRefresh(object sender, EventArgs e)
        {
            // This event can fire more than once for one frame; only act on a new one.
            RenderingEventArgs args = e as RenderingEventArgs;
            if (args != null)
            {
                if (args.RenderingTime == _lastRefreshTime)
                    return;
                _lastRefreshTime = args.RenderingTime;
            }
            Frame();
        }

        private void Frame()
        {
            double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(now - _lastFrame, 0.1);
            _lastFrame = now;

            // Not the active window: draw nothing at all.
            if (!IsActive || WindowState == WindowState.Minimized)
            {
                WantRefresh(false);
                return;
            }

            double blend = 1 - Math.Exp(-dt * 6.5);
            bool moving = false;
            moving |= Ease(ref _camX, _toX, blend, 0.2);
            moving |= Ease(ref _camY, _toY, blend, 0.2);
            moving |= Ease(ref _camZ, _toZ, blend, 0.2);
            moving |= Ease(ref _navShift, _toNavShift, blend, 0.2);
            double tiltBlend = 1 - Math.Exp(-dt * 8);
            moving |= Ease(ref _tiltX, _toTiltX, tiltBlend, 0.001);
            moving |= Ease(ref _tiltY, _toTiltY, tiltBlend, 0.001);
            foreach (SpatialNode node in _nodes)
            {
                if (node.Progress < 1)
                {
                    node.Delay -= dt;
                    if (node.Delay <= 0)
                        node.Progress = Math.Min(1, node.Progress + dt / 0.45);
                    moving = true;
                }
            }

            bool pointerActive = now - _lastPointerMove < 1.2;
            if (!_flat)
            {
                _stars.DriftX = _tiltX;
                _stars.DriftY = _tiltY;
                SpatialNode target = _dragTarget ?? _hovered;
                bool onBall = false;
                if (target != null && target.OnScreen)
                {
                    if (target == _dragTarget)
                        onBall = true;
                    else
                    {
                        // Its clickable area includes the name, so check the pointer is on the ball itself.
                        double px = _stars.Pointer.X - target.ScreenX, py = _stars.Pointer.Y - target.ScreenY;
                        onBall = Math.Sqrt(px * px + py * py) <= target.WrapRadius * target.ScreenScale * BallSize(target, now) + 4;
                    }
                }
                _stars.Wrapping = onBall;
                if (_stars.Wrapping)
                {
                    // It moves when the camera does, and grows, so follow it every frame.
                    _stars.WrapTarget = new Point(target.ScreenX, target.ScreenY);
                    _stars.WrapTargetRadius = target.WrapRadius * target.ScreenScale * BallSize(target, now);
                }
                _stars.Step(dt);

                // The targeted ball grows as the point arrives, then breathes; others ease back.
                foreach (SpatialNode node in _nodes)
                {
                    if (node.BallScale == null)
                        continue;
                    double want = onBall && node == target ? _stars.Arrived : 0;
                    node.Grow += (want - node.Grow) * Math.Min(1, dt * 6);
                    if (want == 0 && node.Grow < 0.002)
                        node.Grow = 0;
                    double size = BallSize(node, now);
                    if (Math.Abs(node.BallScale.ScaleX - size) > 0.0005)
                    {
                        node.BallScale.ScaleX = size;
                        node.BallScale.ScaleY = size;
                    }
                    if (node.Grow > 0)
                        moving = true;
                }
            }
            if (moving || _dirty)
            {
                Project();
                _dirty = false;
            }
            // While a ball is wrapped, keep the full frame rate so its effect stays smooth.
            WantRefresh(moving || ((pointerActive || _stars.Wrapping) && !_flat));
        }

        /// <summary>About 8% bigger when fully grown, swelling a little more and back every three seconds.</summary>
        private static double BallSize(SpatialNode node, double now)
        {
            return 1 + node.Grow * (0.08 + 0.025 * Math.Sin(now * 2 * Math.PI / 3));
        }

        /// <summary>
        /// Moves everything drawn before the name into its own layer, scaled about the ball's centre,
        /// so the ball can grow while its name stays put.
        /// </summary>
        private static ScaleTransform SplitBall(Grid box, UIElement name, double centreX, double centreY)
        {
            Grid ball = new Grid();
            ball.Width = box.Width;
            ball.Height = box.Height;
            ball.IsHitTestVisible = false;
            while (box.Children.Count > 0 && box.Children[0] != name)
            {
                UIElement part = box.Children[0];
                box.Children.RemoveAt(0);
                ball.Children.Add(part);
            }
            ScaleTransform scale = new ScaleTransform(1, 1, centreX, centreY);
            ball.RenderTransform = scale;
            box.Children.Insert(0, ball);
            return scale;
        }

        private static bool Ease(ref double value, double target, double blend, double done)
        {
            if (value == target)
                return false;
            value += (target - value) * blend;
            if (Math.Abs(value - target) < done)
                value = target;
            return true;
        }

        private int _lastLod = -1;

        /// <summary>Places every node for the current camera. Offscreen ones are hidden so they cost nothing.</summary>
        private void Project()
        {
            double w = _world.ActualWidth, h = _world.ActualHeight;
            if (w <= 0 || h <= 0)
                return;
            double cx = CentreX(), cy = h / 2;
            foreach (SpatialNode n in _nodes)
            {
                double p = n.Progress <= 0 ? 0 : EaseOutBack(n.Progress);
                double x = n.FromX + (n.X - n.FromX) * p, y = n.FromY + (n.Y - n.FromY) * p, z = n.FromZ + (n.Z - n.FromZ) * p;
                double depth = Perspective - (z + _camZ);
                bool visible = depth > 60 && n.Progress > 0;
                double s = visible ? Perspective / depth : 0;
                double sx = cx + (x - _camX) * s + _tiltX * (z + _camZ) * 0.12;
                double sy = cy + (y - _camY) * s + _tiltY * (z + _camZ) * 0.12;
                double margin = 220 + n.Extent * s;
                if (visible && (sx < -margin || sx > w + margin || sy < -margin || sy > h + margin))
                    visible = false;
                if (visible != n.OnScreen)
                {
                    n.OnScreen = visible;
                    n.Element.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                    if (n.Link != null)
                        n.Link.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                }
                if (!visible)
                    continue;
                double shown = s * Math.Max(0.001, p);
                n.Scale.ScaleX = shown;
                n.Scale.ScaleY = shown;
                n.Move.X = sx - n.AnchorX;
                n.Move.Y = sy - n.AnchorY;
                n.ScreenX = sx;
                n.ScreenY = sy;
                n.ScreenScale = shown;
                if (n.Link != null)
                {
                    double rs = Perspective / (Perspective - (n.FromZ + _camZ));
                    n.Link.X1 = cx + (n.FromX - _camX) * rs;
                    n.Link.Y1 = cy + (n.FromY - _camY) * rs;
                    n.Link.X2 = sx;
                    n.Link.Y2 = sy;
                }
            }
            double scale = ZoomOf(_camZ);
            int lod = _openRoomKey != null ? 0 : (scale < 0.36 ? 2 : (scale < 0.62 ? 1 : 0));
            if (lod != _lastLod)
            {
                _lastLod = lod;
                foreach (TextBlock t in _subLabels)
                    t.Visibility = lod >= 1 ? Visibility.Collapsed : Visibility.Visible;
                foreach (TextBlock t in _mainLabels)
                    t.Visibility = lod >= 2 ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateMap();
        }

        private static double EaseOutBack(double t)
        {
            double u = t - 1;
            return 1 + 2.2 * u * u * u + 1.2 * u * u;
        }

        private readonly List<TextBlock> _subLabels = new List<TextBlock>();
        private readonly List<TextBlock> _mainLabels = new List<TextBlock>();

        // ---- The scene ----

        private SpatialNode AddNode(FrameworkElement element, double anchorX, double anchorY, double x, double y, double z)
        {
            SpatialNode node = new SpatialNode();
            node.Element = element;
            node.AnchorX = anchorX;
            node.AnchorY = anchorY;
            node.X = x; node.Y = y; node.Z = z;
            node.FromX = x; node.FromY = y; node.FromZ = z;
            node.Scale = new ScaleTransform(1, 1, anchorX, anchorY);
            node.Move = new TranslateTransform();
            TransformGroup group = new TransformGroup();
            group.Children.Add(node.Scale);
            group.Children.Add(node.Move);
            element.RenderTransform = group;
            element.CacheMode = new BitmapCache(1);
            element.Tag = node;
            Panel.SetZIndex(element, (int)z);
            _world.Children.Add(element);
            _nodes.Add(node);
            return node;
        }

        private bool _channelsShown;

        private void RebuildScene(bool animate)
        {
            _nodes.Clear();
            _world.Children.Clear();
            _subLabels.Clear();
            _mainLabels.Clear();
            _sceneRings.Clear();
            _hovered = null;
            _roomNode = null;
            _dragTarget = null;
            _lastLod = -1;
            SpatialItem open = OpenRoomItem();
            if (open != null)
            {
                AddItemNode(open);
                _roomNode = _nodes[_nodes.Count - 1];
                bool showChannels = open.Room.Active && _state.Connected;
                if (showChannels)
                    BuildRoom(open, animate || !_channelsShown);
                _channelsShown = showChannels;
            }
            else
            {
                _channelsShown = false;
                foreach (SpatialGroup group in Groups)
                    AddGroupCircle(group);
                foreach (SpatialItem item in Current)
                    AddItemNode(item);
            }
            MarkTalking();
            _dirty = true;
        }

        private void AddGroupCircle(SpatialGroup group)
        {
            double size = 2 * group.Radius;
            Ellipse ring = new Ellipse();
            ring.Width = size;
            ring.Height = size;
            ring.Stroke = new SolidColorBrush(Fade(TextCol, 0x60));
            ring.StrokeThickness = 1.5;
            ring.StrokeDashArray = new DoubleCollection(new double[] { 6, 5 });
            ring.IsHitTestVisible = false;
            SpatialNode circle = AddNode(ring, group.Radius, group.Radius, group.X, group.Y, -140);
            circle.Extent = group.Radius;
            circle.Group = group;
            ring.CacheMode = new BitmapCache(0.75); // It's only a thin outline, and a big one.

            Border label = new Border();
            label.Background = Brushes.Transparent;
            label.Cursor = Cursors.Hand;
            label.ToolTip = "Drag to move this circle. Click to zoom to it. Right-click to rename or delete it.";
            label.Child = Label(group.Title, 30, Text2, true);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SpatialNode title = AddNode(label, label.DesiredSize.Width / 2, label.DesiredSize.Height / 2, group.X, group.LabelY, 0);
            title.Click = delegate { FocusGroup(group); };
            title.RightClick = delegate { _host.SpatialCircleMenu(_roomsTab, group.Id); };
            title.Group = group;
            title.IsGroupLabel = true;

            if (group.Count == 0)
            {
                TextBlock hint = Label("Drag " + (_roomsTab ? "rooms" : "friends") + " here", 15, Text2, false);
                hint.IsHitTestVisible = false;
                hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                SpatialNode text = AddNode(hint, hint.DesiredSize.Width / 2, hint.DesiredSize.Height / 2, group.X, group.Y + 20, 0);
                text.Group = group;
            }
        }

        private void AddItemNode(SpatialItem item)
        {
            bool isRoom = item.Room != null;
            double size = isRoom ? 92 : 78;
            Grid box = new Grid();
            box.Width = 170;
            box.Height = size + 90;
            box.Background = Brushes.Transparent;
            double left = (170 - size) / 2, top = 28;
            bool active = isRoom && item.Room.Active;
            bool lit = isRoom ? (item.Room.Online || active) : (item.Friend.Status != "Offline" && item.Friend.IsFriend);

            // A soft glow, painted rather than computed by an effect so it costs nothing to move.
            Ellipse glow = TopLeft(new Ellipse(), left - 28, top - 28);
            glow.Width = size + 56;
            glow.Height = size + 56;
            RadialGradientBrush glowBrush = new RadialGradientBrush();
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(lit ? (byte)0x80 : (byte)0x10, item.Color.R, item.Color.G, item.Color.B), 0.55));
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, item.Color.R, item.Color.G, item.Color.B), 1));
            glow.Fill = glowBrush;
            box.Children.Add(glow);

            Ellipse orb = TopLeft(new Ellipse(), left, top);
            orb.Width = size;
            orb.Height = size;
            ImageSource picture = isRoom ? item.Room.Picture : null;
            if (picture != null)
            {
                orb.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                orb.Opacity = lit ? 1 : 0.55;
            }
            else
            {
                Color bright = lit ? Mix(item.Color, Colors.White, 0.45) : Mix(item.Color, Color.FromRgb(90, 90, 110), 0.7);
                Color deep = lit ? Mix(item.Color, Color.FromRgb(5, 5, 16), 0.55) : Mix(item.Color, Color.FromRgb(8, 8, 18), 0.85);
                RadialGradientBrush orbBrush = new RadialGradientBrush();
                orbBrush.GradientOrigin = new Point(0.32, 0.26);
                orbBrush.Center = new Point(0.42, 0.38);
                orbBrush.RadiusX = 0.72;
                orbBrush.RadiusY = 0.72;
                orbBrush.GradientStops.Add(new GradientStop(bright, 0));
                orbBrush.GradientStops.Add(new GradientStop(lit ? item.Color : Mix(item.Color, Color.FromRgb(40, 42, 58), 0.8), 0.45));
                orbBrush.GradientStops.Add(new GradientStop(deep, 1));
                orb.Fill = orbBrush;
            }
            box.Children.Add(orb);

            if (active)
            {
                Ellipse ring = TopLeft(new Ellipse(), left - 11, top - 11);
                ring.Width = size + 22;
                ring.Height = size + 22;
                ring.Stroke = new SolidColorBrush(Good);
                ring.StrokeThickness = 1.5;
                box.Children.Add(ring);
            }

            if (picture == null)
            {
                TextBlock initials = TopLeft(Label(isRoom ? Initials(item.Name) : Initials(item.Name).Substring(0, 1), isRoom ? 24 : 22, Colors.White, true), left, top + size / 2 - 16);
                initials.Width = size;
                initials.TextAlignment = TextAlignment.Center;
                box.Children.Add(initials);
            }

            if (!isRoom)
            {
                Ellipse status = TopLeft(new Ellipse(), left + size - 20, top + size - 20);
                status.Width = 17;
                status.Height = 17;
                status.Fill = new SolidColorBrush(StatusColor(item.Friend.Status));
                status.Stroke = new SolidColorBrush(SolidBg);
                status.StrokeThickness = 3;
                box.Children.Add(status);
                if (item.Friend.Unread > 0)
                {
                    Border badge = Badge(item.Friend.Unread);
                    TopLeft(badge, left + size - 18, top - 2);
                    box.Children.Add(badge);
                }
            }

            TextBlock name = Label(item.Name, 15, TextCol, true);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Top;
            name.Margin = new Thickness(0, top + size + 8, 0, 0);
            box.Children.Add(name);
            _mainLabels.Add(name);

            TextBlock sub = Label(SubLine(item), 11.5, Text2, false);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.VerticalAlignment = VerticalAlignment.Top;
            sub.Margin = new Thickness(0, top + size + 28, 0, 0);
            box.Children.Add(sub);
            _subLabels.Add(sub);

            box.Cursor = Cursors.Hand;
            ScaleTransform ballScale = isRoom ? SplitBall(box, name, 85, top + size / 2) : null;
            SpatialNode node = AddNode(box, 85, top + size / 2, item.X, item.Y, item.Z);
            if (isRoom)
            {
                node.WrapRadius = size / 2;
                node.BallScale = ballScale;
            }
            node.Click = delegate
            {
                if (isRoom)
                    OpenRoom(item);
                else
                    OpenFriend(item.Friend.Id);
            };
            node.RightClick = delegate { ItemMenu(item); };
            if (_openRoomKey == null)
            {
                node.Item = item;
                node.Group = item.Group;
            }
        }

        private void ItemMenu(SpatialItem item)
        {
            if (item.Room != null)
                _host.SpatialRoomMenu(item.Room.Key);
            else
                _host.SpatialFriendMenu(item.Friend.Id);
        }

        private static string SubLine(SpatialItem item)
        {
            if (item.Friend != null)
                return item.Friend.Status;
            SpatialRoom room = item.Room;
            if (room.Active)
                return "You are here";
            if (room.Online)
                return "Online" + (room.Max > 0 ? " · " + room.Members + "/" + room.Max : String.Empty);
            if (room.Checking)
                return "Checking…";
            return room.Owned ? "Offline · yours" : "Offline";
        }

        private void BuildRoom(SpatialItem room, bool animate)
        {
            double h = _world.ActualHeight > 0 ? _world.ActualHeight : 700, w = _world.ActualWidth > 0 ? _world.ActualWidth : 1200;
            double r = Math.Min(245, Math.Max(150, h * 0.36));
            double rx = r * Math.Min(1.35, Math.Max(0.62, w / 1050)), ry = r * 0.92;
            int count = _state.Channels.Count, i;
            for (i = 0; i < count; i++)
            {
                SpatialRoomChannel channel = _state.Channels[i];
                double angle = -Math.PI / 4 + i * 2 * Math.PI / count;
                double x = room.X + Math.Cos(angle) * rx, y = room.Y + Math.Sin(angle) * ry, z = room.Z + 30;
                bool mine = channel.Id == _state.LocalChannel;
                Color color = mine ? Good : channel.IsText ? Warm : Accent2;

                Line link = new Line();
                link.Stroke = new SolidColorBrush(Color.FromArgb(0x50, AccentCol.R, AccentCol.G, AccentCol.B));
                link.StrokeThickness = 1;
                link.IsHitTestVisible = false;
                Panel.SetZIndex(link, -5000);
                _world.Children.Add(link);

                Grid box = new Grid();
                box.Width = 170;
                box.Height = 135;
                box.Background = Brushes.Transparent;
                Border shape = new Border();
                shape.Width = 70;
                shape.Height = 70;
                shape.CornerRadius = new CornerRadius(channel.IsText ? 20 : 35);
                shape.BorderThickness = new Thickness(2);
                shape.BorderBrush = new SolidColorBrush(color);
                shape.Background = new SolidColorBrush(Fade(Void, 0xE6));
                shape.HorizontalAlignment = HorizontalAlignment.Center;
                shape.VerticalAlignment = VerticalAlignment.Top;
                shape.Margin = new Thickness(0, 10, 0, 0);
                shape.Child = channel.IsText ? CentredLabel("#", 30, color) : Glyph("", 26, color);
                box.Children.Add(shape);
                TextBlock name = Label(channel.Name, 15, TextCol, true);
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.VerticalAlignment = VerticalAlignment.Top;
                name.Margin = new Thickness(0, 86, 0, 0);
                box.Children.Add(name);
                string line = channel.IsText ? "Chat" : mine ? "Click for chat" : channel.People.Count > 0 ? channel.People.Count + " in voice" : "Click to join";
                TextBlock sub = Label(line, 11.5, Text2, false);
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sub.VerticalAlignment = VerticalAlignment.Top;
                sub.Margin = new Thickness(0, 106, 0, 0);
                box.Children.Add(sub);
                box.Cursor = Cursors.Hand;
                ScaleTransform ballScale = SplitBall(box, name, 85, 45);

                SpatialNode node = AddNode(box, 85, 45, x, y, z);
                node.WrapRadius = 35;
                node.BallScale = ballScale;
                node.ChannelId = channel.Id;
                node.VoiceChannel = !channel.IsText;
                node.Link = link;
                // Its line starts at the room, whether or not it is flying out of it.
                node.FromX = room.X;
                node.FromY = room.Y;
                node.FromZ = room.Z;
                if (animate)
                {
                    node.Progress = 0;
                    node.Delay = i * 0.05;
                }
                int id = channel.Id;
                bool text = channel.IsText;
                node.Click = delegate
                {
                    if (!text && id != _state.LocalChannel)
                        _host.SpatialJoinVoice(id);
                    else
                        OpenChannel(id);
                };
                node.RightClick = delegate { _host.SpatialChannelMenu(id); };

                int j;
                for (j = 0; j < channel.People.Count; j++)
                {
                    double mid = j - (channel.People.Count - 1) / 2.0, dir = Math.Cos(angle) >= 0 ? 1 : -1;
                    SpatialPerson person = channel.People[j];
                    StackPanel label;
                    Grid face = MemberNode(person, out label);
                    ScaleTransform faceScale = SplitBall(face, label, 45, 22);
                    SpatialNode m = AddNode(face, 45, 22, x + dir * (92 - Math.Abs(mid) * 14), y + mid * 66, room.Z + 70);
                    m.WrapRadius = 20;
                    m.BallScale = faceScale;
                    m.Person = person;
                    if (animate)
                    {
                        m.FromX = x; m.FromY = y; m.FromZ = z;
                        m.Progress = 0;
                        m.Delay = 0.25 + j * 0.06;
                    }
                    uint who = person.Id;
                    SpatialNode self = m;
                    m.Click = delegate { RequestProfile(person, self, null); };
                    m.RightClick = delegate { _host.SpatialMemberMenu(who); };
                }
            }
        }

        private Grid MemberNode(SpatialPerson person, out StackPanel label)
        {
            Grid box = new Grid();
            box.Width = 90;
            box.Height = 62;
            box.Background = Brushes.Transparent;
            box.Cursor = Cursors.Hand;
            Ellipse ring = new Ellipse();
            ring.Width = 48;
            ring.Height = 48;
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            ring.VerticalAlignment = VerticalAlignment.Top;
            ring.Margin = new Thickness(0, -2, 0, 0);
            ring.Stroke = new SolidColorBrush(Good);
            ring.StrokeThickness = 2.5;
            ring.Visibility = person.Speaking ? Visibility.Visible : Visibility.Hidden;
            box.Children.Add(ring);
            _sceneRings.Add(new KeyValuePair<uint, UIElement>(person.Id, ring));
            Border avatar = Avatar(person.Name, 40, null, person.Picture);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            avatar.VerticalAlignment = VerticalAlignment.Top;
            avatar.Margin = new Thickness(0, 2, 0, 0);
            box.Children.Add(avatar);
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.VerticalAlignment = VerticalAlignment.Top;
            line.Margin = new Thickness(0, 44, 0, 0);
            line.Children.Add(Label(person.Name, 11.5, Text2, false));
            if (person.Live)
                line.Children.Add(LiveTag());
            if (person.Muted || person.Deafened)
                line.Children.Add(MuteMark());
            box.Children.Add(line);
            label = line;
            return box;
        }

        private static TextBlock MuteMark()
        {
            TextBlock mark = Glyph("", 11, Bad);
            mark.Margin = new Thickness(4, 1, 0, 0);
            return mark;
        }

        private void MarkTalking()
        {
            HashSet<uint> talking = new HashSet<uint>();
            foreach (SpatialRoomChannel channel in _state.Channels)
                foreach (SpatialPerson person in channel.People)
                    if (person.Speaking) talking.Add(person.Id);
            foreach (KeyValuePair<uint, UIElement> ring in _sceneRings)
                ring.Value.Visibility = talking.Contains(ring.Key) ? Visibility.Visible : Visibility.Hidden;
            foreach (KeyValuePair<uint, UIElement> ring in _panelRings)
                ring.Value.Visibility = talking.Contains(ring.Key) ? Visibility.Visible : Visibility.Hidden;
        }

        private static Border LiveTag()
        {
            Border tag = new Border();
            tag.Background = new SolidColorBrush(Bad);
            tag.CornerRadius = new CornerRadius(3);
            tag.Padding = new Thickness(4, 0, 4, 0);
            tag.Margin = new Thickness(4, 1, 0, 1);
            tag.Child = Label("LIVE", 9.5, Colors.White, true);
            return tag;
        }

        // ---- Navigation ----

        private void SetFlat(bool flat)
        {
            if (_flat == flat)
                return;
            _flat = flat;
            _world.Opacity = flat ? 0.14 : 1;
            // WPF cannot blur what sits behind a panel, so the space itself is blurred while it holds still.
            _world.Effect = flat ? new BlurEffect { Radius = 8 } : null;
            _stars.Pointer = new Point(-9999, -9999);
            _hovered = null;
            _stars.InvalidateVisual();
            _dirty = true;
        }

        private void SwitchTab(bool rooms)
        {
            if (_roomsTab == rooms)
                return;
            int from = _roomsTab ? 0 : 3, to = rooms ? 0 : 3;
            _saved[from] = _toX; _saved[from + 1] = _toY; _saved[from + 2] = _toZ;
            _roomsTab = rooms;
            _search.Text = String.Empty;
            RebuildScene(false);
            if (_visited[rooms ? 0 : 1])
            {
                _toX = _saved[to]; _toY = _saved[to + 1]; _toZ = _saved[to + 2];
            }
            else
                ShowAll();
            _visited[rooms ? 0 : 1] = true;
            RefreshAll();
        }

        private void OpenRoom(SpatialItem room)
        {
            if (_openRoomKey == null)
            {
                _beforeX = _toX; _beforeY = _toY; _beforeZ = _toZ;
            }
            _openRoomKey = room.Room.Key;
            _openChannel = 0;
            _toNavShift = 0;
            _toX = room.X;
            _toY = room.Y;
            _toZ = 120 - room.Z;
            RebuildScene(true);
            RefreshAll();
        }

        private void GoHome()
        {
            SetFlat(false);
            bool wasInRoom = _openRoomKey != null;
            _openRoomKey = null;
            _openChannel = 0;
            if (_openFriend != null)
            {
                _openFriend = null;
                _host.SpatialOpenFriend(null);
            }
            _toNavShift = NavWidth;
            if (wasInRoom)
            {
                _toX = _beforeX; _toY = _beforeY; _toZ = _beforeZ;
            }
            RebuildScene(false);
            RefreshAll();
        }

        private void OpenChannel(int id)
        {
            _openChannel = id;
            _lastChat = null;
            _chatSearch.Clear();
            _host.SpatialViewChannel(id);
            SetFlat(true);
            RefreshAll();
        }

        private void OpenFriend(string id)
        {
            _openFriend = id;
            _lastFriendChat = null;
            _chatSearch.Clear();
            _openChannel = 0;
            _host.SpatialOpenFriend(id);
            SetFlat(true);
            RefreshAll();
        }

        private void GoBack()
        {
            if (_openFriend != null)
            {
                _openFriend = null;
                _host.SpatialOpenFriend(null);
                SetFlat(_openChannel != 0);
                RebuildScene(false);
                RefreshAll();
            }
            else if (_openChannel != 0)
            {
                _openChannel = 0;
                SetFlat(false);
                RebuildScene(false);
                RefreshAll();
            }
            else if (_openRoomKey != null)
                GoHome();
        }

        private int FriendsUnread()
        {
            int total = 0;
            foreach (SpatialFriend friend in _state.Friends)
                total += friend.Unread;
            return total;
        }

        // ---- Input ----

        private void PointerMoved(object sender, MouseEventArgs e)
        {
            Point at = e.GetPosition(_root);
            _lastPointerMove = _clock.Elapsed.TotalSeconds;
            if (_flat)
                return;
            _stars.Pointer = at;
            SpatialNode over = NodeFrom(e.OriginalSource as DependencyObject);
            _hovered = over != null && over.WrapRadius > 0 ? over : null;
            if (!_panning && _card == null && ActualWidth > 0)
            {
                _toTiltX = at.X / ActualWidth - 0.5;
                _toTiltY = at.Y / ActualHeight - 0.5;
            }
            if (IsActive)
                WantRefresh(true);
        }

        private void KeyPressed(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_card != null)
                {
                    CloseCard();
                    e.Handled = true;
                    return;
                }
                GoBack();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (_openRoomKey != null || _openFriend != null)
                    GoHome();
                _search.Focus();
                e.Handled = true;
                return;
            }
            if (_flat || _openRoomKey != null || Keyboard.FocusedElement is TextBox)
                return;
            double step = 90 / ZoomOf(_toZ);
            if (e.Key == Key.Left) { _toX -= step; e.Handled = true; }
            else if (e.Key == Key.Right) { _toX += step; e.Handled = true; }
            else if (e.Key == Key.Up) { _toY -= step; e.Handled = true; }
            else if (e.Key == Key.Down) { _toY += step; e.Handled = true; }
            else if (e.Key == Key.Home) { ShowAll(); e.Handled = true; }
            _dirty = true;
        }

        private void SpaceDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = NodeFrom(e.OriginalSource as DependencyObject);
            // Hosts can move anyone; everyone can move themselves.
            _draggingPerson = _pressed != null && _pressed.Person != null && (_state.IsHost || _pressed.Person.IsLocal);
            bool dragsSomething = _pressed != null && (_pressed.Item != null || _pressed.IsGroupLabel || _draggingPerson);
            _panning = _openRoomKey == null && !dragsSomething;
            _panMoved = false;
            _panLast = e.GetPosition(_world);
            _world.CaptureMouse();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void SpaceMove(object sender, MouseEventArgs e)
        {
            if (!_world.IsMouseCaptured)
                return;
            Point at = e.GetPosition(_world);
            double dx = at.X - _panLast.X, dy = at.Y - _panLast.Y;
            if (!_panMoved && dx * dx + dy * dy < 36)
                return;
            _panMoved = true;
            _panLast = at;
            double s = ZoomOf(_camZ);
            if (_pressed != null && _draggingPerson)
            {
                _pressed.X += dx / s; _pressed.FromX = _pressed.X;
                _pressed.Y += dy / s; _pressed.FromY = _pressed.Y;
                Panel.SetZIndex(_pressed.Element, 9000);
                _dragTarget = DropTargetFor(_pressed);
                _dirty = true;
                return;
            }
            if (_pressed != null && _pressed.Item != null)
            {
                _pressed.X += dx / s; _pressed.FromX = _pressed.X;
                _pressed.Y += dy / s; _pressed.FromY = _pressed.Y;
                Panel.SetZIndex(_pressed.Element, 9000);
                _dirty = true;
                return;
            }
            if (_pressed != null && _pressed.IsGroupLabel)
            {
                SpatialGroup group = _pressed.Group;
                group.X += dx / s;
                group.Y += dy / s;
                foreach (SpatialNode node in _nodes)
                {
                    if (node.Group == group)
                    {
                        node.X += dx / s; node.FromX = node.X;
                        node.Y += dy / s; node.FromY = node.Y;
                    }
                }
                _dirty = true;
                return;
            }
            if (!_panning)
                return;
            _camX -= dx / s; _toX = _camX;
            _camY -= dy / s; _toY = _camY;
            _dirty = true;
        }

        private void SpaceUp(object sender, MouseButtonEventArgs e)
        {
            if (!_world.IsMouseCaptured)
                return;
            _world.ReleaseMouseCapture();
            _panning = false;
            SpatialNode pressed = _pressed;
            _pressed = null;
            if (!_panMoved)
            {
                if (pressed != null && pressed.Click != null)
                    pressed.Click();
                return;
            }
            if (pressed == null)
                return;
            if (pressed.IsGroupLabel)
                _host.SpatialPlaceCircle(_roomsTab, pressed.Group.Id, pressed.Group.X - pressed.Group.Origin, pressed.Group.Y);
            else if (pressed.Item != null)
                DropItem(pressed.Item, pressed.X, pressed.Y);
            else if (pressed.Person != null && _draggingPerson)
                DropPerson(pressed.Person);
            _draggingPerson = false;
        }

        /// <summary>A voice channel they aren't in, or the room's ball (out of voice), under the dragged person.</summary>
        private SpatialNode DropTargetFor(SpatialNode dragged)
        {
            SpatialNode best = null;
            double bestDistance = 1e12;
            foreach (SpatialNode node in _nodes)
            {
                bool channel = node.VoiceChannel && node.ChannelId != dragged.Person.ChannelId;
                bool outOfVoice = node == _roomNode && dragged.Person.ChannelId > 0;
                if (!channel && !outOfVoice)
                    continue;
                double reach = node.WrapRadius * 1.5;
                double distance = Math.Sqrt((node.X - dragged.X) * (node.X - dragged.X) + (node.Y - dragged.Y) * (node.Y - dragged.Y));
                if (distance < reach && distance < bestDistance)
                {
                    best = node;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>Into the channel they were dropped on, or out of voice on the room's ball. Anywhere else, they go back.</summary>
        private void DropPerson(SpatialPerson person)
        {
            SpatialNode target = _dragTarget;
            _dragTarget = null;
            if (target != null)
            {
                int channel = target == _roomNode ? 0 : target.ChannelId;
                if (person.IsLocal)
                {
                    if (channel > 0)
                        _host.SpatialJoinVoice(channel);
                    else
                        _host.SpatialLeaveVoice();
                }
                else
                    _host.SpatialMovePerson(person.Id, channel);
            }
            // Back to where they were until the room confirms the move.
            RebuildScene(false);
            _dirty = true;
        }

        /// <summary>Dropped inside another circle, it moves there. Anywhere else, it goes back.</summary>
        private void DropItem(SpatialItem item, double x, double y)
        {
            SpatialGroup target = GroupAt(x, y);
            if (target != null && target.Id != item.Circle)
                MoveItemTo(item, target);
            else
            {
                RebuildScene(false);
                _dirty = true;
            }
        }

        private SpatialGroup GroupAt(double x, double y)
        {
            SpatialGroup best = null;
            double bestDistance = 1e12;
            foreach (SpatialGroup group in Groups)
            {
                double distance = Math.Sqrt((group.X - x) * (group.X - x) + (group.Y - y) * (group.Y - y));
                if (distance < group.Radius && distance < bestDistance)
                {
                    best = group;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private void MoveItemTo(SpatialItem item, SpatialGroup group)
        {
            // Shown straight away; the saved state arrives with the next update.
            item.Circle = group.Id;
            if (item.Room != null)
                _host.SpatialMoveRoomToCircle(item.Room.Key, group.Id);
            else
                _host.SpatialMoveFriendToCircle(item.Friend.Id, group.Id);
            LayoutGroups(_rooms, _roomGroups, _state.RoomCircles, 0);
            LayoutGroups(_friends, _friendGroups, _state.FriendCircles, FriendSpace);
            RebuildScene(false);
            RefreshAll();
        }

        private double _lastWheel;
        private int _wheelPush; // Scrolling past the limit, towards entering or leaving a room.

        private void SpaceWheel(object sender, MouseWheelEventArgs e)
        {
            if (_flat)
                return;
            e.Handled = true;
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastWheel > 0.6)
                _wheelPush = 0;
            _lastWheel = now;
            if (_openRoomKey != null)
            {
                // Zooming out of a room goes back to all of them. Two notches, so a stray one doesn't.
                if (e.Delta < 0 && (_wheelPush += -e.Delta) >= 240)
                {
                    _wheelPush = 0;
                    GoHome();
                }
                else if (e.Delta > 0)
                    _wheelPush = 0;
                return;
            }
            Point at = e.GetPosition(_world);
            bool zoomedIn = ZoomOf(_toZ) >= 1.49;
            ZoomBy(Math.Exp(e.Delta * 0.0012), at);
            if (e.Delta <= 0 || !zoomedIn || !_roomsTab)
            {
                _wheelPush = 0;
                return;
            }
            // Already as close as it goes: one more notch towards a room flies into it.
            SpatialItem room = RoomNear(at);
            if (room != null && (_wheelPush += e.Delta) >= 120)
            {
                _wheelPush = 0;
                OpenRoom(room);
            }
        }

        /// <summary>The room nearest a point on screen, if it's close enough to mean that one.</summary>
        private SpatialItem RoomNear(Point at)
        {
            double s = ZoomOf(_camZ);
            double x = _camX + (at.X - CentreX()) / s, y = _camY + (at.Y - _world.ActualHeight / 2) / s;
            SpatialItem best = null;
            double bestDistance = 220;
            foreach (SpatialItem room in _rooms)
            {
                double distance = Math.Sqrt((room.X - x) * (room.X - x) + (room.Y - y) * (room.Y - y));
                if (distance < bestDistance)
                {
                    best = room;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static SpatialNode NodeFrom(DependencyObject source)
        {
            while (source != null)
            {
                FrameworkElement element = source as FrameworkElement;
                if (element != null && element.Tag is SpatialNode)
                    return (SpatialNode)element.Tag;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        // ---- Fixed interface ----

        private void BuildChrome()
        {
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = _root;

            Grid.SetRowSpan(_stars, 2);
            _root.Children.Add(_stars);

            _world.Background = Brushes.Transparent;
            _world.ClipToBounds = true;
            _world.MouseLeftButtonDown += SpaceDown;
            _world.MouseMove += SpaceMove;
            _world.MouseLeftButtonUp += SpaceUp;
            _world.MouseWheel += SpaceWheel;
            _world.AllowDrop = true;
            _world.Drop += delegate(object s, DragEventArgs e)
            {
                SpatialItem item = e.Data.GetData(typeof(SpatialItem)) as SpatialItem;
                if (item == null)
                    return;
                Point at = e.GetPosition(_world);
                double scale = ZoomOf(_camZ);
                SpatialGroup target = GroupAt(_camX + (at.X - CentreX()) / scale, _camY + (at.Y - _world.ActualHeight / 2) / scale);
                if (target != null && target.Id != item.Circle)
                    MoveItemTo(item, target);
            };
            _world.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                SpatialNode node = NodeFrom(e.OriginalSource as DependencyObject);
                if (node != null && node.RightClick != null)
                {
                    node.RightClick();
                    e.Handled = true;
                }
            };
            _root.Children.Add(_world);

            _root.Children.Add(_overlay);
            _top.Orientation = Orientation.Horizontal;
            _top.VerticalAlignment = VerticalAlignment.Top;
            _top.HorizontalAlignment = HorizontalAlignment.Left;
            _top.Margin = new Thickness(16, 12, 0, 0);
            _overlay.Children.Add(_top);

            // The list of rooms or friends.
            _nav.Width = 254;
            _nav.HorizontalAlignment = HorizontalAlignment.Left;
            _nav.Margin = new Thickness(14, 62, 0, 12);
            _nav.Background = new SolidColorBrush(Fade(SolidBg, 0xE6));
            _nav.BorderBrush = new SolidColorBrush(LineCol);
            _nav.BorderThickness = new Thickness(1);
            _nav.CornerRadius = new CornerRadius(14);
            DockPanel navLayout = new DockPanel();
            _search.Margin = new Thickness(10);
            _search.Padding = new Thickness(8, 5, 8, 5);
            _search.Background = new SolidColorBrush(Raise);
            _search.Foreground = new SolidColorBrush(TextCol);
            _search.CaretBrush = new SolidColorBrush(AccentCol);
            _search.BorderThickness = new Thickness(0);
            _search.TextChanged += delegate { RefreshNav(); };
            _search.ToolTip = "Find (Ctrl+K)";
            DockPanel.SetDock(_search, Dock.Top);
            navLayout.Children.Add(_search);
            _navFoot.Orientation = Orientation.Horizontal;
            _navFoot.HorizontalAlignment = HorizontalAlignment.Center;
            _navFoot.Margin = new Thickness(8);
            DockPanel.SetDock(_navFoot, Dock.Bottom);
            navLayout.Children.Add(_navFoot);
            ScrollViewer navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            navScroll.Content = _navList;
            navLayout.Children.Add(navScroll);
            _nav.Child = navLayout;
            _overlay.Children.Add(_nav);

            // Zoom and map, bottom right.
            _mapTools.HorizontalAlignment = HorizontalAlignment.Right;
            _mapTools.VerticalAlignment = VerticalAlignment.Bottom;
            _mapTools.Margin = new Thickness(0, 0, 14, 12);
            StackPanel zoom = new StackPanel();
            zoom.Orientation = Orientation.Horizontal;
            zoom.HorizontalAlignment = HorizontalAlignment.Right;
            zoom.Margin = new Thickness(0, 0, 0, 6);
            zoom.Children.Add(Chip("−", delegate { ZoomBy(1 / 1.3, null); }));
            zoom.Children.Add(Chip("+", delegate { ZoomBy(1.3, null); }));
            zoom.Children.Add(Chip("Show all", ShowAll));
            zoom.Children.Add(Chip("Map", delegate
            {
                // Hidden until asked for, so it doesn't sit over the space.
                _map.Visibility = _map.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                UpdateMap();
            }));
            _mapTools.Children.Add(zoom);
            _map.Visibility = Visibility.Collapsed;
            _map.Width = 178;
            _map.Height = 112;
            _map.Background = new SolidColorBrush(Fade(SolidBg, 0xE0));
            _map.ClipToBounds = true;
            _map.Cursor = Cursors.Hand;
            _map.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                Point at = e.GetPosition(_map);
                _toX = _mapX0 + (at.X - _mapOx) / _mapK;
                _toY = _mapY0 + (at.Y - _mapOy) / _mapK;
                _dirty = true;
            };
            _mapTools.Children.Add(_map);
            _overlay.Children.Add(_mapTools);

            // Join or host, shown under a room you aren't in.
            _prompt.HorizontalAlignment = HorizontalAlignment.Center;
            _prompt.VerticalAlignment = VerticalAlignment.Bottom;
            _prompt.Margin = new Thickness(0, 0, 0, 24);
            _prompt.Width = 330;
            _prompt.Padding = new Thickness(16, 14, 16, 14);
            _prompt.CornerRadius = new CornerRadius(16);
            _prompt.Background = new SolidColorBrush(Fade(SolidBg, 0xEE));
            _prompt.BorderBrush = new SolidColorBrush(LineCol);
            _prompt.BorderThickness = new Thickness(1);
            _overlay.Children.Add(_prompt);

            _overlay.Children.Add(_panelHost);
            _overlay.Children.Add(_cardLayer);

            Grid.SetRow(_dock, 1);
            _dock.Background = new SolidColorBrush(SolidBg);
            _dock.BorderBrush = new SolidColorBrush(LineCol);
            _dock.BorderThickness = new Thickness(0, 1, 0, 0);
            _dock.Padding = new Thickness(14, 8, 14, 8);
            _dock.MinHeight = 72;
            _root.Children.Add(_dock);

            _input.Background = Brushes.Transparent;
            _input.BorderThickness = new Thickness(0);
            _input.Foreground = new SolidColorBrush(TextCol);
            _input.CaretBrush = new SolidColorBrush(AccentCol);
            _input.MaxLength = VoipEngine.MaxChatChars;
            _input.KeyDown += InputKeyDown;
            _chatSearch.TextChanged += delegate { RefreshMessages(); };
            _messages.Margin = new Thickness(18, 12, 18, 12);

            RefreshAll();
        }

        private readonly WrapPanel _navFoot = new WrapPanel();
        private readonly Canvas _map = new Canvas();
        private double _mapX0, _mapY0, _mapK = 1, _mapOx, _mapOy;
        private readonly Rectangle _mapView = new Rectangle();
        private bool _mapBuilt;

        private void BuildMap()
        {
            _map.Children.Clear();
            _mapBuilt = false;
            if (Groups.Count == 0)
                return;
            double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            foreach (SpatialGroup group in Groups)
            {
                x0 = Math.Min(x0, group.X - group.Radius); x1 = Math.Max(x1, group.X + group.Radius);
                y0 = Math.Min(y0, group.Y - group.Radius); y1 = Math.Max(y1, group.Y + group.Radius);
            }
            x0 -= 30; x1 += 30; y0 -= 30; y1 += 30;
            _mapK = Math.Min(178 / (x1 - x0), 112 / (y1 - y0));
            _mapX0 = x0; _mapY0 = y0;
            _mapOx = (178 - (x1 - x0) * _mapK) / 2;
            _mapOy = (112 - (y1 - y0) * _mapK) / 2;
            foreach (SpatialGroup group in Groups)
            {
                Ellipse outline = new Ellipse();
                outline.Width = outline.Height = 2 * group.Radius * _mapK;
                outline.Stroke = new SolidColorBrush(Fade(TextCol, 0x70));
                outline.StrokeThickness = 1;
                Canvas.SetLeft(outline, _mapOx + (group.X - group.Radius - x0) * _mapK);
                Canvas.SetTop(outline, _mapOy + (group.Y - group.Radius - y0) * _mapK);
                _map.Children.Add(outline);
            }
            foreach (SpatialItem item in Current)
            {
                Ellipse dot = new Ellipse();
                dot.Width = 4.4;
                dot.Height = 4.4;
                dot.Fill = new SolidColorBrush(item.Room != null ? (item.Room.Online ? item.Color : Color.FromRgb(0x55, 0x5C, 0x70)) : StatusColor(item.Friend.Status));
                Canvas.SetLeft(dot, _mapOx + (item.X - x0) * _mapK - 2.2);
                Canvas.SetTop(dot, _mapOy + (item.Y - y0) * _mapK - 2.2);
                _map.Children.Add(dot);
            }
            _mapView.Stroke = new SolidColorBrush(Fade(TextCol, 0xCC));
            _mapView.StrokeThickness = 1;
            _map.Children.Add(_mapView);
            _mapBuilt = true;
        }

        // ----- Profile cards. The card grows out from behind the person's avatar, which stays put
        // and becomes the card's picture. From a side list, it opens beside the row. -----

        private const double CardWidth = 300;
        private const double CardPad = 12;
        private readonly Canvas _cardLayer = new Canvas();
        private Border _card;
        private uint _cardFor;
        private SpatialNode _cardNode;
        private FrameworkElement _cardRow;
        private FrameworkElement _cardHides; // Their avatar and name in the space, hidden while the card shows them.
        private SpatialNode _roomNode;   // The room's ball, in a room: drop someone on it to take them out of voice.
        private SpatialNode _dragTarget; // The channel or ball under someone being dragged.
        private bool _draggingPerson;

        private void RequestProfile(SpatialPerson person, SpatialNode node, FrameworkElement row)
        {
            _cardFor = person.Id;
            _cardNode = node;
            _cardRow = row;
            _host.SpatialProfile(person.Id);
        }

        public void ShowProfile(SpatialProfile profile)
        {
            if (profile.Id != _cardFor)
                return;
            RemoveCard();
            double width = _overlay.ActualWidth, height = _overlay.ActualHeight;
            Point centre;
            double radius;
            if (_cardNode != null && _cardNode.OnScreen)
            {
                centre = new Point(_cardNode.ScreenX, _cardNode.ScreenY);
                radius = _cardNode.WrapRadius * _cardNode.ScreenScale * BallSize(_cardNode, _clock.Elapsed.TotalSeconds);
            }
            else if (_cardRow != null && _cardRow.IsVisible)
            {
                radius = 30;
                Point side = _cardRow.TranslatePoint(new Point(_cardRow.ActualWidth, _cardRow.ActualHeight / 2), _overlay);
                centre = new Point(side.X + 12 + CardPad + radius, side.Y);
            }
            else
                return;

            Border card = new Border();
            card.Width = CardWidth;
            card.Background = new SolidColorBrush(SolidBg);
            card.BorderBrush = new SolidColorBrush(LineCol);
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(16);
            card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };

            FrameworkElement header = CardHeader(profile, radius);
            FrameworkElement body = CardBody(profile);
            StackPanel stack = new StackPanel();
            card.Child = stack;
            stack.Children.Add(header);
            stack.Children.Add(body);
            card.Measure(new Size(CardWidth, double.PositiveInfinity));
            double cardHeight = card.DesiredSize.Height;

            // The avatar sits in the card's top corner; with no room below, it sits in the bottom one.
            bool flipY = centre.Y - radius - CardPad + cardHeight > height - 8;
            if (flipY)
            {
                stack.Children.Clear();
                stack.Children.Add(body);
                stack.Children.Add(CardHeader(profile, radius));
            }
            // Always opens to the right, picture first. Near the right edge it slides left to stay on screen.
            double left = Math.Max(8, Math.Min(centre.X - radius - CardPad, width - CardWidth - 8));
            double top = flipY ? centre.Y + radius + CardPad - cardHeight : centre.Y - radius - CardPad;
            top = Math.Max(8, top);

            // A click anywhere else closes it.
            Rectangle catcher = new Rectangle();
            catcher.Width = width;
            catcher.Height = height;
            catcher.Fill = Brushes.Transparent;
            catcher.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; CloseCard(); };
            catcher.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; CloseCard(); };
            _cardLayer.Children.Add(catcher);

            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            card.RenderTransformOrigin = new Point((centre.X - left) / CardWidth, (centre.Y - top) / cardHeight);
            ScaleTransform grow = new ScaleTransform(0.82, 0.82);
            card.RenderTransform = grow;
            card.Opacity = 0;
            _cardLayer.Children.Add(card);
            _card = card;
            if (_cardNode != null && _cardNode.OnScreen)
            {
                _cardHides = _cardNode.Element;
                _cardHides.Opacity = 0;
            }

            // Grows out of the avatar.
            Duration time = new Duration(TimeSpan.FromMilliseconds(170));
            QuadraticEase ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
        }

        private void CloseCard()
        {
            Border card = _card;
            if (card == null)
                return;
            _card = null;
            _cardFor = 0;
            ScaleTransform grow = card.RenderTransform as ScaleTransform;
            Duration time = new Duration(TimeSpan.FromMilliseconds(120));
            DoubleAnimation fade = new DoubleAnimation(0, time);
            fade.Completed += delegate
            {
                if (_card != null)
                    return;
                _cardLayer.Children.Clear();
                ShowHidden();
            };
            if (grow != null)
            {
                grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, time));
                grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, time));
            }
            card.BeginAnimation(OpacityProperty, fade);
        }

        private void RemoveCard()
        {
            _cardLayer.Children.Clear();
            _card = null;
            ShowHidden();
        }

        private void ShowHidden()
        {
            if (_cardHides != null)
                _cardHides.Opacity = 1;
            _cardHides = null;
        }

        /// <summary>The picture in the corner, exactly where the avatar is, with the name beside it.</summary>
        private FrameworkElement CardHeader(SpatialProfile profile, double radius)
        {
            double size = 2 * radius;
            Color tint = ColorFor(profile.Name);
            Grid header = new Grid();
            header.MinHeight = size + 2 * CardPad;
            header.Background = new LinearGradientBrush(Fade(tint, 0x99), Fade(tint, 0x00), 90);

            Border avatar = Avatar(profile.Name, size, null, profile.Picture);
            avatar.HorizontalAlignment = HorizontalAlignment.Left;
            avatar.VerticalAlignment = VerticalAlignment.Top;
            avatar.Margin = new Thickness(CardPad, CardPad, CardPad, CardPad);
            header.Children.Add(avatar);

            StackPanel words = new StackPanel();
            words.VerticalAlignment = VerticalAlignment.Center;
            words.Margin = new Thickness(size + 2 * CardPad, CardPad, CardPad + 4, CardPad);
            StackPanel nameLine = new StackPanel();
            nameLine.Orientation = Orientation.Horizontal;
            TextBlock name = Label(profile.Name, 20, TextCol, true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = CardWidth - size - 3 * CardPad - 30;
            nameLine.Children.Add(name);
            if (profile.IsHost)
            {
                TextBlock crown = Label("♛", 16, Color.FromRgb(0xF0, 0xB2, 0x32), false);
                crown.Margin = new Thickness(6, 3, 0, 0);
                crown.ToolTip = "Host";
                nameLine.Children.Add(crown);
            }
            words.Children.Add(nameLine);
            if (!String.IsNullOrEmpty(profile.Status))
            {
                TextBlock status = Label(profile.Status, 13, Text2, false);
                status.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(status);
            }
            words.Children.Add(Label(profile.State, 12, Muted, false));
            header.Children.Add(words);
            return header;
        }

        private FrameworkElement CardBody(SpatialProfile profile)
        {
            StackPanel body = new StackPanel();
            body.Margin = new Thickness(CardPad, 2, CardPad, CardPad);
            if (profile.IsLocal)
            {
                body.Children.Add(CardCaption("Appear as"));
                WrapPanel choices = new WrapPanel();
                foreach (string choice in new[] { "Online", "Away", "Busy", "Offline" })
                {
                    string picked = choice;
                    Border option = Chip(choice, delegate
                    {
                        _host.SpatialSetMyStatus(picked);
                        CloseCard();
                    });
                    option.Margin = new Thickness(0, 0, 6, 6);
                    if (choice == profile.MyStatus)
                    {
                        option.Background = new SolidColorBrush(Fade(AccentCol, 0x40));
                        option.BorderBrush = new SolidColorBrush(AccentCol);
                    }
                    choices.Children.Add(option);
                }
                body.Children.Add(choices);
                body.Children.Add(CardButton("Edit profile", AccentCol, OnAccent, delegate { CloseCard(); _host.SpatialEditProfile(); }));
                return body;
            }

            uint id = profile.Id;
            if (profile.LiveText != null)
            {
                if (profile.CanWatch)
                    body.Children.Add(CardButton(profile.LiveText, Bad, Colors.White, delegate { CloseCard(); _host.SpatialWatch(id); }));
                else
                {
                    TextBlock note = Label("They're live. Join their voice channel to watch.", 12, Muted, false);
                    note.TextWrapping = TextWrapping.Wrap;
                    note.Margin = new Thickness(0, 0, 0, 8);
                    body.Children.Add(note);
                }
            }

            DockPanel volumeLine = new DockPanel();
            TextBlock readout = Label(profile.Volume + "%", 12, Muted, false);
            DockPanel.SetDock(readout, Dock.Right);
            volumeLine.Children.Add(readout);
            volumeLine.Children.Add(CardCaption("Volume (just for you)"));
            body.Children.Add(volumeLine);
            body.Children.Add(VolumeSlider(profile.Volume, readout, delegate(int value) { _host.SpatialSetPeerVolume(id, value); }));

            bool muted = profile.MutedForMe;
            Border mute = null;
            mute = Chip(muted ? "✓  Muted for you" : "Mute for me", delegate
            {
                muted = !muted;
                ((TextBlock)mute.Child).Text = muted ? "✓  Muted for you" : "Mute for me";
                mute.BorderBrush = new SolidColorBrush(muted ? Bad : LineCol);
                _host.SpatialSetPeerMuted(id, muted);
            });
            mute.BorderBrush = new SolidColorBrush(muted ? Bad : LineCol);
            mute.HorizontalAlignment = HorizontalAlignment.Left;
            mute.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(mute);

            if (profile.ViewerIsHost)
            {
                TextBlock host = Label("HOST", 11, Muted, true);
                host.Margin = new Thickness(0, 14, 0, 4);
                body.Children.Add(host);
                body.Children.Add(CardCaption("Move to"));
                WrapPanel channels = new WrapPanel();
                foreach (SpatialRoomChannel channel in profile.VoiceChannels)
                {
                    int target = channel.Id;
                    bool here = channel.Id == profile.ChannelId;
                    Border move = Chip(channel.Name, delegate { if (!here) { CloseCard(); _host.SpatialMovePerson(id, target); } });
                    move.Margin = new Thickness(0, 0, 6, 6);
                    if (here)
                    {
                        move.Opacity = 0.45;
                        move.Cursor = Cursors.Arrow;
                        move.ToolTip = "They're here";
                    }
                    channels.Children.Add(move);
                }
                if (profile.ChannelId > 0)
                {
                    Border outOfVoice = Chip("Remove from voice", delegate { CloseCard(); _host.SpatialMovePerson(id, 0); });
                    outOfVoice.Margin = new Thickness(0, 0, 6, 6);
                    channels.Children.Add(outOfVoice);
                }
                body.Children.Add(channels);

                Grid actions = new Grid();
                actions.Margin = new Thickness(0, 6, 0, 0);
                actions.ColumnDefinitions.Add(new ColumnDefinition());
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                actions.ColumnDefinitions.Add(new ColumnDefinition());
                FrameworkElement kick = ConfirmButton("Kick", "Click again to kick", Raise, TextCol, delegate { CloseCard(); _host.SpatialKick(id, false); });
                FrameworkElement ban = ConfirmButton("Ban", "Click again to ban", Bad, Colors.White, delegate { CloseCard(); _host.SpatialKick(id, true); });
                Grid.SetColumn(ban, 2);
                actions.Children.Add(kick);
                actions.Children.Add(ban);
                body.Children.Add(actions);
            }
            return body;
        }

        private static TextBlock CardCaption(string text)
        {
            TextBlock caption = Label(text, 12, Text2, true);
            caption.Margin = new Thickness(0, 4, 0, 6);
            return caption;
        }

        private static Border CardButton(string text, Color colour, Color ink, Action click)
        {
            Border button = Clickable(click);
            button.Background = new SolidColorBrush(colour);
            button.CornerRadius = new CornerRadius(9);
            button.Padding = new Thickness(12, 9, 12, 9);
            button.Margin = new Thickness(0, 4, 0, 4);
            TextBlock label = Label(text, 13.5, ink, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            button.Child = label;
            return button;
        }

        /// <summary>Asks for a second click before acting, so kick and ban can't happen by accident.</summary>
        private static Border ConfirmButton(string text, string confirm, Color colour, Color ink, Action act)
        {
            bool armed = false;
            Border button = null;
            button = CardButton(text, colour, ink, delegate
            {
                if (armed)
                {
                    act();
                    return;
                }
                armed = true;
                ((TextBlock)button.Child).Text = confirm;
            });
            button.MouseLeave += delegate
            {
                armed = false;
                ((TextBlock)button.Child).Text = text;
            };
            return button;
        }

        /// <summary>0 to 200%, drawn in the theme. Changes go straight to the app as you drag.</summary>
        private static FrameworkElement VolumeSlider(int value, TextBlock readout, Action<int> changed)
        {
            Grid track = new Grid();
            track.Height = 24;
            track.Background = Brushes.Transparent;
            track.Cursor = Cursors.Hand;
            Border rail = new Border();
            rail.Height = 4;
            rail.CornerRadius = new CornerRadius(2);
            rail.Background = new SolidColorBrush(Raise);
            rail.VerticalAlignment = VerticalAlignment.Center;
            track.Children.Add(rail);
            Border fill = new Border();
            fill.Height = 4;
            fill.CornerRadius = new CornerRadius(2);
            fill.Background = new SolidColorBrush(AccentCol);
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.VerticalAlignment = VerticalAlignment.Center;
            track.Children.Add(fill);
            Ellipse knob = new Ellipse();
            knob.Width = 14;
            knob.Height = 14;
            knob.Fill = new SolidColorBrush(TextCol);
            knob.HorizontalAlignment = HorizontalAlignment.Left;
            knob.VerticalAlignment = VerticalAlignment.Center;
            track.Children.Add(knob);
            int current = Math.Max(0, Math.Min(200, value));
            Action place = delegate
            {
                double width = track.ActualWidth;
                if (width <= 0)
                    return;
                double share = current / 200.0;
                fill.Width = width * share;
                knob.Margin = new Thickness(Math.Max(0, Math.Min(width - 14, width * share - 7)), 0, 0, 0);
                readout.Text = current + "%";
            };
            Action<MouseEventArgs> set = delegate(MouseEventArgs e)
            {
                double width = track.ActualWidth;
                if (width <= 0)
                    return;
                int next = (int)Math.Round(Math.Max(0, Math.Min(1, e.GetPosition(track).X / width)) * 200);
                if (Math.Abs(next - 100) <= 4)
                    next = 100; // Easy to land back on normal.
                if (next == current)
                    return;
                current = next;
                place();
                changed(current);
            };
            track.SizeChanged += delegate { place(); };
            track.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { track.CaptureMouse(); set(e); e.Handled = true; };
            track.MouseMove += delegate(object s, MouseEventArgs e) { if (track.IsMouseCaptured) set(e); };
            track.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { track.ReleaseMouseCapture(); e.Handled = true; };
            return track;
        }

        // ----- Right-click menus, drawn here in the theme. -----

        private readonly System.Windows.Controls.Primitives.Popup _menu = new System.Windows.Controls.Primitives.Popup();

        public void ShowMenu(List<SpatialMenuEntry> entries)
        {
            _menu.IsOpen = false;
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.StaysOpen = false; // A click anywhere else closes it.
            _menu.AllowsTransparency = true;
            Border frame = new Border();
            frame.Background = new SolidColorBrush(SolidBg);
            frame.BorderBrush = new SolidColorBrush(LineCol);
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(10);
            frame.Padding = new Thickness(5);
            frame.MinWidth = 200;
            frame.Focusable = true;
            frame.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                    _menu.IsOpen = false;
            };
            FillMenu(frame, entries, null);
            _menu.Child = frame;
            _menu.IsOpen = true;
            frame.Focus();
        }

        /// <summary>A submenu replaces the list, with a way back, so nothing opens off screen.</summary>
        private void FillMenu(Border frame, List<SpatialMenuEntry> entries, List<SpatialMenuEntry> parent)
        {
            StackPanel list = new StackPanel();
            if (parent != null)
                list.Children.Add(MenuRow("‹  Back", true, false, null, false, delegate { FillMenu(frame, parent, null); }));
            int i;
            for (i = 0; i < entries.Count; i++)
            {
                SpatialMenuEntry entry = entries[i];
                if (entry.Separator)
                {
                    Border line = new Border();
                    line.Height = 1;
                    line.Margin = new Thickness(6, 4, 6, 4);
                    line.Background = new SolidColorBrush(LineCol);
                    list.Children.Add(line);
                    continue;
                }
                if (!entry.Enabled && entry.Children == null && i == 0)
                {
                    // Classic menus open with their subject as a greyed-out heading.
                    TextBlock heading = Label(entry.Text, 12, Muted, true);
                    heading.Margin = new Thickness(9, 4, 9, 6);
                    heading.TextTrimming = TextTrimming.CharacterEllipsis;
                    list.Children.Add(heading);
                    continue;
                }
                int id = entry.Id;
                List<SpatialMenuEntry> children = entry.Children;
                Action click = children != null
                    ? (Action)delegate { FillMenu(frame, children, entries); }
                    : delegate
                    {
                        _menu.IsOpen = false;
                        _host.SpatialMenuClicked(id);
                    };
                list.Children.Add(MenuRow(entry.Text, entry.Enabled, entry.Checked, entry.Picture, children != null, click));
            }
            frame.Child = list;
        }

        private static FrameworkElement MenuRow(string text, bool enabled, bool ticked, ImageSource picture, bool more, Action click)
        {
            DockPanel line = new DockPanel();
            TextBlock tick = Label(ticked ? "✓" : String.Empty, 13, AccentCol, true);
            tick.Width = 18;
            DockPanel.SetDock(tick, Dock.Left);
            line.Children.Add(tick);
            if (picture != null)
            {
                Image image = new Image();
                image.Source = picture;
                image.Width = 16;
                image.Height = 16;
                image.Margin = new Thickness(0, 0, 8, 0);
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(image, Dock.Left);
                line.Children.Add(image);
            }
            if (more)
            {
                TextBlock arrow = Label("›", 14, Muted, false);
                arrow.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(arrow, Dock.Right);
                line.Children.Add(arrow);
            }
            line.Children.Add(Label(text, 13, enabled ? TextCol : Muted, false));
            if (!enabled)
            {
                Border still = new Border();
                still.Padding = new Thickness(6, 6, 10, 6);
                still.Child = line;
                return still;
            }
            Border row = Clickable(click);
            row.Padding = new Thickness(6, 6, 10, 6);
            row.CornerRadius = new CornerRadius(7);
            row.MouseEnter += delegate { row.Background = new SolidColorBrush(Raise); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.Child = line;
            return row;
        }

        private void UpdateMap()
        {
            if (!_mapBuilt || _mapTools.Visibility != Visibility.Visible || _map.Visibility != Visibility.Visible)
                return;
            double s = ZoomOf(_camZ), vw = (_world.ActualWidth - _navShift) / s * _mapK, vh = _world.ActualHeight / s * _mapK;
            _mapView.Width = Math.Max(2, vw);
            _mapView.Height = Math.Max(2, vh);
            Canvas.SetLeft(_mapView, _mapOx + (_camX - _mapX0) * _mapK - vw / 2);
            Canvas.SetTop(_mapView, _mapOy + (_camY - _mapY0) * _mapK - vh / 2);
        }

        private void RefreshAll()
        {
            RefreshTop();
            RefreshNav();
            RefreshPrompt();
            RefreshPanel();
            RefreshDock();
            bool home = _openRoomKey == null && !_flat;
            _nav.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            _mapTools.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            _top.Visibility = _flat ? Visibility.Collapsed : Visibility.Visible;
            if (home)
                BuildMap();
            _toNavShift = home ? NavWidth : 0;
            _dirty = true;
        }

        private void RefreshTop()
        {
            _top.Children.Clear();
            if (_mark != null)
            {
                Image logo = new Image();
                logo.Source = _mark;
                logo.Width = 30;
                logo.Height = 30;
                logo.Margin = new Thickness(0, 0, 8, 0);
                logo.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
                _top.Children.Add(logo);
            }
            TextBlock brand = Label("mæla", 25, TextCol, true);
            brand.VerticalAlignment = VerticalAlignment.Center;
            brand.Margin = new Thickness(0, 0, 14, 0);
            _top.Children.Add(brand);
            SpatialItem open = OpenRoomItem();
            if (open != null)
            {
                _top.Children.Add(Chip("← All rooms", GoHome));
                TextBlock here = Label(open.Name, 16, TextCol, true);
                here.VerticalAlignment = VerticalAlignment.Center;
                here.Margin = new Thickness(10, 0, 10, 0);
                _top.Children.Add(here);
                AddRoomChips(_top, open);
                return;
            }
            _top.Children.Add(Tab("Rooms", _rooms.Count.ToString(), 0, _roomsTab, delegate { SwitchTab(true); }));
            int on = 0;
            foreach (SpatialFriend friend in _state.Friends)
                if (friend.IsFriend && friend.Status != "Offline") on++;
            _top.Children.Add(Tab("Friends", on + " on", FriendsUnread(), !_roomsTab, delegate { SwitchTab(false); }));
        }

        private void AddRoomChips(Panel into, SpatialItem room)
        {
            object key = room.Room.Key;
            if (room.Room.Active && _state.Connected)
            {
                into.Children.Add(Chip("Copy room code", _host.SpatialCopyRoomCode));
                if (_state.IsHost)
                    into.Children.Add(Chip("+ Channel", _host.SpatialAddChannel));
            }
            if (key is SavedRoom)
                into.Children.Add(Chip("Room options", delegate { _host.SpatialRoomMenu(key); }));
        }

        private void RefreshNav()
        {
            _navList.Children.Clear();
            string find = _search.Text.Trim();
            int shown = 0;
            foreach (SpatialGroup group in Groups)
            {
                SpatialGroup target2 = group;
                List<SpatialItem> members = Current.FindAll(delegate(SpatialItem m) { return m.Group == target2 && m.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0; });
                if (find.Length > 0 && members.Count == 0)
                    continue;
                Border head = Clickable(delegate { FocusGroup(target2); });
                head.Padding = new Thickness(13, 6, 13, 4);
                head.AllowDrop = true;
                head.Drop += delegate(object s, DragEventArgs e)
                {
                    SpatialItem dropped = e.Data.GetData(typeof(SpatialItem)) as SpatialItem;
                    if (dropped != null && dropped.Circle != target2.Id)
                        MoveItemTo(dropped, target2);
                };
                head.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    _host.SpatialCircleMenu(_roomsTab, target2.Id);
                };
                DockPanel headLine = new DockPanel();
                TextBlock count = Label(members.Count.ToString(), 11, Muted, false);
                DockPanel.SetDock(count, Dock.Right);
                headLine.Children.Add(count);
                headLine.Children.Add(Label(group.Title, 11, Muted, true));
                head.Child = headLine;
                _navList.Children.Add(head);
                shown += members.Count;
                AddNavRows(members);
            }
            if (shown == 0 && find.Length > 0)
            {
                TextBlock note = Label("Nothing matches.", 12.5, Muted, false);
                note.Margin = new Thickness(16);
                _navList.Children.Add(note);
            }
            _navFoot.Children.Clear();
            bool roomsTab = _roomsTab;
            _navFoot.Children.Add(Chip("+ New circle", delegate { _host.SpatialNewCircle(roomsTab); }));
            if (_roomsTab)
            {
                _navFoot.Children.Add(Chip("Host a room", delegate { _host.SpatialAddRoom(true); }));
                _navFoot.Children.Add(Chip("Join with a code", delegate { _host.SpatialAddRoom(false); }));
            }
            else
            {
                _navFoot.Children.Add(Chip("Add a friend", _host.SpatialAddFriend));
                _navFoot.Children.Add(Chip("My code", _host.SpatialCopyFriendCode));
                _navFoot.Children.Add(Chip("Backup", _host.SpatialBackupMenu));
            }
            foreach (UIElement chip in _navFoot.Children)
                ((FrameworkElement)chip).Margin = new Thickness(0, 0, 4, 4);
        }

        private void AddNavRows(List<SpatialItem> members)
        {
            foreach (SpatialItem item in members)
            {
                SpatialItem target = item;
                Border row = Clickable(delegate
                {
                    if (target.Room != null)
                        OpenRoom(target);
                    else
                        OpenFriend(target.Friend.Id);
                });
                row.Margin = new Thickness(6, 0, 6, 0);
                row.Padding = new Thickness(7, 4, 7, 4);
                row.CornerRadius = new CornerRadius(8);
                row.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    ItemMenu(target);
                };
                DockPanel line = new DockPanel();
                FrameworkElement lead;
                if (item.Room != null)
                {
                    Border tile = new Border();
                    tile.Width = 28;
                    tile.Height = 28;
                    tile.CornerRadius = new CornerRadius(9);
                    if (item.Room.Picture != null)
                        tile.Background = new ImageBrush(item.Room.Picture) { Stretch = Stretch.UniformToFill };
                    else
                    {
                        tile.Background = new SolidColorBrush(item.Room.Online ? item.Color : Mix(item.Color, Color.FromRgb(40, 42, 58), 0.75));
                        tile.Child = CentredLabel(Initials(item.Name), 12, Colors.White);
                    }
                    lead = tile;
                }
                else
                    lead = Avatar(item.Name, 28, item.Friend.Status, null);
                lead.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(lead, Dock.Left);
                line.Children.Add(lead);
                Point? pressedAt = null;
                row.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { pressedAt = e.GetPosition(row); };
                row.MouseLeave += delegate { pressedAt = null; };
                row.PreviewMouseMove += delegate(object s, MouseEventArgs e)
                {
                    if (pressedAt == null || e.LeftButton != MouseButtonState.Pressed)
                        return;
                    Vector moved = e.GetPosition(row) - pressedAt.Value;
                    if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6)
                        return;
                    pressedAt = null;
                    DataObject data = new DataObject(typeof(SpatialItem), target);
                    DragDrop.DoDragDrop(row, data, DragDropEffects.Move);
                };
                if (item.Friend != null && item.Friend.Unread > 0)
                {
                    Border badge = Badge(item.Friend.Unread);
                    badge.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(badge, Dock.Right);
                    line.Children.Add(badge);
                }
                StackPanel words = new StackPanel();
                words.Children.Add(Label(item.Name, 13, item.Room != null && item.Room.Active ? Good : TextCol, false));
                words.Children.Add(Label(SubLine(item), 11, Muted, false));
                line.Children.Add(words);
                row.Child = line;
                _navList.Children.Add(row);
            }
        }

        private void RefreshPrompt()
        {
            SpatialItem open = OpenRoomItem();
            bool show = open != null && !_flat && !(open.Room.Active && _state.Connected);
            _prompt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                return;
            StackPanel body = new StackPanel();
            TextBlock title = Label(open.Name, 21, TextCol, true);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(title);
            string line;
            if (open.Room.Active)
                line = String.IsNullOrEmpty(_state.Message) ? "Connecting…" : _state.Message;
            else if (open.Room.Online)
                line = SubLine(open);
            else
                line = open.Room.Owned ? "Offline. Host it to open the room." : "Offline. The host needs to open it.";
            TextBlock detail = Label(line, 13, Text2, false);
            detail.HorizontalAlignment = HorizontalAlignment.Center;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextAlignment = TextAlignment.Center;
            detail.Margin = new Thickness(0, 2, 0, 10);
            body.Children.Add(detail);
            if (open.Room.Active)
            {
                Border cancel = Primary("Cancel", Bad, _host.SpatialDisconnect);
                body.Children.Add(cancel);
            }
            else
            {
                object key = open.Room.Key;
                body.Children.Add(Primary(open.Room.Owned ? "Host room" : "Join room", AccentCol, delegate { _host.SpatialEnterRoom(key); }));
                if (_state.Running)
                {
                    TextBlock warn = Label("This leaves " + _state.RoomName + ". You can only be in one room at a time.", 12, Muted, false);
                    warn.TextWrapping = TextWrapping.Wrap;
                    warn.TextAlignment = TextAlignment.Center;
                    warn.Margin = new Thickness(0, 8, 0, 0);
                    body.Children.Add(warn);
                }
            }
            _prompt.Child = body;
        }

        private static Border Primary(string text, Color color, Action click)
        {
            Border button = Clickable(click);
            button.Background = new SolidColorBrush(color);
            button.CornerRadius = new CornerRadius(9);
            button.Padding = new Thickness(14, 7, 14, 7);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Child = Label(text, 13.5, color == AccentCol ? OnAccent : Colors.White, true);
            return button;
        }

        private void RefreshPanel()
        {
            bool inputFocused = _input.IsKeyboardFocused;
            string text = _input.Text;
            Decorator holder = _input.Parent as Decorator;
            if (holder != null)
                holder.Child = null;
            ScrollViewer oldScroll = _messages.Parent as ScrollViewer;
            if (oldScroll != null)
                oldScroll.Content = null;
            _panelHost.Children.Clear();
            _panelRings.Clear();
            if (!_flat)
                return;
            SpatialFriend friend = _openFriend != null ? FindFriend(_openFriend) : null;
            SpatialRoomChannel channel = _openChannel != 0 ? FindChannel(_openChannel) : null;
            if (friend == null && channel == null)
                return;

            Border panel = new Border();
            panel.Margin = new Thickness(14, 14, 14, 12);
            panel.Background = new SolidColorBrush(Fade(SolidBg, 0xF0));
            panel.BorderBrush = new SolidColorBrush(LineCol);
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(18);
            Grid layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            bool voice = friend == null && !channel.IsText;
            if (voice)
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            panel.Child = layout;

            layout.Children.Add(BuildRail());
            FrameworkElement main = BuildChat(friend, channel);
            Grid.SetColumn(main, 1);
            layout.Children.Add(main);
            if (voice)
            {
                StackPanel side = new StackPanel();
                side.Margin = new Thickness(10, 14, 10, 10);
                side.Children.Add(Label("IN VOICE · " + channel.People.Count, 11, Muted, true));
                foreach (SpatialPerson person in channel.People)
                    side.Children.Add(PersonRow(person, true));
                Grid.SetColumn(side, 2);
                layout.Children.Add(side);
            }
            _panelHost.Children.Add(panel);
            RefreshMessages();
            _input.Text = text;
            if (inputFocused)
                _input.Focus();
            MarkTalking();
        }

        private FrameworkElement PersonRow(SpatialPerson person, bool roomy)
        {
            uint who = person.Id;
            Border row = null;
            row = Clickable(delegate { RequestProfile(person, null, row); });
            row.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _host.SpatialMemberMenu(who);
            };
            row.Padding = new Thickness(roomy ? 4 : 30, 3, 4, 3);
            row.CornerRadius = new CornerRadius(7);
            row.Margin = new Thickness(roomy ? 0 : 8, roomy ? 5 : 0, roomy ? 0 : 8, 0);
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            Grid face = new Grid();
            double size = roomy ? 28 : 20;
            face.Width = size + 6;
            face.Height = size + 6;
            Ellipse ring = new Ellipse();
            ring.Stroke = new SolidColorBrush(Good);
            ring.StrokeThickness = 2;
            ring.Visibility = person.Speaking ? Visibility.Visible : Visibility.Hidden;
            face.Children.Add(ring);
            _panelRings.Add(new KeyValuePair<uint, UIElement>(person.Id, ring));
            Border avatar = Avatar(person.Name, size, null, person.Picture);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            avatar.VerticalAlignment = VerticalAlignment.Center;
            face.Children.Add(avatar);
            line.Children.Add(face);
            TextBlock name = Label(person.Name, roomy ? 13 : 12.5, TextCol, false);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(name);
            if (person.Live)
                line.Children.Add(LiveTag());
            if (person.Muted || person.Deafened)
                line.Children.Add(MuteMark());
            row.Child = line;
            return row;
        }

        private FrameworkElement BuildRail()
        {
            DockPanel rail = new DockPanel();
            Grid banner = new Grid();
            banner.Height = 74;
            SpatialItem room = OpenRoomItem();
            Color tint = _openFriend != null ? ColorFor(_openFriend) : room != null ? room.Color : Accent2;
            banner.Background = new LinearGradientBrush(tint, Mix(Accent2, Color.FromRgb(8, 8, 18), 0.45), 20);
            Border back = Clickable(GoBack);
            back.Width = 30;
            back.Height = 30;
            back.CornerRadius = new CornerRadius(15);
            back.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.VerticalAlignment = VerticalAlignment.Top;
            back.Margin = new Thickness(10);
            back.Child = Glyph("", 13, Colors.White);
            banner.Children.Add(back);
            DockPanel.SetDock(banner, Dock.Top);
            rail.Children.Add(banner);

            TextBlock title = Label(_openFriend != null ? "Friends" : room != null ? room.Name : _state.RoomName, 20, TextCol, true);
            title.Margin = new Thickness(14, 10, 14, 8);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(title, Dock.Top);
            rail.Children.Add(title);

            WrapPanel tools = new WrapPanel();
            tools.Margin = new Thickness(12, 0, 8, 8);
            if (_openFriend != null)
            {
                tools.Children.Add(Chip("Add a friend", _host.SpatialAddFriend));
                tools.Children.Add(Chip("My code", _host.SpatialCopyFriendCode));
            }
            else if (room != null)
                AddRoomChips(tools, room);
            foreach (UIElement chip in tools.Children)
                ((FrameworkElement)chip).Margin = new Thickness(0, 0, 4, 4);
            DockPanel.SetDock(tools, Dock.Top);
            rail.Children.Add(tools);

            StackPanel list = new StackPanel();
            if (_openFriend != null)
            {
                foreach (SpatialFriend friend in _state.Friends)
                {
                    string id = friend.Id;
                    Border row = RailRow(Avatar(friend.Name, 26, friend.Status, null), friend.Name, friend.Id == _openFriend, friend.Unread, delegate { OpenFriend(id); });
                    row.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                    {
                        e.Handled = true;
                        _host.SpatialFriendMenu(id);
                    };
                    list.Children.Add(row);
                }
            }
            else
            {
                foreach (SpatialRoomChannel channel in _state.Channels)
                {
                    int id = channel.Id;
                    bool mine = id == _state.LocalChannel;
                    FrameworkElement lead = channel.IsText ? CentredLabel("#", 15, Text2) : Glyph("", 14, mine ? Good : Text2);
                    lead.Width = 26;
                    Border row = RailRow(lead, channel.Name, id == _openChannel, 0, delegate { OpenChannel(id); });
                    row.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                    {
                        e.Handled = true;
                        _host.SpatialChannelMenu(id);
                    };
                    list.Children.Add(row);
                    foreach (SpatialPerson person in channel.People)
                        list.Children.Add(PersonRow(person, false));
                }
            }
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            scroll.Content = list;
            rail.Children.Add(scroll);
            return rail;
        }

        private static Border RailRow(FrameworkElement lead, string name, bool selected, int unread, Action click)
        {
            Border row = Clickable(click);
            row.Margin = new Thickness(8, 1, 8, 1);
            row.Padding = new Thickness(7, 5, 7, 5);
            row.CornerRadius = new CornerRadius(8);
            if (selected)
                row.Background = new SolidColorBrush(Color.FromArgb(0x30, AccentCol.R, AccentCol.G, AccentCol.B));
            DockPanel line = new DockPanel();
            lead.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(lead, Dock.Left);
            line.Children.Add(lead);
            if (unread > 0)
            {
                Border badge = Badge(unread);
                DockPanel.SetDock(badge, Dock.Right);
                line.Children.Add(badge);
            }
            TextBlock text = Label(name, 13, selected ? TextCol : Text2, false);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(text);
            row.Child = line;
            return row;
        }

        private FrameworkElement BuildChat(SpatialFriend friend, SpatialRoomChannel channel)
        {
            DockPanel main = new DockPanel();
            string title = friend != null ? friend.Name : channel.Name;
            string subtitle = friend != null ? friend.Status : channel.IsText ? "Chat-only channel" : channel.Id == _state.LocalChannel ? channel.People.Count + " in voice" : "Voice channel";
            Border head = new Border();
            head.Padding = new Thickness(18, 13, 18, 13);
            head.BorderBrush = new SolidColorBrush(LineCol);
            head.BorderThickness = new Thickness(0, 0, 0, 1);
            DockPanel headLine = new DockPanel();
            if (friend != null && friend.WantsToBeFriends)
            {
                string id = friend.Id;
                Border decline = Primary("Decline and block", Bad, delegate { _host.SpatialBlockFriend(id); });
                decline.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(decline, Dock.Right);
                headLine.Children.Add(decline);
                Border accept = Primary("Accept", AccentCol, delegate { _host.SpatialAcceptFriend(id); });
                DockPanel.SetDock(accept, Dock.Right);
                headLine.Children.Add(accept);
            }
            else if (friend != null && friend.IsFriend)
            {
                string id = friend.Id;
                Border call = Primary("Call", AccentCol, delegate { _host.SpatialCallFriend(id); });
                DockPanel.SetDock(call, Dock.Right);
                headLine.Children.Add(call);
            }
            else if (channel != null && !channel.IsText && channel.Id != _state.LocalChannel)
            {
                int id = channel.Id;
                Border join = Primary("Join voice", AccentCol, delegate { _host.SpatialJoinVoice(id); });
                DockPanel.SetDock(join, Dock.Right);
                headLine.Children.Add(join);
            }
            _chatSearch.Width = 150;
            _chatSearch.Margin = new Thickness(0, 0, 8, 0);
            _chatSearch.Padding = new Thickness(8, 4, 8, 4);
            _chatSearch.VerticalAlignment = VerticalAlignment.Center;
            _chatSearch.Background = new SolidColorBrush(Raise);
            _chatSearch.Foreground = new SolidColorBrush(TextCol);
            _chatSearch.CaretBrush = new SolidColorBrush(AccentCol);
            _chatSearch.BorderThickness = new Thickness(0);
            _chatSearch.ToolTip = "Search this chat";
            DockPanel oldSearchHolder = _chatSearch.Parent as DockPanel;
            if (oldSearchHolder != null)
                oldSearchHolder.Children.Remove(_chatSearch);
            DockPanel.SetDock(_chatSearch, Dock.Right);
            headLine.Children.Add(_chatSearch);
            StackPanel words = new StackPanel();
            words.Orientation = Orientation.Horizontal;
            words.Children.Add(Label(title, 22, TextCol, true));
            TextBlock sub = Label(subtitle, 12.5, Muted, false);
            sub.VerticalAlignment = VerticalAlignment.Center;
            sub.Margin = new Thickness(10, 0, 0, 0);
            words.Children.Add(sub);
            headLine.Children.Add(words);
            head.Child = headLine;
            DockPanel.SetDock(head, Dock.Top);
            main.Children.Add(head);

            _chatScroll = new ScrollViewer();
            _chatScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _chatScroll.Content = _messages;

            Border compose = new Border();
            compose.Margin = new Thickness(18, 0, 18, 15);
            compose.Background = new SolidColorBrush(Raise);
            compose.CornerRadius = new CornerRadius(12);
            compose.Padding = new Thickness(14, 8, 8, 8);
            _input.ToolTip = "Message " + title;
            _input.VerticalAlignment = VerticalAlignment.Center;
            compose.Child = _input;
            // Emoji and send sit in a row with the box.
            Grid composeRow = new Grid();
            composeRow.Margin = compose.Margin;
            compose.Margin = new Thickness(0);
            composeRow.ColumnDefinitions.Add(new ColumnDefinition());
            composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composeRow.Children.Add(compose);
            Border emoji = DockButton("", false, false, "Emoji", delegate
            {
                _host.SpatialPickEmoji(delegate(string text)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        int caret = _input.CaretIndex;
                        _input.Text = _input.Text.Insert(caret, text);
                        _input.CaretIndex = caret + text.Length;
                        _input.Focus();
                    });
                });
            });
            emoji.Margin = new Thickness(6, 0, 2, 0);
            Grid.SetColumn(emoji, 1);
            composeRow.Children.Add(emoji);
            Border send = DockButton("", false, false, "Send", SendInput);
            Grid.SetColumn(send, 2);
            composeRow.Children.Add(send);
            DockPanel.SetDock(composeRow, Dock.Bottom);
            main.Children.Add(composeRow);
            main.Children.Add(_chatScroll);
            return main;
        }

        private readonly TextBox _chatSearch = new TextBox();

        private void SendInput()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0)
                return;
            if (_openFriend != null)
                _host.SpatialSendFriend(_openFriend, text);
            else if (_openChannel != 0)
                _host.SpatialSendRoomChat(_openChannel, text);
            _input.Clear();
        }

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            SendInput();
            e.Handled = true;
        }

        private void RefreshMessages()
        {
            if (_chatScroll == null)
                return;
            bool atBottom = _chatScroll.ScrollableHeight - _chatScroll.VerticalOffset < 24;
            _messages.Children.Clear();
            List<SpatialLine> lines = _openFriend != null
                ? (_state.FriendChatId == _openFriend ? _state.FriendChat : null)
                : (_state.ChatChannel == _openChannel ? _state.Chat : null);
            if (lines == null)
                return;
            if (lines.Count == 0)
            {
                TextBlock empty = Label("No messages yet.", 13, Muted, false);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 30, 0, 0);
                _messages.Children.Add(empty);
            }
            string find = _chatSearch.Text.Trim();
            foreach (SpatialLine line in lines)
            {
                if (find.Length == 0 || (line.Text ?? String.Empty).IndexOf(find, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    _messages.Children.Add(MessageRow(line));
            }
            if (atBottom)
                _chatScroll.ScrollToEnd();
        }

        private FrameworkElement MessageRow(SpatialLine message)
        {
            DockPanel row = new DockPanel();
            row.Margin = new Thickness(0, 6, 0, 6);
            Border avatar = Avatar(message.Who, 36, null, message.Picture);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            avatar.Margin = new Thickness(0, 0, 11, 0);
            DockPanel.SetDock(avatar, Dock.Left);
            row.Children.Add(avatar);
            StackPanel words = new StackPanel();
            StackPanel headLine = new StackPanel();
            headLine.Orientation = Orientation.Horizontal;
            headLine.Children.Add(Label(message.Who, 14, ColorFor(message.Who), true));
            TextBlock time = Label(message.TimeUtc.ToLocalTime().ToShortTimeString() + (message.Note != null ? " · " + message.Note : String.Empty), 11.5, Muted, false);
            time.Margin = new Thickness(7, 2, 0, 0);
            headLine.Children.Add(time);
            words.Children.Add(headLine);
            TextBlock body = Label(message.Text, 14, TextCol, false);
            body.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(body);
            string friendId = _openFriend;
            ulong key = message.ReactKey;
            if (message.Reactions.Count > 0)
            {
                WrapPanel chips = new WrapPanel();
                chips.Margin = new Thickness(0, 4, 0, 0);
                foreach (SpatialReaction reaction in message.Reactions)
                    chips.Children.Add(ReactionChip(reaction, friendId, key));
                words.Children.Add(chips);
            }
            row.Children.Add(words);
            if (key != 0)
            {
                row.Background = Brushes.Transparent;
                row.ToolTip = "Right-click to react";
                row.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    _host.SpatialReactMenu(friendId, key);
                };
            }
            return row;
        }

        private Border ReactionChip(SpatialReaction reaction, string friendId, ulong key)
        {
            string emoji = reaction.Emoji;
            Border chip = Clickable(delegate { _host.SpatialToggleReaction(friendId, key, emoji); });
            chip.CornerRadius = new CornerRadius(10);
            chip.Padding = new Thickness(6, 2, 8, 2);
            chip.Margin = new Thickness(0, 0, 5, 3);
            chip.BorderThickness = new Thickness(1);
            chip.BorderBrush = new SolidColorBrush(reaction.Mine ? AccentCol : LineCol);
            chip.Background = new SolidColorBrush(reaction.Mine ? Color.FromArgb(0x30, AccentCol.R, AccentCol.G, AccentCol.B) : Raise);
            chip.ToolTip = reaction.Mine ? "Remove your reaction" : "React with this too";
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            if (reaction.Picture != null)
            {
                Image picture = new Image();
                picture.Source = reaction.Picture;
                picture.Width = 16;
                picture.Height = 16;
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                line.Children.Add(picture);
            }
            else
                line.Children.Add(Label(reaction.Emoji, 13, TextCol, false));
            TextBlock count = Label(reaction.Count.ToString(), 12, reaction.Mine ? AccentCol : Text2, false);
            count.Margin = new Thickness(5, 0, 0, 0);
            count.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(count);
            chip.Child = line;
            return chip;
        }

        private void RefreshDock()
        {
            SpatialSnapshot s = _state;
            DockPanel line = new DockPanel();
            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            Border meButton = Clickable(_host.SpatialMyProfile);
            meButton.CornerRadius = new CornerRadius(10);
            StackPanel meLine = new StackPanel();
            meLine.Orientation = Orientation.Horizontal;
            meLine.Children.Add(Avatar(s.MyName ?? "?", 38, s.MyStatus, s.MyPicture));
            StackPanel who = new StackPanel();
            who.Margin = new Thickness(9, 2, 12, 0);
            who.Children.Add(Label(s.MyName ?? String.Empty, 14, TextCol, true));
            who.Children.Add(Label(s.Deafened ? "Deafened" : s.Muted ? "Muted" : s.InVoice ? "Connected" : s.MyStatus ?? String.Empty, 12, Muted, false));
            meLine.Children.Add(who);
            meButton.Child = meLine;
            left.Children.Add(meButton);
            left.Children.Add(DockButton(s.Muted || s.Deafened ? "" : "", s.Muted || s.Deafened, false, s.Muted || s.Deafened ? "Unmute" : "Mute", _host.SpatialMute));
            left.Children.Add(DockButton("", s.Deafened, false, s.Deafened ? "Undeafen" : "Deafen: hear nobody and mute your mic", _host.SpatialDeafen));
            DockPanel.SetDock(left, Dock.Left);
            line.Children.Add(left);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.Children.Add(DockButton("", false, false, "All rooms and friends", GoHome));
            right.Children.Add(DockButton("", false, false, "Settings", _host.SpatialSettings));
            DockPanel.SetDock(right, Dock.Right);
            line.Children.Add(right);

            StackPanel centre = new StackPanel();
            centre.Orientation = Orientation.Horizontal;
            centre.HorizontalAlignment = HorizontalAlignment.Center;
            if (s.Connected)
            {
                if (s.Sharing)
                {
                    centre.Children.Add(LiveTag());
                    Border preview = Chip("Preview", _host.SpatialPreviewShare);
                    preview.Margin = new Thickness(6, 0, 4, 0);
                    centre.Children.Add(preview);
                    Border change = Chip("Change", _host.SpatialChangeShare);
                    change.Margin = new Thickness(0, 0, 4, 0);
                    centre.Children.Add(change);
                    Border stop = Chip("Stop", _host.SpatialStopShare);
                    stop.Margin = new Thickness(0, 0, 12, 0);
                    centre.Children.Add(stop);
                }
                StackPanel words = new StackPanel();
                words.Margin = new Thickness(0, 2, 10, 0);
                SpatialRoomChannel channel = FindChannel(s.LocalChannel);
                TextBlock state = Label(s.InVoice ? "Voice connected" + (s.Ping > 0 ? " · " + s.Ping + " ms" : String.Empty) : "In the room", 13, s.InVoice ? Good : Text2, true);
                state.HorizontalAlignment = HorizontalAlignment.Right;
                words.Children.Add(state);
                TextBlock where = Label((channel != null ? channel.Name + " · " : String.Empty) + s.RoomName, 12, Text2, false);
                where.HorizontalAlignment = HorizontalAlignment.Right;
                words.Children.Add(where);
                centre.Children.Add(words);
                if (s.InVoice)
                {
                    centre.Children.Add(DockButton("", s.Noise, true, "Filter out steady background noise like fans or hum", _host.SpatialNoise));
                    if (!s.Sharing)
                        centre.Children.Add(DockButton("", false, false, "Share your screen", _host.SpatialShare));
                    centre.Children.Add(DockButton("", true, false, "Leave voice", _host.SpatialLeaveVoice));
                }
                Border leave = Chip(s.IsHost ? "Stop room" : "Disconnect", _host.SpatialDisconnect);
                leave.VerticalAlignment = VerticalAlignment.Center;
                leave.Margin = new Thickness(8, 0, 0, 0);
                centre.Children.Add(leave);
            }
            else if (s.Running)
            {
                TextBlock state = Label(String.IsNullOrEmpty(s.Message) ? "Connecting…" : s.Message, 13, Text2, false);
                state.VerticalAlignment = VerticalAlignment.Center;
                centre.Children.Add(state);
            }
            line.Children.Add(centre);
            _dock.Child = line;
        }

        // ---- Small building blocks ----

        private static T TopLeft<T>(T element, double left, double top) where T : FrameworkElement
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.VerticalAlignment = VerticalAlignment.Top;
            element.Margin = new Thickness(left, top, 0, 0);
            return element;
        }

        private static TextBlock Label(string text, double size, Color color, bool strong)
        {
            TextBlock label = new TextBlock();
            label.Text = text ?? String.Empty;
            label.FontSize = size;
            label.Foreground = new SolidColorBrush(color);
            if (strong)
            {
                label.FontFamily = DisplayFont;
                label.FontWeight = FontWeights.SemiBold;
            }
            return label;
        }

        private static TextBlock CentredLabel(string text, double size, Color color)
        {
            TextBlock label = Label(text, size, color, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            return label;
        }

        private static TextBlock Glyph(string glyph, double size, Color color)
        {
            TextBlock icon = new TextBlock();
            icon.Text = glyph;
            icon.FontFamily = IconFont;
            icon.FontSize = size;
            icon.Foreground = new SolidColorBrush(color);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return icon;
        }

        private static Border Avatar(string name, double size, string status, ImageSource picture)
        {
            string safe = String.IsNullOrEmpty(name) ? "?" : name;
            Border avatar = new Border();
            avatar.Width = size;
            avatar.Height = size;
            avatar.CornerRadius = new CornerRadius(size / 2);
            Grid inner = new Grid();
            if (picture != null)
                avatar.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            else
            {
                avatar.Background = new SolidColorBrush(ColorFor(safe));
                inner.Children.Add(CentredLabel(safe.Substring(0, 1).ToUpperInvariant(), size * 0.42, Colors.White));
            }
            if (status != null)
            {
                Ellipse dot = new Ellipse();
                dot.Width = size * 0.38;
                dot.Height = size * 0.38;
                dot.HorizontalAlignment = HorizontalAlignment.Right;
                dot.VerticalAlignment = VerticalAlignment.Bottom;
                dot.Fill = new SolidColorBrush(StatusColor(status));
                dot.Stroke = new SolidColorBrush(SolidBg);
                dot.StrokeThickness = 2;
                inner.Children.Add(dot);
            }
            avatar.Child = inner;
            return avatar;
        }

        private static Border Badge(int count)
        {
            Border badge = new Border();
            badge.MinWidth = 20;
            badge.Height = 20;
            badge.CornerRadius = new CornerRadius(10);
            badge.Background = new SolidColorBrush(Bad);
            badge.Padding = new Thickness(5, 0, 5, 0);
            badge.Child = CentredLabel(count.ToString(), 11, Colors.White);
            return badge;
        }

        private static Border Clickable(Action click)
        {
            Border border = new Border();
            border.Background = Brushes.Transparent;
            border.Cursor = Cursors.Hand;
            border.Opacity = 1;
            border.MouseEnter += delegate { border.Opacity = 0.82; };
            border.MouseLeave += delegate { border.Opacity = 1; };
            border.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                click();
            };
            return border;
        }

        private static Border Chip(string text, Action click)
        {
            Border chip = Clickable(click);
            chip.Background = new SolidColorBrush(Fade(SolidBg, 0xE6));
            chip.BorderBrush = new SolidColorBrush(LineCol);
            chip.BorderThickness = new Thickness(1);
            chip.CornerRadius = new CornerRadius(10);
            chip.Padding = new Thickness(11, 5, 11, 5);
            chip.Margin = new Thickness(0, 0, 4, 0);
            TextBlock label = Label(text, 12.5, Text2, false);
            label.VerticalAlignment = VerticalAlignment.Center;
            chip.Child = label;
            return chip;
        }

        private static Border Tab(string text, string detail, int unread, bool selected, Action click)
        {
            Border tab = Clickable(click);
            tab.CornerRadius = new CornerRadius(10);
            tab.Padding = new Thickness(14, 6, 14, 6);
            tab.Margin = new Thickness(0, 0, 4, 0);
            tab.Background = selected ? new SolidColorBrush(AccentCol) : new SolidColorBrush(Fade(SolidBg, 0xE6));
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            Color ink = selected ? OnAccent : Text2;
            line.Children.Add(Label(text, 15, ink, true));
            TextBlock more = Label(detail, 11.5, ink, false);
            more.Margin = new Thickness(7, 2, 0, 0);
            more.Opacity = 0.75;
            line.Children.Add(more);
            if (unread > 0)
            {
                Border badge = Badge(unread);
                badge.Margin = new Thickness(7, 0, 0, 0);
                line.Children.Add(badge);
            }
            tab.Child = line;
            return tab;
        }

        private static Border DockButton(string glyph, bool on, bool soft, string tip, Action click)
        {
            Border button = Clickable(click);
            button.Width = 34;
            button.Height = 34;
            button.CornerRadius = new CornerRadius(9);
            button.Margin = new Thickness(2, 0, 2, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = tip;
            if (on)
                button.Background = new SolidColorBrush(soft ? Color.FromArgb(0x30, AccentCol.R, AccentCol.G, AccentCol.B) : Bad);
            button.Child = Glyph(glyph, 16, on ? (soft ? AccentCol : Colors.White) : Text2);
            return button;
        }

        private static Color StatusColor(string status)
        {
            if (status == "Online") return Good;
            if (status == "Away") return Color.FromRgb(0xF0, 0xB2, 0x32);
            if (status == "Busy") return Bad;
            return Color.FromRgb(0x68, 0x70, 0x8A);
        }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
    }
}

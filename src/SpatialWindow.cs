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
            bool failed = false;
            dispatcher.UnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                // Once is enough: frames keep running behind the message, and may fail the same way.
                if (failed)
                    return;
                failed = true;
                Program.LogCrash(e.Exception);
                if (_window != null)
                    _window.Hide();
                MessageBox.Show("The Dynamic layout ran into a problem, so mæla is switching to the Lightweight layout. Your room or call keeps running.\n\n" + e.Exception.Message, "mæla");
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
                MessageBox.Show("The Dynamic layout could not open, so mæla is switching to the Lightweight layout.\n\n" + ex.Message, "mæla");
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
        public bool Placed;              // Where you put it, and how big, in the Dynamic layout.
        public double PlaceX, PlaceY, Size = 1;
        public int Members, Max;
        public ImageSource Picture;
    }

    internal sealed class SpatialFriend
    {
        public string Id, Name, Status, Circle;
        public bool IsFriend, WantsToBeFriends;
        public int Unread;
        public bool Placed;
        public double PlaceX, PlaceY, Size = 1;
    }

    internal sealed class SpatialPerson
    {
        public uint Id;
        public string Name;
        public bool IsLocal, Speaking, Muted, Deafened, Live;
        public int ChannelId;
        public ImageSource Picture;
        public PictureDeco Deco;          // None unless you have Dynamic effects on.
        public int DecoA, DecoB;
    }

    internal sealed class SpatialRoomChannel
    {
        public int Id;
        public string Name;
        public bool IsText;
        public bool HasLayout;           // The host has moved or resized it:
        public double Angle, Reach, Size; // round the room, distance (1 usual) and size (1 usual).
        public readonly List<SpatialPerson> People = new List<SpatialPerson>();
    }

    internal sealed class SpatialLine
    {
        public string Who, Text, Note;
        public ImageSource Picture;
        // A picture the message carries: a small copy to show, or still on its way, or gone.
        public string ShotId;
        public int ShotWidth, ShotHeight;
        public ImageSource Shot;
        public bool ShotComing;
        public string FriendId;  // Whose chat it's in, so the full picture can be found; null in a room.
        public string VideoId;   // A YouTube video it links to.
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
        public Action Local; // A menu the Dynamic window makes itself: what this item does.
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
        /// <summary>Asks for your own card, in or out of a room.</summary>
        public const uint Mine = uint.MaxValue;

        public uint Id;
        public string Name, Status, State;
        public ImageSource Picture;
        public bool IsLocal, IsHost, ViewerIsHost, MutedForMe, CanWatch;
        public string LiveText;          // "Watch stream · 1080p60", or null if they aren't live.
        public int Volume = 100;         // Just for you, 0 to 200.
        public string MyStatus;          // Your own card: Online, Away, Busy or Offline.
        public int ChannelId;
        public readonly List<SpatialRoomChannel> VoiceChannels = new List<SpatialRoomChannel>();
        public BannerKind Banner;
        public int BannerA, BannerB;     // 0xRRGGBB, also the effect's colours.
        public ImageSource BannerPicture;
        public CardEffect Effect;        // None unless you have Dynamic effects on.
        public PictureDeco Deco;
    }

    /// <summary>A picture deco around one picture in the space or the dock, moving a frame at a time.</summary>
    internal sealed class SpatialDecoRing : FrameworkElement
    {
        private readonly PictureDeco _deco;
        private readonly Color _a, _b;
        private readonly Point _centre;
        private readonly double _radius;
        private double _time;

        public SpatialDecoRing(PictureDeco deco, int a, int b, Point centre, double radius)
        {
            _deco = deco;
            _a = SpatialCardEffect.Rgb(a);
            _b = SpatialCardEffect.Rgb(b);
            _centre = centre;
            _radius = radius;
            IsHitTestVisible = false;
        }

        public void Tick(double time)
        {
            _time = time;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            SpatialCardEffect.DrawDeco(dc, _deco, _time, _centre, _radius, _a, _b);
        }
    }

    /// <summary>
    /// A profile card's effect, drawn over the card a frame at a time. The shapes come from
    /// CardEffects, so they match the Lightweight card.
    /// </summary>
    internal sealed class SpatialCardEffect : FrameworkElement
    {
        private readonly CardEffect _effect;
        private readonly PictureDeco _deco;
        private readonly Color _a, _b;
        private readonly int _seed;
        private readonly FrameworkElement _avatar, _banner, _card;
        private readonly DropShadowEffect _shadow;
        private double _time;

        /// <summary>
        /// Two of these go on a card: the card deco just above the banner, so the picture, words and
        /// buttons sit in front of it, and the picture deco on top. Each is given None for the other.
        /// </summary>
        public SpatialCardEffect(CardEffect effect, PictureDeco deco, int a, int b, int seed, FrameworkElement avatar, FrameworkElement banner, FrameworkElement card, DropShadowEffect shadow)
        {
            _card = card;
            _effect = effect;
            _deco = deco;
            _a = Rgb(a);
            _b = Rgb(b);
            _seed = seed;
            _avatar = avatar;
            _banner = banner;
            _shadow = shadow;
            IsHitTestVisible = false;
        }

        public void Tick(double time)
        {
            _time = time;
            if (_effect == CardEffect.Glow && _shadow != null)
            {
                // The card's own shadow becomes a breathing glow in their colour.
                double pulse = CardEffects.Pulse(time);
                _shadow.Color = _a;
                _shadow.BlurRadius = 22 + 16 * pulse;
                _shadow.Opacity = 0.55 + 0.4 * pulse;
            }
            InvalidateVisual();
        }

        private static Color Alpha(Color c, double alpha)
        {
            return Color.FromArgb((byte)Math.Max(0, Math.Min(255, alpha * 255)), c.R, c.G, c.B);
        }

        public static Color Rgb(int rgb)
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        /// <summary>A picture deco around a picture: the same shapes on the card, in the space and in the dock.</summary>
        public static void DrawDeco(DrawingContext dc, PictureDeco deco, double time, Point centre, double radius, Color a, Color b)
        {
            if (deco == PictureDeco.None)
                return;
            foreach (EffectDot dot in CardEffects.Deco(deco, time, centre.X, centre.Y, radius))
            {
                Brush brush = new SolidColorBrush(Alpha(dot.Second ? b : a, 0.94 * dot.Alpha));
                if (deco == PictureDeco.Ripple)
                    dc.DrawEllipse(null, new Pen(brush, 2), new Point(dot.X, dot.Y), dot.Size, dot.Size);
                else
                    dc.DrawEllipse(brush, null, new Point(dot.X, dot.Y), dot.Size, dot.Size);
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_card.ActualWidth <= 0 || _card.ActualHeight <= 0)
                return;
            // These may sit anywhere in the card, so map through the tree rather than assume a parent.
            Rect box = _card.TransformToVisual(this).TransformBounds(new Rect(0, 0, _card.ActualWidth, _card.ActualHeight));
            Rect banner = _banner.TransformToVisual(this).TransformBounds(new Rect(0, 0, _banner.ActualWidth, _banner.ActualHeight));
            double radius = _avatar.ActualWidth / 2;
            Point avatar = _avatar.TranslatePoint(new Point(radius, radius), this);
            double w = box.Width, h = box.Height;
            dc.PushClip(new RectangleGeometry(box, 15, 15)); // On the card only, inside its rounded edge.
            dc.PushTransform(new TranslateTransform(box.X, box.Y));
            avatar = new Point(avatar.X - box.X, avatar.Y - box.Y);
            banner.Offset(-box.X, -box.Y);
            if (_effect == CardEffect.Glow)
            {
                double pulse = CardEffects.Pulse(_time);
                for (int i = 0; i < 4; i++)
                {
                    double fade = (1 - i / 4.0) * (1 - i / 4.0);
                    Pen pen = new Pen(new SolidColorBrush(Alpha(i % 2 == 0 ? _a : _b, (0.5 + 0.35 * pulse) * fade)), 1.5);
                    dc.DrawRoundedRectangle(null, pen, new Rect(i + 0.75, i + 0.75, w - 2 * i - 1.5, h - 2 * i - 1.5), 15 - i, 15 - i);
                }
            }
            else if (_effect == CardEffect.Sparkles)
            {
                foreach (EffectDot dot in CardEffects.Sparkles(_seed, _time, w, h, avatar.X, avatar.Y, radius))
                {
                    Color tint = Color.FromRgb((byte)(255 - (255 - (dot.Second ? _b : _a).R) * 0.35), (byte)(255 - (255 - (dot.Second ? _b : _a).G) * 0.35), (byte)(255 - (255 - (dot.Second ? _b : _a).B) * 0.35));
                    System.Drawing.PointF[] star = CardEffects.Star((float)dot.X, (float)dot.Y, (float)dot.Size);
                    StreamGeometry shape = new StreamGeometry();
                    using (StreamGeometryContext ctx = shape.Open())
                    {
                        ctx.BeginFigure(new Point(star[0].X, star[0].Y), true, true);
                        for (int i = 1; i < star.Length; i++)
                            ctx.LineTo(new Point(star[i].X, star[i].Y), false, false);
                    }
                    dc.DrawGeometry(new SolidColorBrush(Alpha(tint, 0.92 * dot.Alpha)), null, shape);
                }
            }
            else if (_effect == CardEffect.Aurora)
            {
                dc.PushClip(new RectangleGeometry(banner, 15, 15));
                Color[] colours = { _a, _b, Color.FromRgb((byte)((_a.R + _b.R) / 2), (byte)((_a.G + _b.G) / 2), (byte)((_a.B + _b.B) / 2)) };
                const int steps = 30;
                for (int band = 0; band < 3; band++)
                {
                    double[] ys = CardEffects.AuroraBand(band, _time, banner.Width, banner.Height, steps);
                    double thick = banner.Height * 0.32;
                    StreamGeometry shape = new StreamGeometry();
                    using (StreamGeometryContext ctx = shape.Open())
                    {
                        ctx.BeginFigure(new Point(banner.Left, banner.Top + ys[0] - thick / 2), true, true);
                        for (int i = 1; i <= steps; i++)
                            ctx.LineTo(new Point(banner.Left + i * banner.Width / steps, banner.Top + ys[i] - thick / 2), false, false);
                        for (int i = steps; i >= 0; i--)
                            ctx.LineTo(new Point(banner.Left + i * banner.Width / steps, banner.Top + ys[i] + thick / 2), false, false);
                    }
                    LinearGradientBrush fill = new LinearGradientBrush();
                    fill.StartPoint = new Point(0, 0.5);
                    fill.EndPoint = new Point(1, 0.5);
                    fill.GradientStops.Add(new GradientStop(Alpha(colours[band], 0.04), 0));
                    fill.GradientStops.Add(new GradientStop(Alpha(colours[band], 0.38), 0.5));
                    fill.GradientStops.Add(new GradientStop(Alpha(colours[band], 0.04), 1));
                    dc.DrawGeometry(fill, null, shape);
                }
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
            DrawDeco(dc, _deco, _time, new Point(avatar.X + box.X, avatar.Y + box.Y), radius, _a, _b);
        }
    }

    /// <summary>A circle you sort rooms or friends into. Favourites and the main one can't be deleted.</summary>
    internal sealed class SpatialCircle
    {
        // Any circle can hold rooms and friends. Rooms and Friends are just where each starts out.
        public const string FavouritesId = "fav", MainId = "main", FriendsId = "friends";
        public const string FreeId = "free"; // Not a circle: in none, floating where it was dropped.
        public string Id, Name;
        public bool HasPosition; // Stays where you dragged it. Otherwise it's placed automatically.
        public double X, Y;
        public bool Protected { get { return Id == FavouritesId || Id == MainId || Id == FriendsId; } }


    }

    internal sealed class SpatialSnapshot
    {
        public List<SpatialCircle> Circles = new List<SpatialCircle>();
        public string MyName, MyStatus, RoomName, Message, ThemeId;
        public ImageSource MyPicture;
        public PictureDeco MyDeco;
        public int MyDecoA, MyDecoB;
        public bool Muted, Deafened, Noise, Running, Connected, IsHost, InVoice, Sharing;
        public int Ping, LocalChannel, ChatChannel;
        public readonly List<SpatialRoom> Rooms = new List<SpatialRoom>();
        public readonly List<SpatialFriend> Friends = new List<SpatialFriend>();
        public readonly List<SpatialRoomChannel> Channels = new List<SpatialRoomChannel>();
        public readonly List<SpatialLine> Chat = new List<SpatialLine>();
        public readonly List<SpatialLine> FriendChat = new List<SpatialLine>();
        public string ChatKey = String.Empty, FriendChatId, FriendChatKey = String.Empty;
        public string StructureKey = String.Empty, TalkKey = String.Empty, DockKey = String.Empty;
        public int LayoutKey;
        public int FrameRate; // See AppPreferences.FrameRate.
        public int ReadingFont, TextScale = 100;
        public bool ReduceMotion;
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
                if (String.IsNullOrEmpty(circle))
                    return Room != null ? SpatialCircle.MainId : SpatialCircle.FriendsId;
                return circle;
            }
            set
            {
                if (Room != null) Room.Circle = value;
                else Friend.Circle = value;
            }
        }

        // Where you put it: relative to its circle's centre, or in the space when it's in none.
        public bool Placed
        {
            get { return Room != null ? Room.Placed : Friend.Placed; }
            set { if (Room != null) Room.Placed = value; else Friend.Placed = value; }
        }
        public double PlaceX
        {
            get { return Room != null ? Room.PlaceX : Friend.PlaceX; }
            set { if (Room != null) Room.PlaceX = value; else Friend.PlaceX = value; }
        }
        public double PlaceY
        {
            get { return Room != null ? Room.PlaceY : Friend.PlaceY; }
            set { if (Room != null) Room.PlaceY = value; else Friend.PlaceY = value; }
        }
        public double Size
        {
            get { return Room != null ? Room.Size : Friend.Size; }
            set { if (Room != null) Room.Size = value; else Friend.Size = value; }
        }

        /// <summary>The orb's radius in the space, at its size.</summary>
        public double Radius { get { return (Room != null ? 46 : 39) * Size; } }
    }

    /// <summary>A circle as laid out in the space.</summary>
    internal sealed class SpatialGroup
    {
        public string Id, Title;
        public int Count;
        public double X, Y, Radius, LabelY;
        public double AutoRadius; // Its usual size; it grows to fit placed orbs, up to twice this.
        public bool Placed;       // You've dragged the circle itself somewhere.
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
        public double WrapRadius; // Rooms, friends, channels and people: the ball's radius, for the dots to wrap.
        public ScaleTransform BallScale; // Grows the ball, not its name, while it's targeted.
        public SpatialPerson Person;     // Someone in a voice channel: click for their card.
        public int ChannelId;            // A channel's id, so people can be dropped onto it.
        public bool VoiceChannel;
        public double Grow;              // 0 to 1: how far it has grown.
        public SpatialNode Channel;      // Someone in voice: their channel's ball, which their voice line runs to.
        public double Voice;             // 0 to 1: how loud they are now, smoothed.
        public double PushX, PushY;      // Pushed out by their own voice, and aside by loud neighbours.
        public TranslateTransform LabelMove; // Keeps the name just under the ball as it grows,
        public double LabelDrop;             // by this much for each time bigger.
        public double SizeScale = 1;     // A channel the host has resized.
        public double Angle, Reach = 1, HomeAngle; // A channel: where it sits round the room, and its usual place.
        public double Mid;               // Someone in voice: their place in the channel's column.
        public double GlideX, GlideY;    // Still catching up after being moved; eases to nothing.
        public bool Tuckable;            // A channel or person: pulled into the room's ball when it's clicked.
        public Shape Ring;               // A circle's title: its (now unseen) ring,
        public SpatialNode RingNode;     // whose place on screen the gathering dots go round,
        public TextBlock TitleText;      // and its words, which light up on hover.
        public double Glow;              // 0 to 1: how lit the title and ring are.
        public RotateTransform PatternTurn; // An orb with no picture: its pattern turns while it's hovered.
        public double ScreenX, ScreenY, ScreenScale; // Where it was last drawn.
        public bool OnScreen = true;
    }

    /// <summary>
    /// A line from each person in a voice channel to the channel's ball: flat and faint while
    /// they're quiet, a live waveform in the theme's highlight while they talk, bigger the
    /// louder they are.
    /// </summary>
    internal sealed class SpatialVoiceLines : FrameworkElement
    {
        public readonly List<SpatialNode> People = new List<SpatialNode>();
        public Color Colour = Colors.White;
        public Func<SpatialNode, double> RadiusOf; // A ball's radius on screen right now.
        private double _time;

        public SpatialVoiceLines()
        {
            IsHitTestVisible = false;
        }

        public void Tick(double time)
        {
            _time = time;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            const int steps = 36;
            foreach (SpatialNode person in People)
            {
                SpatialNode channel = person.Channel;
                if (channel == null || !person.OnScreen || !channel.OnScreen)
                    continue;
                double dx = channel.ScreenX - person.ScreenX, dy = channel.ScreenY - person.ScreenY;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double fromEdge = RadiusOf(person) + 3, toEdge = RadiusOf(channel) + 3;
                if (length <= fromEdge + toEdge + 4)
                    continue;
                double ux = dx / length, uy = dy / length; // Along the line,
                double nx = -uy, ny = ux;                   // and across it.
                double run = length - fromEdge - toEdge;
                double voice = person.Voice;
                double talk = Math.Min(1, voice), shout = Math.Max(0, voice - 1);
                double height = (talk + 2 * shout) * (5 + 11 * person.ScreenScale);
                double seed = person.Person == null ? 0 : (person.Person.Id % 97) * 0.37;

                StreamGeometry wave = new StreamGeometry();
                using (StreamGeometryContext ctx = wave.Open())
                {
                    for (int i = 0; i <= steps; i++)
                    {
                        double u = (double)i / steps;
                        // Pinned at both ends, widest in the middle; two speeds so it looks like a voice, not a sine.
                        double envelope = Math.Sin(u * Math.PI);
                        double offset = height * envelope * (0.7 * Math.Sin(u * 26 - _time * 18 + seed) + 0.3 * Math.Sin(u * 61 + _time * 29 + seed * 2));
                        Point p = new Point(person.ScreenX + ux * (fromEdge + run * u) + nx * offset, person.ScreenY + uy * (fromEdge + run * u) + ny * offset);
                        if (i == 0)
                            ctx.BeginFigure(p, false, false);
                        else
                            ctx.LineTo(p, true, true);
                    }
                }
                wave.Freeze();
                byte alpha = (byte)(46 + 209 * Math.Min(1, voice * 1.6));
                Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, Colour.R, Colour.G, Colour.B)), 1.2 + 1.4 * talk + 1.8 * shout);
                pen.LineJoin = PenLineJoin.Round;
                dc.DrawGeometry(null, pen, wave);
            }
        }
    }

    /// <summary>The drifting dots and the lines that reach for the pointer.</summary>
    internal sealed class SpatialStars : FrameworkElement
    {
        private const int Count = 240;
        private const double Reach = 190;
        private readonly double[] _x = new double[Count], _y = new double[Count], _d = new double[Count];
        private readonly double[] _nearX = new double[Count], _nearY = new double[Count], _nearP = new double[Count];
        private readonly double[] _edgeX = new double[Count], _edgeY = new double[Count];
        private readonly Brush[] _dots = new Brush[8];
        private readonly Pen[] _toPointer = new Pen[8];
        private readonly Pen[] _between = new Pen[8];
        public Point Pointer = new Point(-9999, -9999);
        public double DriftX, DriftY;

        // What the lines reach for: the pointer, or the edge of a ball or a profile card. It's a
        // rounded rectangle: a ball is one with equal sides and full rounding, the pointer one with
        // no size. It glides between them just as the lines follow the pointer.
        public bool Wrapping;
        public Point WrapTarget;
        public double WrapHalfWidth, WrapHalfHeight, WrapCorner;
        private Point _focus = new Point(-9999, -9999);
        private double _halfWidth, _halfHeight, _corner;

        /// <summary>Aim at a ball.</summary>
        public void WrapBall(Point centre, double radius)
        {
            Wrapping = true;
            WrapTarget = centre;
            WrapHalfWidth = radius;
            WrapHalfHeight = radius;
            WrapCorner = radius;
        }

        /// <summary>Aim at a rectangle with rounded corners, such as a profile card.</summary>
        public void WrapBox(Rect box, double corner)
        {
            Wrapping = true;
            WrapTarget = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            WrapHalfWidth = box.Width / 2;
            WrapHalfHeight = box.Height / 2;
            WrapCorner = Math.Min(corner, Math.Min(box.Width, box.Height) / 2);
        }
        private double _arrived; // 0 to 1: how far the focus has got onto the ball. The highlight follows this.

        /// <summary>Holds the focus exactly on its target, with no glide: for something being dragged.</summary>
        public bool Locked;

        // A circle hovered or being dragged: about two dots in three leave the background and gather
        // round its edge, in two rings that ripple against each other, linked into a net.
        public bool Ringing;
        public object RingKey;          // Which circle is wanted. A different one: the ring lets go, then forms there.
        public Point RingCentre;
        public double RingRadius;
        private object _ringKey;
        private Point _ringCentre;
        private double _ringRadius;
        private double _travel = 1; // 0 to 1: the dots on their way from the last circle to this one.
        private double _ringHold;   // Seconds the ring holds on after it stops being wanted.
        // Chasing: the ring travels to a moving point (an orb pulled out of its circle) just as it
        // does to another circle, following it on the way, and once it has arrived it breaks away softly.
        public bool RingChase;
        private bool _caught;
        private double _ringLines, _softLines; // 0 to 1: how far the rings' links have faded in.
        private bool _linesMoving;
        private readonly double[] _fromX = new double[Count], _fromY = new double[Count]; // Where each set off from,
        private readonly double[] _atX = new double[Count], _atY = new double[Count];     // and where it was last drawn going.
        private double _ring, _spin;
        private readonly int[] _ringSlot = new int[Count]; // Each dot's place round the ring, or -1 to stay put.
        private int _ringCount;
        private readonly double[] _ringX = new double[Count], _ringY = new double[Count], _ringP = new double[Count];

        /// <summary>True while dots are gathering, travelling or going back. A formed ring turns slowly, which the idle frames cover.</summary>
        public bool RingBusy { get { return (_ring > 0 && (_ring < 1 || _travel < 1 || !Ringing || RingChase)) || _softMoving || _ringHold > 0 || _spoke > 0 || _linesMoving || (_badge > 0 && _badge < 0.99) || (!Badge && _badge > 0); } }

        // The loose ring: hover an orb and the dots it reaches for drift out to its circle's edge,
        // one simple ring with gaps. It stays while the pointer is inside that circle, collecting
        // the dots of each orb hovered there, and lets them go gently when the pointer leaves.
        public bool SoftRinging;
        public object SoftKey;
        public Point SoftCentre;
        public double SoftRadius;
        private object _softKey;
        private Point _softCentre;
        private double _softRadius;
        private const int MaxSoft = 40, MinSoft = 32;
        private int _softFill, _fillDot = -1;
        private double _fillX, _fillY, _fillGap;
        private readonly double[] _softAngle = new double[Count], _softG = new double[Count];
        private readonly List<int> _softMembers = new List<int>(); // In order round the ring.
        private readonly int[] _nearI = new int[Count];
        private readonly List<int> _leaving = new List<int>(); // From a loose ring left for another: going home.
        private readonly double[] _leaveX = new double[Count], _leaveY = new double[Count], _leaveR = new double[Count];
        private readonly double[] _softX = new double[Count], _softY = new double[Count];
        private bool _softMoving;
        private double _denseSpin, _softSpin;

        // A circle's title hovered: a line from it to each orb in the circle, each orb lightly ringed.
        public bool Spoking;
        public Rect SpokeFrom;           // The title: the lines start from its edge, wherever the pointer goes.
        private Rect _spokeFrom;
        public readonly List<Point> SpokeCentres = new List<Point>();
        public readonly List<double> SpokeRadii = new List<double>();
        private readonly List<Point> _spokeCentres = new List<Point>();
        private readonly List<double> _spokeRadii = new List<double>();
        private double _spoke;

        /// <summary>A title lit on its own (zoomed in on its circle): a glow round it, and the ring's nearby dots reaching up to it.</summary>
        public bool Badge;
        public Rect BadgeBox;
        private Rect _badgeBox;
        private double _badge;

        /// <summary>True while the title's lines are showing or still fading out.</summary>
        public bool SpokesShowing { get { return _spoke > 0; } }

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
                _ringSlot[i] = i % 3 == 2 ? -1 : _ringCount++;
                _softAngle[i] = Double.NaN;
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
            // A frame or two without a ring wanted (the pointer between two things, the scene being
            // rebuilt) holds it rather than letting it fall apart and form again.
            if (Ringing)
                _ringHold = 0.3;
            else if (_ringHold > 0)
                _ringHold -= seconds;
            bool want = Ringing || (_ringHold > 0 && _ringKey != null);
            if (Ringing && !Object.Equals(RingKey, _ringKey))
            {
                // Still round another circle: the dots set off from there rather than starting again.
                _caught = false;
                if (_ring > 0.04 && _ringKey != null)
                {
                    // From where each dot is right now, even halfway across from somewhere else.
                    Array.Copy(_atX, _fromX, _ringCount);
                    Array.Copy(_atY, _fromY, _ringCount);
                    _travel = 0;
                }
                _ringKey = RingKey;
            }
            _travel = Math.Min(1, _travel + seconds / 0.8);
            if (Ringing && RingChase)
            {
                // The ring it's travelling to moves with the orb; once the dots have arrived, it lets go.
                _ringCentre = RingCentre;
                _ringRadius = RingRadius;
                if (_travel >= 1)
                    _caught = true;
            }
            else if (Ringing)
            {
                _ringCentre = RingCentre; // Follows the circle as the camera or the circle moves.
                _ringRadius = RingRadius;
            }
            bool fading = Ringing && RingChase && _caught; // Caught up with the orb: breaking away softly.
            double goal = want && !fading ? 1 : 0;
            _ring += (goal - _ring) * Math.Min(1, seconds * (goal > 0 ? 2.4 : fading ? 1.3 : 4.5));
            if (want && _ring > 0.998)
                _ring = 1;
            // The links between ring dots fade in slowly once it has formed, dim while it travels,
            // and fade out first when it breaks up, so building and breaking look calm.
            double linesGoal = want && !fading && _ring > 0.5 ? (_travel < 1 ? 0.35 : 1) : 0;
            _ringLines += (linesGoal - _ringLines) * Math.Min(1, seconds * (linesGoal > _ringLines ? 1.1 : 3.5));
            if (linesGoal == 0 && _ringLines < 0.01)
                _ringLines = 0;
            double softGoal = SoftRinging && _softMembers.Count > 1 ? 1 : 0;
            _softLines += (softGoal - _softLines) * Math.Min(1, seconds * (softGoal > _softLines ? 1.0 : 3.5));
            if (softGoal == 0 && _softLines < 0.01)
                _softLines = 0;
            _linesMoving = Math.Abs(linesGoal - _ringLines) > 0.01 || Math.Abs(softGoal - _softLines) > 0.01;
            if (!want && _ring < 0.003)
            {
                _ring = 0;
                if (!want)
                    _ringKey = null;
            }
            _spin += seconds;
            _denseSpin += seconds * 0.2;
            _softSpin += seconds * 0.07;
            StepSoft(seconds);
            if (SpokeCentres.Count > 0 || Spoking)
            {
                // The latest places, fading out too; with none given, the last ones are kept.
                _spokeCentres.Clear();
                _spokeCentres.AddRange(SpokeCentres);
                _spokeRadii.Clear();
                _spokeRadii.AddRange(SpokeRadii);
                _spokeFrom = SpokeFrom;
            }
            if (Badge)
                _badgeBox = BadgeBox;
            _badge += ((Badge ? 1 : 0) - _badge) * Math.Min(1, seconds * (Badge ? 2.5 : 5));
            if (!Badge && _badge < 0.01)
                _badge = 0;
            _spoke += ((Spoking ? 1 : 0) - _spoke) * Math.Min(1, seconds * (Spoking ? 7 : 5));
            if (!Spoking && _spoke < 0.01)
                _spoke = 0;
            Point target = Wrapping ? WrapTarget : Pointer;
            double halfWidth = Wrapping ? WrapHalfWidth : 0, halfHeight = Wrapping ? WrapHalfHeight : 0, corner = Wrapping ? WrapCorner : 0;
            bool away = target.X < -9000 || _focus.X < -9000;
            bool settled = !Wrapping && _halfWidth < 1 && _halfHeight < 1 && Math.Abs(_focus.X - target.X) + Math.Abs(_focus.Y - target.Y) < 8;
            if (away || settled || (Locked && Wrapping))
            {
                // Plain pointer, or something being dragged: follow it exactly.
                _focus = target;
                _halfWidth = halfWidth;
                _halfHeight = halfHeight;
                _corner = corner;
            }
            else
            {
                // Back to the pointer quickly, so it never trails a moving mouse; onto a ball a little gentler.
                double glide = Math.Min(1, seconds * (Wrapping ? 7 : 18));
                _focus = new Point(_focus.X + (target.X - _focus.X) * glide, _focus.Y + (target.Y - _focus.Y) * glide);
                _halfWidth += (halfWidth - _halfWidth) * glide;
                _halfHeight += (halfHeight - _halfHeight) * glide;
                _corner += (corner - _corner) * glide;
            }
            // The highlight builds as the focus settles on its target, and fades as soon as it
            // leaves, so moving quickly from one ball to the next doesn't drag it across the gap.
            double arrived = 0;
            if (Wrapping && WrapHalfWidth > 0 && WrapHalfHeight > 0)
            {
                double fx = _focus.X - WrapTarget.X, fy = _focus.Y - WrapTarget.Y;
                double distance = Math.Sqrt(fx * fx + fy * fy) / (Math.Min(WrapHalfWidth, WrapHalfHeight) + 40);
                arrived = Math.Max(0, 1 - distance) * Math.Min(1, Math.Min(_halfWidth / WrapHalfWidth, _halfHeight / WrapHalfHeight));
            }
            if (Locked && Wrapping)
                arrived = 1;
            double rate = arrived < _arrived ? 22 : (Locked ? 22 : 8);
            _arrived += (arrived - _arrived) * Math.Min(1, seconds * rate);
            if (_arrived < 0.005)
                _arrived = 0;
            InvalidateVisual();
        }

        private void StepSoft(double seconds)
        {
            _softMoving = false;
            bool want = SoftRinging;
            if (want && !Object.Equals(SoftKey, _softKey))
            {
                // Round another circle: those dots head home from where they are, on their own,
                // while this circle's ring starts straight away.
                foreach (int d in _softMembers)
                {
                    _leaving.Add(d);
                    _leaveX[d] = _softCentre.X;
                    _leaveY[d] = _softCentre.Y;
                    _leaveR[d] = _softRadius;
                }
                _softMembers.Clear();
                _softKey = SoftKey;
            }
            for (int m = _leaving.Count - 1; m >= 0; m--)
            {
                int d = _leaving[m];
                _softG[d] -= _softG[d] * Math.Min(1, seconds * 1.6);
                _softMoving = true;
                if (_softG[d] < 0.01)
                {
                    _softG[d] = 0;
                    _softAngle[d] = Double.NaN;
                    _leaving.RemoveAt(m);
                }
            }
            if (want)
            {
                _softCentre = SoftCentre;
                _softRadius = SoftRadius;
            }
            for (int m = _softMembers.Count - 1; m >= 0; m--)
            {
                int d = _softMembers[m];
                double goal = want ? 1 : 0;
                _softG[d] += (goal - _softG[d]) * Math.Min(1, seconds * (want ? 2.2 : 1.3));
                if (Math.Abs(goal - _softG[d]) > 0.003)
                    _softMoving = true;
                if (!want && _softG[d] < 0.01)
                {
                    _softG[d] = 0;
                    _softAngle[d] = Double.NaN;
                    _softMembers.RemoveAt(m);
                }
            }
            if (!SoftRinging && _softMembers.Count == 0)
                _softKey = null;

            // Once on the ring, its dots slide round to even spacing, so it closes up instead of
            // bunching where they came from. Their order round it never changes.
            int count = _softMembers.Count;
            if (want && count >= 2)
            {
                double step = 2 * Math.PI / count, sin = 0, cos = 0;
                for (int m = 0; m < count; m++)
                {
                    double a = _softAngle[_softMembers[m]] - m * step;
                    sin += Math.Sin(a);
                    cos += Math.Cos(a);
                }
                double start = Math.Atan2(sin, cos), ease = Math.Min(1, seconds * 1.6);
                for (int m = 0; m < count; m++)
                {
                    int d = _softMembers[m];
                    double off = Math.IEEERemainder(start + m * step - _softAngle[d], 2 * Math.PI);
                    if (Math.Abs(off) > 0.002)
                    {
                        _softAngle[d] += off * ease;
                        _softMoving = true;
                    }
                }
            }
        }

        private static double Turn(double angle)
        {
            double turn = angle % (2 * Math.PI);
            return turn < 0 ? turn + 2 * Math.PI : turn;
        }

        /// <summary>Takes a dot into the loose ring, in its place round it, starting from where it is.</summary>
        private void JoinSoft(int dot, double x, double y)
        {
            double angle = Math.Atan2(y - _softCentre.Y, x - _softCentre.X) - _softSpin;
            _softAngle[dot] = angle;
            _softG[dot] = 0;
            // Between the two neighbours it falls between going round, wherever the ring has turned to.
            int at = _softMembers.Count;
            for (int m = 0; m < _softMembers.Count; m++)
            {
                double from = _softAngle[_softMembers[m]], to = _softAngle[_softMembers[(m + 1) % _softMembers.Count]];
                if (_softMembers.Count == 1 || Turn(angle - from) < Turn(to - from))
                {
                    at = m + 1;
                    break;
                }
            }
            _softMembers.Insert(at, dot);
        }

        private static int Level(double value)
        {
            int level = (int)(value * 8);
            return level < 0 ? 0 : (level > 7 ? 7 : level);
        }

        /// <summary>
        /// The nearest point on the focus's edge to a point, and how far outside it that point is.
        /// With no size, the edge is the focus itself.
        /// </summary>
        private Point EdgeNear(double x, double y, out double gap)
        {
            double innerW = Math.Max(0, _halfWidth - _corner), innerH = Math.Max(0, _halfHeight - _corner);
            double dx = x - _focus.X, dy = y - _focus.Y;
            double cx = Math.Max(-innerW, Math.Min(innerW, dx)), cy = Math.Max(-innerH, Math.Min(innerH, dy));
            double ox = dx - cx, oy = dy - cy, out_ = Math.Sqrt(ox * ox + oy * oy);
            if (out_ > 0.001)
            {
                gap = out_ - _corner;
                return new Point(_focus.X + cx + ox / out_ * _corner, _focus.Y + cy + oy / out_ * _corner);
            }
            // Inside the straight part: out through the nearest side.
            double toSide = innerW - Math.Abs(dx), toTop = innerH - Math.Abs(dy);
            gap = -Math.Min(toSide, toTop) - _corner;
            if (toSide < toTop)
                return new Point(_focus.X + Math.Sign(dx == 0 ? 1 : dx) * _halfWidth, _focus.Y + dy);
            return new Point(_focus.X + dx, _focus.Y + Math.Sign(dy == 0 ? 1 : dy) * _halfHeight);
        }

        /// <summary>
        /// Dots near the focus lean towards it and draw lines to it. Normally the focus is the
        /// pointer. Over a room, channel or person it glides onto the ball and grows to its size,
        /// and with a profile card open it takes the card's shape, so the dots reach for the
        /// nearest point on its edge and the lines land all round it.
        /// </summary>
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double size = Math.Min(_halfWidth, _halfHeight);
            double reach = Reach + size;          // Something bigger draws from further out.
            double lean = 0.14 + 0.12 * _arrived; // And holds its dots a little closer.
            int near = 0, i, j;
            for (i = 0; i < _ringCount; i++)
                _ringP[i] = 0;
            _fillDot = -1;
            // With the focus inside a formed ring, the ring is the edge of its world: loose dots
            // beyond it fade out of its net, so nothing reaches outside the circle.
            Point fence = new Point();
            double fenceRadius = 0, fenceStrength = 0;
            double fx0 = _focus.X - _ringCentre.X, fy0 = _focus.Y - _ringCentre.Y;
            if (_ring > 0 && fx0 * fx0 + fy0 * fy0 < _ringRadius * _ringRadius)
            {
                fence = _ringCentre;
                fenceRadius = _ringRadius;
                fenceStrength = _ring;
            }
            else if (SoftRinging && Object.Equals(SoftKey, _softKey) && _softMembers.Count > 0)
            {
                double sx0 = _focus.X - _softCentre.X, sy0 = _focus.Y - _softCentre.Y;
                if (sx0 * sx0 + sy0 * sy0 < _softRadius * _softRadius)
                {
                    fence = _softCentre;
                    fenceRadius = _softRadius;
                    fenceStrength = 1;
                }
            }
            for (i = 0; i < Count; i++)
            {
                double sx = Wrap(_x[i] - DriftX * 0.05 * _d[i]) * w;
                double sy = Wrap(_y[i] - DriftY * 0.05 * _d[i]) * h;
                int slot = _ringSlot[i];
                bool onRing = false; // Held on a ring: it still reaches for the focus, but isn't pulled off.
                if (_ring > 0 && slot >= 0)
                {
                    // Gathering in turn rather than all at once, each to its place on one of two rings.
                    double gather = Math.Max(0, Math.Min(1, _ring * 1.7 - (slot % 9) * 0.07));
                    gather = gather * gather * (3 - 2 * gather);
                    if (gather > 0)
                    {
                        double a = (double)slot / _ringCount * 2 * Math.PI + _denseSpin;
                        double shape = ((slot & 1) == 0 ? 1 : 0.955) * (1 + 0.03 * Math.Sin(5 * a + _spin * 0.9) + 0.015 * Math.Sin(13 * a - _spin * 1.4));
                        double tx = _ringCentre.X + Math.Cos(a) * _ringRadius * shape, ty = _ringCentre.Y + Math.Sin(a) * _ringRadius * shape;
                        if (_travel < 1)
                        {
                            // From where it was to its place on this ring, in turn, swooping a little.
                            double q = Math.Max(0, Math.Min(1, _travel * 1.6 - (slot % 9) * 0.07));
                            q = q * q * (3 - 2 * q);
                            double fx = _fromX[slot], fy = _fromY[slot];
                            double dx = tx - fx, dy = ty - fy, dl = Math.Sqrt(dx * dx + dy * dy);
                            double swoop = dl > 1 ? Math.Sin(Math.PI * q) * dl * (0.1 + 0.08 * Math.Sin(slot * 1.7)) / dl : 0;
                            tx = fx + dx * q - dy * swoop;
                            ty = fy + dy * q + dx * swoop;
                        }
                        _atX[slot] = tx;
                        _atY[slot] = ty;
                        sx += (tx - sx) * gather;
                        sy += (ty - sy) * gather;
                        _ringX[slot] = sx;
                        _ringY[slot] = sy;
                        _ringP[slot] = gather;
                        if (gather > 0.5)
                        {
                            double big = _d[i] * 1.5 + 1.2 * gather;
                            dc.DrawEllipse(_dots[Level(0.3 + 0.6 * gather)], null, new Point(sx, sy), big, big);
                            onRing = true;
                        }
                    }
                    else
                    {
                        // Not set off yet: if the ring moves, it leaves from where it is.
                        _atX[slot] = sx;
                        _atY[slot] = sy;
                    }
                }
                if (!onRing && !Double.IsNaN(_softAngle[i]) && _softG[i] > 0)
                {
                    // The loose ring: the circle's own edge, barely rippling.
                    double g = _softG[i];
                    g = g * g * (3 - 2 * g);
                    double a = _softAngle[i] + _softSpin;
                    bool leaving = _leaving.Contains(i);
                    double cx = leaving ? _leaveX[i] : _softCentre.X, cy = leaving ? _leaveY[i] : _softCentre.Y;
                    double r = (leaving ? _leaveR[i] : _softRadius) * (1 + 0.012 * Math.Sin(3 * a + _spin * 0.5));
                    sx += (cx + Math.Cos(a) * r - sx) * g;
                    sy += (cy + Math.Sin(a) * r - sy) * g;
                    _softX[i] = sx;
                    _softY[i] = sy;
                    if (g > 0.5)
                    {
                        double mid = _d[i] * 1.5 + 0.7 * g;
                        dc.DrawEllipse(_dots[Level(0.25 + 0.4 * g)], null, new Point(sx, sy), mid, mid);
                        onRing = true;
                    }
                }
                double gap, pull = 0;
                Point edge = EdgeNear(sx, sy, out gap);
                gap = Math.Max(0, gap);
                if (gap < reach && !onRing && fenceStrength > 0)
                {
                    double ox = sx - fence.X, oy = sy - fence.Y, beyond = Math.Sqrt(ox * ox + oy * oy) - fenceRadius * 0.96;
                    if (beyond > 0)
                        gap += reach * fenceStrength * Math.Min(1, beyond / 24); // Out past the ring: out of reach.
                }
                if (gap < reach)
                {
                    pull = 1 - gap / reach;
                    if (!onRing)
                    {
                        sx += (edge.X - sx) * pull * lean;
                        sy += (edge.Y - sy) * pull * lean;
                    }
                    _nearI[near] = i;
                    _nearX[near] = sx;
                    _nearY[near] = sy;
                    _nearP[near] = pull;
                    _edgeX[near] = edge.X;
                    _edgeY[near] = edge.Y;
                    near++;
                }
                if (onRing)
                    continue;
                if (Double.IsNaN(_softAngle[i]) && !(_ring > 0.3 && slot >= 0) && _softRadius > 0)
                {
                    // The best free dot to fill out the loose ring with: the one nearest its edge.
                    double cx = sx - _softCentre.X, cy = sy - _softCentre.Y, off = Math.Abs(Math.Sqrt(cx * cx + cy * cy) - _softRadius);
                    if (_fillDot < 0 || off < _fillGap)
                    {
                        _fillDot = i;
                        _fillGap = off;
                        _fillX = sx;
                        _fillY = sy;
                    }
                }
                double radius = _d[i] * 1.5 + pull * 1.4;
                dc.DrawEllipse(_dots[Level(0.15 + _d[i] * 0.5 + pull * 0.5)], null, new Point(sx, sy), radius, radius);
            }
            // Too few to make out the circle: now and then the free dot nearest its edge drifts over too.
            if (SoftRinging && Object.Equals(SoftKey, _softKey) && _softMembers.Count > 0 && _softMembers.Count < MinSoft && ++_softFill % 4 == 0 && _fillDot >= 0)
                JoinSoft(_fillDot, _fillX, _fillY);
            // An orb hovered in the loose ring's circle hands it the dots it reached for.
            if (SoftRinging && Object.Equals(SoftKey, _softKey) && Wrapping && _arrived > 0.5)
            {
                for (i = 0; i < near && _softMembers.Count < MaxSoft; i++)
                {
                    int d = _nearI[i];
                    if (Double.IsNaN(_softAngle[d]) && !(_ring > 0.3 && _ringSlot[d] >= 0))
                        JoinSoft(d, _nearX[i], _nearY[i]);
                }
            }
            // Neighbours on the loose ring link up where they're close, leaving gaps where they aren't.
            for (int m = 0; m < _softMembers.Count && _softMembers.Count > 1; m++)
            {
                int d = _softMembers[m], e = _softMembers[(m + 1) % _softMembers.Count];
                double apart = Turn(_softAngle[e] - _softAngle[d]);
                double p = Math.Min(_softG[d], _softG[e]) * _softLines;
                if (apart < 0.75 && p > 0.05 && !(_ring > 0.3 && (_ringSlot[d] >= 0 || _ringSlot[e] >= 0)))
                    dc.DrawLine(_between[Level(p * 0.65)], new Point(_softX[d], _softY[d]), new Point(_softX[e], _softY[e]));
            }
            if (_arrived > 0)
            {
                Rect halo = new Rect(_focus.X - _halfWidth - 7, _focus.Y - _halfHeight - 7, 2 * _halfWidth + 14, 2 * _halfHeight + 14);
                dc.DrawRoundedRectangle(null, _haloPens[Level(_arrived * 0.9)], halo, _corner + 7, _corner + 7);
            }

            // Spokes: from the title's edge to each of its circle's orbs, with a small light running out
            // along each, and a thin ring round the orb.
            for (i = 0; i < _spokeCentres.Count && _spoke > 0; i++)
            {
                Point c = _spokeCentres[i];
                double r = _spokeRadii[i] + 5;
                // The nearest point of the title's box, not of the pointer's effect, so they stay together.
                Point from = new Point(Math.Max(_spokeFrom.Left, Math.Min(_spokeFrom.Right, c.X)), Math.Max(_spokeFrom.Top, Math.Min(_spokeFrom.Bottom, c.Y)));
                double dx = c.X - from.X, dy = c.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= r + 2)
                    continue;
                Point to = new Point(c.X - dx / length * r, c.Y - dy / length * r);
                dc.DrawLine(_wrapPens[Level(_spoke * 0.55)], from, to);
                dc.DrawEllipse(null, _wrapPens[Level(_spoke * 0.75)], c, r, r);
                double run = (_spin * 0.7 + i * 0.37) % 1.0;
                Point light = new Point(from.X + (to.X - from.X) * run, from.Y + (to.Y - from.Y) * run);
                dc.DrawEllipse(_dots[Level(_spoke * 0.9)], null, light, 2.2, 2.2);
            }

            // A lit title: its glow, and lines up to it from the ring's dots close by.
            if (_badge > 0)
            {
                Rect b = _badgeBox;
                dc.DrawRoundedRectangle(null, _haloPens[Level(_badge * 0.8)], b, b.Height / 2, b.Height / 2);
                for (i = 0; i < _ringCount; i++)
                {
                    if (_ringP[i] < 0.5)
                        continue;
                    Point from = new Point(Math.Max(b.Left, Math.Min(b.Right, _ringX[i])), Math.Max(b.Top, Math.Min(b.Bottom, _ringY[i])));
                    double gx = _ringX[i] - from.X, gy = _ringY[i] - from.Y, gap = Math.Sqrt(gx * gx + gy * gy);
                    if (gap < 170)
                        dc.DrawLine(_wrapPens[Level((1 - gap / 170) * _badge * _ringP[i] * 0.85)], from, new Point(_ringX[i], _ringY[i]));
                }
            }

            // The ring's dots link to their neighbours on both rings, so it closes into a net.
            if (_ring > 0)
            {
                for (i = 0; i < _ringCount; i++)
                {
                    for (j = 1; j <= 2; j++)
                    {
                        int k = (i + j) % _ringCount;
                        double p = Math.Min(_ringP[i], _ringP[k]) * _ringLines;
                        if (p > 0.04)
                            dc.DrawLine(j == 1 ? _between[Level(p * 0.95)] : _toPointer[Level(p * 0.7)], new Point(_ringX[i], _ringY[i]), new Point(_ringX[k], _ringY[k]));
                    }
                }
            }

            // Only dots near the focus link up, so this is a handful of lines, not every dot against every dot.
            Pen[] reaching = _arrived > 0.5 ? _wrapPens : _toPointer;
            double link = 110 + size * 0.6; // Round something big, longer links so the net closes.
            int linking = Math.Min(near, 70); // Dots linking to each other: enough for the net, never hundreds.
            for (i = 0; i < near; i++)
            {
                dc.DrawLine(reaching[Level(_nearP[i] * 0.9)], new Point(_nearX[i], _nearY[i]), new Point(_edgeX[i], _edgeY[i]));
                for (j = i + 1; j < linking; j++)
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
        private static readonly FontFamily StandardUiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily StandardDisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI");
        private static FontFamily UiFont = StandardUiFont, DisplayFont = StandardDisplayFont;
        private static double _textScale = 1;   // Text size, from Accessibility.
        private static bool _reduceMotion;      // Reduce motion, from Accessibility.
        private int _readingFont = -1, _textPercent = -1;

        /// <summary>The reading font and text size: everything is rebuilt in them.</summary>
        private void ApplyReading(int font, int percent)
        {
            _readingFont = font;
            _textPercent = percent;
            _textScale = Math.Max(1, Math.Min(1.3, percent / 100.0));
            UiFont = StandardUiFont;
            DisplayFont = StandardDisplayFont;
            AppFont chosen = ReadingFonts.At(font);
            string folder = ReadingFonts.Folder(chosen);
            if (folder != null)
            {
                // The built-in font, with Windows' as the fallback for anything it doesn't have.
                FontFamily family = new FontFamily(new Uri(folder.TrimEnd('\\') + "\\"), "./#" + chosen.Family + ", Segoe UI");
                UiFont = family;
                DisplayFont = family;
            }
            LetteringFace = new Typeface(folder != null ? UiFont : new FontFamily("Segoe UI Variable Display Light, Segoe UI Light, Segoe UI"),
                FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
            FontFamily = UiFont;
            _lastDock = null;
            RebuildScene(false);
            RefreshAll();
        }
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
        private SpatialVoiceLines _voiceLines;
        private SpatialNode _hoveredLabel; // A circle's title under the pointer, or just left.
        private bool _labelUnderPointer;
        private double _labelSeenAt;
        private const double LabelLinger = 0.35;
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

        private object _openRoomKey;        // The room being looked at, or null for the whole space.
        private int _openChannel;
        private string _openFriend;
        private bool _pendingFirstFit = true;

        private double _camX, _camY, _camZ, _toX, _toY, _toZ;
        private double _tiltX, _tiltY, _toTiltX, _toTiltY;
        private double _navShift = NavWidth, _toNavShift = NavWidth;
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
            // A little roomier to start with, but never bigger than most of the screen.
            Rect work = SystemParameters.WorkArea;
            Width = Math.Min(1440, work.Width * 0.92);
            Height = Math.Min(900, work.Height * 0.92);
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
                _labelUnderPointer = false; // A hovered title lets go after its short linger.
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
            if (state.ReadingFont != _readingFont || state.TextScale != _textPercent)
                ApplyReading(state.ReadingFont, state.TextScale);
            if (state.ReduceMotion != _reduceMotion)
            {
                _reduceMotion = state.ReduceMotion;
                _stars.Visibility = _reduceMotion ? Visibility.Hidden : Visibility.Visible;
                _dirty = true;
            }
            if (state.FrameRate != _frameRate)
            {
                _frameRate = state.FrameRate;
                if (_frameRate == 2)
                    WantRefresh(false);
            }
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
            if (state.LayoutKey != _lastLayout)
            {
                _lastLayout = state.LayoutKey;
                ApplyChannelLayout();
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
            _items.Clear();
            _items.AddRange(_rooms);
            _items.AddRange(_friends);
            LayoutGroups(_items, _groups, _state.Circles, 0);
        }

        /// <summary>Rooms and friends together, in one space of circles.</summary>
        private readonly List<SpatialItem> _items = new List<SpatialItem>();
        private readonly List<SpatialGroup> _groups = new List<SpatialGroup>();

        private List<SpatialGroup> Groups { get { return _groups; } }

        /// <summary>Which circle's members an item counts as: its own, its kind's home if that's gone, or none when free.</summary>
        private static bool InCircle(SpatialItem item, string id, List<SpatialCircle> circles)
        {
            string own = item.Circle;
            if (own == SpatialCircle.FreeId)
                return false;
            string home = item.Room != null ? SpatialCircle.MainId : SpatialCircle.FriendsId;
            return own == id || (id == home && !circles.Exists(delegate(SpatialCircle c) { return c.Id == own; }));
        }

        /// <summary>A circle's usual size for this many orbs.</summary>
        private static double AutoRadius(int count)
        {
            return 115 * Math.Sqrt(Math.Max(count, 1)) * 1.2 + 110;
        }

        /// <summary>
        /// Places every circle and orb. A circle is its usual size for its orbs, grown to take in
        /// any you've placed near its edge, up to twice that. Orbs you've placed sit where you put
        /// them (relative to their circle, so they move with it); the rest spiral out round them;
        /// orbs in no circle float where they were dropped.
        /// </summary>
        private static void LayoutGroups(List<SpatialItem> items, List<SpatialGroup> groups, List<SpatialCircle> circles, double originX)
        {
            groups.Clear();
            // First the sizes, so circles you haven't placed can go to the right of those you have.
            Dictionary<string, List<SpatialItem>> membersOf = new Dictionary<string, List<SpatialItem>>();
            Dictionary<string, double> autoOf = new Dictionary<string, double>(), radiusOf = new Dictionary<string, double>();
            foreach (SpatialCircle circle in circles)
            {
                string id = circle.Id;
                List<SpatialItem> members = items.FindAll(delegate(SpatialItem item) { return InCircle(item, id, circles); });
                double auto = AutoRadius(members.Count), need = auto;
                foreach (SpatialItem m in members)
                    if (m.Placed)
                        need = Math.Max(need, Math.Sqrt(m.PlaceX * m.PlaceX + m.PlaceY * m.PlaceY) + m.Radius + 40);
                membersOf[id] = members;
                autoOf[id] = auto;
                radiusOf[id] = Math.Min(need, 2 * auto);
            }

            double x = 0, y = 0, rowHeight = 0, top = 0;
            bool anyPlaced = false;
            foreach (SpatialCircle circle in circles)
            {
                if (!circle.HasPosition)
                    continue;
                double r = radiusOf[circle.Id];
                x = anyPlaced ? Math.Max(x, circle.X + r + 70) : circle.X + r + 70;
                top = anyPlaced ? Math.Min(top, circle.Y - r) : circle.Y - r;
                anyPlaced = true;
            }
            double startX = anyPlaced ? x : 0;
            y = anyPlaced ? top : 0;
            x = startX;
            foreach (SpatialCircle circle in circles)
            {
                SpatialGroup group = new SpatialGroup();
                group.Id = circle.Id;
                group.Title = (circle.Name ?? String.Empty).ToUpperInvariant();
                group.Origin = originX;
                group.Count = membersOf[circle.Id].Count;
                group.AutoRadius = autoOf[circle.Id];
                group.Radius = radiusOf[circle.Id];
                group.Placed = circle.HasPosition;
                if (circle.HasPosition)
                {
                    group.X = originX + circle.X;
                    group.Y = circle.Y;
                }
                else
                {
                    if (x > startX && x - startX + 2 * group.Radius > 2900)
                    {
                        x = startX;
                        y += rowHeight + 70;
                        rowHeight = 0;
                    }
                    group.X = originX + x + group.Radius;
                    group.Y = y + group.Radius;
                    x += 2 * group.Radius + 70;
                    rowHeight = Math.Max(rowHeight, 2 * group.Radius);
                }
                groups.Add(group);
            }

            // Two circles you've placed yourself don't grow into each other: the growing one stops short.
            foreach (SpatialGroup a in groups)
            {
                if (!a.Placed || a.Radius <= a.AutoRadius)
                    continue;
                foreach (SpatialGroup b in groups)
                {
                    if (b == a || !b.Placed)
                        continue;
                    double apart = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                    a.Radius = Math.Max(a.AutoRadius, Math.Min(a.Radius, apart - b.Radius - 20));
                }
            }

            foreach (SpatialGroup group in groups)
            {
                group.LabelY = group.Y - group.Radius - 34; // Just above the circle's top edge.
                List<SpatialItem> members = membersOf[group.Id];
                List<SpatialItem> settled = new List<SpatialItem>();
                foreach (SpatialItem m in members)
                {
                    m.Group = group;
                    m.Z = (Hash(m.Name) % 200) - 100;
                    if (!m.Placed)
                        continue;
                    // Kept inside, even if the circle couldn't grow all the way.
                    double d = Math.Sqrt(m.PlaceX * m.PlaceX + m.PlaceY * m.PlaceY), room = Math.Max(0, group.Radius - m.Radius - 20);
                    double k = d > room && d > 0 ? room / d : 1;
                    m.X = group.X + m.PlaceX * k;
                    m.Y = group.Y + m.PlaceY * k;
                    settled.Add(m);
                }
                int spot = 0;
                foreach (SpatialItem m in members)
                {
                    if (m.Placed)
                        continue;
                    // The next spot on the spiral that's clear of everything already there.
                    for (int tries = 0; tries < 80; tries++, spot++)
                    {
                        double angle = spot * 2.399963, reach = 115 * Math.Sqrt(spot + 0.35);
                        double px = group.X + Math.Cos(angle) * reach * 1.2, py = group.Y + 28 + Math.Sin(angle) * reach;
                        bool clear = true;
                        foreach (SpatialItem o in settled)
                        {
                            double min = m.Radius + o.Radius + 14;
                            if ((o.X - px) * (o.X - px) + (o.Y - py) * (o.Y - py) < min * min)
                            {
                                clear = false;
                                break;
                            }
                        }
                        if (clear || tries == 79)
                        {
                            m.X = px;
                            m.Y = py;
                            break;
                        }
                    }
                    spot++;
                    settled.Add(m);
                }
            }

            foreach (SpatialItem item in items)
            {
                if (item.Circle != SpatialCircle.FreeId)
                    continue;
                item.Group = null;
                item.X = item.PlaceX;
                item.Y = item.PlaceY;
                item.Z = (Hash(item.Name) % 200) - 100;
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

        private List<SpatialItem> Current { get { return _items; } }

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
            foreach (SpatialItem item in _items)
            {
                if (item.Group != null)
                    continue; // Floating in no circle: in view too.
                x0 = Math.Min(x0, item.X - 120); x1 = Math.Max(x1, item.X + 120);
                y0 = Math.Min(y0, item.Y - 120); y1 = Math.Max(y1, item.Y + 120);
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

        private int _frameRate;
        private double _refreshSeconds = 1 / 60.0; // How long one refresh of this screen lasts, measured.
        private bool _skipRefresh;

        private void WantRefresh(bool want)
        {
            if (_frameRate == 2)
                want = false; // Light: the 30-a-second timer draws everything.
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
                double gap = (args.RenderingTime - _lastRefreshTime).TotalSeconds;
                if (gap > 0.002 && gap < 0.05)
                    _refreshSeconds += (gap - _refreshSeconds) * 0.05;
                _lastRefreshTime = args.RenderingTime;
            }
            // Balanced: on a screen over 100 Hz, every other refresh, so each frame shows for
            // exactly two and motion stays evenly paced at half the work.
            if (_frameRate == 1 && _refreshSeconds < 0.01)
            {
                _skipRefresh = !_skipRefresh;
                if (_skipRefresh)
                    return;
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
            if (_hoveredLabel != null && !_labelUnderPointer && now - _labelSeenAt > LabelLinger)
                _hoveredLabel = null;
            moving |= StepVoices(dt);
            moving |= StepGlides(dt);
            moving |= StepTuck(dt);
            moving |= _stars.RingBusy;
            moving |= StepCircleGlow(dt, now);
            if (_voiceLines != null)
                _voiceLines.Tick(now);
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
                if (_cardIsPreview && _pressed != null && _panMoved)
                {
                    // Picked up: the preview goes at once, so the person being dragged shows.
                    RemoveCard();
                    _cardFor = 0;
                }
                PreviewCards(onBall && target != null && target.Person != null && !(_pressed != null && _panMoved) ? target : null, now);
                _stars.Wrapping = false;
                _stars.Locked = false;
                // A circle hovered or being dragged gathers a ring of dots.
                SpatialNode ringed = _pressed != null && _pressed.IsGroupLabel && _panMoved ? _pressed : null;
                // Hovering a title (not dragging it): lines out to each orb in its circle instead.
                _stars.Spoking = ringed == null && _hoveredLabel != null && _openRoomKey == null && !(_pressed != null && _panMoved);
                if (_stars.Spoking)
                    _spokeCircle = _hoveredLabel.Group.Id;
                else if (!_stars.SpokesShowing)
                    _spokeCircle = null;
                _stars.SpokeCentres.Clear();
                _stars.SpokeRadii.Clear();
                // While they fade out too, so they stay on their orbs as the camera moves.
                SpatialNode spokeTitle = _spokeCircle == null ? null : TitleOf(_spokeCircle);
                if (spokeTitle != null && spokeTitle.Element.ActualWidth > 0)
                {
                    FrameworkElement t = spokeTitle.Element;
                    _stars.SpokeFrom = t.TransformToAncestor(_root).TransformBounds(new Rect(-6, -2, t.ActualWidth + 12, t.ActualHeight + 4));
                    foreach (SpatialNode n in _nodes)
                    {
                        if (n.Item == null || n.Item.Group == null || n.Item.Group.Id != _spokeCircle || !n.OnScreen)
                            continue;
                        _stars.SpokeCentres.Add(new Point(n.ScreenX, n.ScreenY));
                        _stars.SpokeRadii.Add(n.WrapRadius * n.ScreenScale * BallSize(n, now));
                    }
                }
                if (_pressed != null && _panMoved && _pressed.Item != null)
                {
                    // A room or friend being dragged: the circle it's over, if it can go there.
                    double grownTo;
                    SpatialGroup over = DropCircleFor(_pressed.Item, _pressed.X, _pressed.Y, _pressed.WrapRadius * _pressed.SizeScale, out grownTo);
                    _dropRadius = grownTo;
                    ringed = null;
                    if (over != null)
                    {
                        foreach (SpatialNode n in _nodes)
                        {
                            if (n.RingNode != null && n.Group == over)
                            {
                                ringed = n;
                                break;
                            }
                        }
                    }
                }
                if (ringed == null && (_pressed == null || !_panMoved) && _openRoomKey == null)
                    ringed = ZoomedCircle();
                // Whenever the full ring is round a circle (zoomed in, or something dragged into it), its
                // title stays lit with the ring reaching up to it, while the pointer keeps its own effect.
                // Hovered or held, the title has the full effect anyway.
                _zoomedTitle = ringed != null && ringed.RingNode != null ? ringed : null;
                _stars.Badge = _zoomedTitle != null && _zoomedTitle.OnScreen && _zoomedTitle != _hoveredLabel && _zoomedTitle != _pressed && _zoomedTitle.Element.ActualWidth > 0;
                if (_stars.Badge)
                {
                    FrameworkElement t = _zoomedTitle.Element;
                    _stars.BadgeBox = t.TransformToAncestor(_root).TransformBounds(new Rect(-14, -6, t.ActualWidth + 28, t.ActualHeight + 12));
                }

                // The loose ring: an orb hovered on the main screen starts it round its circle, and it
                // stays while the pointer is inside that circle.
                bool dragging = _pressed != null && _panMoved;
                if (_openRoomKey == null && !dragging && onBall && target != null && target.Item != null && target.Item.Group != null)
                    _softCircle = target.Item.Group.Id;
                SpatialNode softTitle = _softCircle == null || _openRoomKey != null || dragging ? null : TitleOf(_softCircle);
                if (softTitle != null)
                {
                    double r = softTitle.Group.Radius * softTitle.RingNode.ScreenScale;
                    double px = _stars.Pointer.X - softTitle.RingNode.ScreenX, py = _stars.Pointer.Y - softTitle.RingNode.ScreenY;
                    if (!softTitle.RingNode.OnScreen || px * px + py * py > r * r)
                        softTitle = null;
                }
                if (softTitle == null)
                    _softCircle = null;
                _stars.SoftRinging = softTitle != null;
                if (softTitle != null)
                {
                    _stars.SoftKey = _softCircle;
                    _stars.SoftCentre = new Point(softTitle.RingNode.ScreenX, softTitle.RingNode.ScreenY);
                    _stars.SoftRadius = softTitle.Group.Radius * softTitle.RingNode.ScreenScale;
                }
                _stars.Ringing = ringed != null && ringed.RingNode != null;
                if (_stars.Ringing)
                {
                    _stars.RingKey = ringed.Group.Id;
                    _stars.RingCentre = new Point(ringed.RingNode.ScreenX, ringed.RingNode.ScreenY);
                    _stars.RingRadius = (_pressed != null && _panMoved && _pressed.Item != null ? _dropRadius : ringed.Group.Radius) * ringed.RingNode.ScreenScale;
                }
                _stars.RingChase = false;
                if (_stars.Ringing && _pressed != null && _panMoved && _pressed.Item != null)
                    _carriedRadius = _stars.RingRadius / Math.Max(0.001, ringed.RingNode.ScreenScale);
                else if (_pressed != null && _panMoved && _pressed.Item != null && _pressed.OnScreen && _carriedRadius > 0)
                {
                    // Pulled out into open space: the whole ring follows the orb, keeping its shape,
                    // until it catches up and breaks away, or the orb is let go.
                    _stars.Ringing = true;
                    _stars.RingChase = true;
                    _stars.RingKey = "orb";
                    _stars.RingCentre = new Point(_pressed.ScreenX, _pressed.ScreenY);
                    _stars.RingRadius = _carriedRadius * _pressed.ScreenScale;
                }
                if (_pressed == null || !_panMoved)
                    _carriedRadius = 0;
                // While its title is being dragged the pointer is captured, so hovering doesn't see it.
                // Just left it, the title's lines and glow linger but the pointer's effect goes with the pointer.
                SpatialNode titleHeld = _pressed != null && _pressed.IsGroupLabel && _panMoved ? _pressed : (_labelUnderPointer ? _hoveredLabel : null);
                SpatialNode dragged = _pressed != null && _panMoved && _pressed.BallScale != null && (_pressed.Item != null || _draggingPerson || _draggingChannel) ? _pressed : null;
                if (_card != null && _card.IsVisible && _card.ActualWidth > 0)
                {
                    // An open profile card is highlighted, following it as it grows in.
                    Rect box = _card.TransformToAncestor(_root).TransformBounds(new Rect(0, 0, _card.ActualWidth, _card.ActualHeight));
                    _stars.WrapBox(box, 16 * box.Width / _card.ActualWidth);
                    onBall = false;
                }
                else if (dragged != null && dragged.OnScreen)
                {
                    // Something being dragged keeps its highlight locked on, while the lines reach for the dots it passes.
                    _stars.WrapBall(new Point(dragged.ScreenX, dragged.ScreenY), dragged.WrapRadius * dragged.ScreenScale * BallSize(dragged, now));
                    _stars.Locked = true;
                    target = dragged;
                    onBall = true;
                }
                else if (titleHeld != null && titleHeld.OnScreen && titleHeld.Element.ActualWidth > 0)
                {
                    // A circle's title: the lines wrap it like a ball, as a rounded box. Dragged, it stays
                    // locked on, so the ring's lines keep reaching it as it moves.
                    FrameworkElement t = titleHeld.Element;
                    Rect box = t.TransformToAncestor(_root).TransformBounds(new Rect(-14, -6, t.ActualWidth + 28, t.ActualHeight + 12));
                    _stars.WrapBox(box, box.Height / 2);
                    _stars.Locked = titleHeld == _pressed;
                    onBall = false;
                }
                else if (onBall)
                {
                    // It moves when the camera does, and grows, so follow it every frame.
                    _stars.WrapBall(new Point(target.ScreenX, target.ScreenY), target.WrapRadius * target.ScreenScale * BallSize(target, now));
                }
                if (_reduceMotion)
                {
                    _stars.Wrapping = false;
                    _stars.Ringing = false;
                    _stars.SoftRinging = false;
                    _stars.Spoking = false;
                    _stars.Badge = false;
                    onBall = false; // Nothing grows or breathes.
                }
                else
                    _stars.Step(dt);

                // The targeted ball grows as the point arrives, then breathes; others ease back.
                foreach (SpatialNode node in _nodes)
                {
                    if (node.BallScale == null)
                        continue;
                    double want = onBall && node == target ? _stars.Arrived : (node == _dragTarget ? 1 : 0);
                    node.Grow += (want - node.Grow) * Math.Min(1, dt * 6);
                    if (want == 0 && node.Grow < 0.002)
                        node.Grow = 0;
                    if (node.PatternTurn != null && node.Grow > 0.01)
                        node.PatternTurn.Angle += dt * 16 * node.Grow;
                    double size = BallSize(node, now);
                    if (Math.Abs(node.BallScale.ScaleX - size) > 0.0005)
                    {
                        node.BallScale.ScaleX = size;
                        node.BallScale.ScaleY = size;
                        if (node.LabelMove != null)
                            node.LabelMove.Y = Math.Round(node.LabelDrop * (size - 1) * 2) / 2; // The ball's edge, in half pixels.
                    }
                    if (node.Grow > 0)
                        moving = true;
                }
            }
            if (_cardEffect != null)
                _cardEffect.Tick(now);
            if (_cardDeco != null)
                _cardDeco.Tick(now);
            foreach (SpatialDecoRing deco in _sceneDecos)
                deco.Tick(now);
            foreach (SpatialDecoRing deco in _dockDecos)
                deco.Tick(now);
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
            // Hover growth and breathing, then bigger with their voice: about a third for normal
            // talking, and much more for shouting.
            return node.SizeScale * (1 + node.Grow * (0.08 + 0.025 * Math.Sin(now * 2 * Math.PI / 3))) * (1 + 0.32 * Talk(node.Voice) + 0.5 * Shout(node.Voice));
        }

        private double _roomRx = 1, _roomRy = 1; // The usual ellipse channels sit on, in this room.
        private double _tuck, _tuckTo;            // 0 to 1: how far the channels are pulled into the room's ball.
        private SpatialNode _lastChannelClick;    // For telling a double click on a channel from a single one.
        private double _lastChannelClickAt;

        /// <summary>Pulls channels in or lets them out, and swells the room's ball. True while it moves.</summary>
        private bool StepTuck(double dt)
        {
            if (_reduceMotion)
                _tuck = _tuckTo; // Straight there.
            if (_tuck == _tuckTo && (_roomNode == null || _roomNode.SizeScale == 1 + 0.16 * Smooth(_tuck)))
                return false;
            _tuck += (_tuckTo - _tuck) * Math.Min(1, dt * 6);
            if (Math.Abs(_tuckTo - _tuck) < 0.002)
                _tuck = _tuckTo;
            if (_roomNode != null)
                _roomNode.SizeScale = 1 + 0.16 * Smooth(_tuck);
            _dirty = true;
            return true;
        }

        private static double Smooth(double t)
        {
            return t * t * (3 - 2 * t);
        }

        /// <summary>Lights a circle's title in the highlight colour while it's hovered. True while anything is lit.</summary>
        private bool StepCircleGlow(double dt, double now)
        {
            bool lit = false;
            foreach (SpatialNode n in _nodes)
            {
                if (n.Ring == null)
                    continue;
                double want = n == _hoveredLabel || n == _zoomedTitle ? 1 : 0;
                if (n.Glow == want && want == 0)
                    continue;
                n.Glow += (want - n.Glow) * Math.Min(1, dt * (want > n.Glow ? 8 : 5));
                if (want == 0 && n.Glow < 0.01)
                    n.Glow = 0;
                if (n.TitleText != null)
                    n.TitleText.Foreground = new SolidColorBrush(Mix(Text2, AccentCol, n.Glow));
                lit = true;
            }
            return lit;
        }
        private bool _draggingChannel;
        private int _lastLayout = -1;

        /// <summary>Where someone sits beside their channel: a column on its outer side, further out on a big one.</summary>
        private static Point PersonSpot(SpatialNode channel, double mid)
        {
            double dir = Math.Cos(channel.Angle) >= 0 ? 1 : -1;
            return new Point(channel.X + dir * (92 + 35 * (channel.SizeScale - 1) - Math.Abs(mid) * 14), channel.Y + mid * 66);
        }

        /// <summary>Puts a channel, and the people in it, somewhere new. Gliding, they ease over from where they were.</summary>
        private void MoveChannel(SpatialNode node, double angle, double reach, double size, bool glide)
        {
            if (_roomNode == null)
                return;
            double x = _roomNode.X + Math.Cos(angle) * _roomRx * reach, y = _roomNode.Y + Math.Sin(angle) * _roomRy * reach;
            if (glide)
            {
                node.GlideX += node.X - x;
                node.GlideY += node.Y - y;
            }
            node.X = x;
            node.Y = y;
            node.Angle = angle;
            node.Reach = reach;
            node.SizeScale = size;
            foreach (SpatialNode person in _nodes)
            {
                if (person.Channel != node || person == _pressed)
                    continue;
                Point spot = PersonSpot(node, person.Mid);
                if (glide)
                {
                    person.GlideX += person.X - spot.X;
                    person.GlideY += person.Y - spot.Y;
                }
                person.X = spot.X;
                person.FromX = spot.X;
                person.Y = spot.Y;
                person.FromY = spot.Y;
            }
            _dirty = true;
        }

        /// <summary>The host moved or resized channels: everything glides to the new places.</summary>
        private void ApplyChannelLayout()
        {
            if (_roomNode == null)
                return;
            foreach (SpatialNode node in _nodes.ToArray())
            {
                if (node.ChannelId <= 0 || node.Person != null || (_draggingChannel && node == _pressed))
                    continue;
                SpatialRoomChannel channel = FindChannel(node.ChannelId);
                if (channel == null)
                    continue;
                double angle = channel.HasLayout ? channel.Angle : node.HomeAngle;
                double reach = channel.HasLayout ? channel.Reach : 1, size = channel.HasLayout ? channel.Size : 1;
                if (angle != node.Angle || reach != node.Reach || size != node.SizeScale)
                    MoveChannel(node, angle, reach, size, true);
            }
        }

        /// <summary>Eases moved things the rest of the way. True while anything is still catching up.</summary>
        private bool StepGlides(double dt)
        {
            bool moving = false;
            double keep = Math.Exp(-dt * 9);
            foreach (SpatialNode n in _nodes)
            {
                if (n.GlideX == 0 && n.GlideY == 0)
                    continue;
                n.GlideX *= keep;
                n.GlideY *= keep;
                if (Math.Abs(n.GlideX) < 0.05 && Math.Abs(n.GlideY) < 0.05)
                {
                    n.GlideX = 0;
                    n.GlideY = 0;
                }
                moving = true;
            }
            return moving;
        }

        /// <summary>The room's own chat: the channel you're in, else its first text channel, else its first channel.</summary>
        private void OpenMainChannel()
        {
            int id = _state.LocalChannel;
            if (id <= 0 || FindChannel(id) == null)
            {
                id = 0;
                foreach (SpatialRoomChannel channel in _state.Channels)
                {
                    if (channel.IsText)
                    {
                        id = channel.Id;
                        break;
                    }
                }
                if (id == 0 && _state.Channels.Count > 0)
                    id = _state.Channels[0].Id;
            }
            if (id > 0)
                OpenChannel(id);
        }

        /// <summary>Voice up to normal talking, 0 to 1.</summary>
        private static double Talk(double voice) { return Math.Min(1, voice); }

        /// <summary>How far past normal talking, 0 to 1 at a shout.</summary>
        private static double Shout(double voice) { return Math.Max(0, voice - 1);
        }

        /// <summary>
        /// Follows everyone's voice: quick to rise, slower to settle, so words don't flicker. The
        /// loud push their neighbours in the channel aside a little. True while anything moves.
        /// </summary>
        private bool StepVoices(double dt)
        {
            if (_voiceLines == null || _voiceLines.People.Count == 0)
                return false;
            bool moving = false;
            foreach (SpatialNode n in _voiceLines.People)
            {
                SpatialPerson p = n.Person;
                double want = p.Muted || p.Deafened || _reduceMotion ? 0 : _host.SpatialVoiceLevel(p.Id);
                double rate = want > n.Voice ? 22 : 5;
                n.Voice += (want - n.Voice) * Math.Min(1, dt * rate);
                if (n.Voice < 0.002)
                    n.Voice = 0;
            }
            foreach (SpatialNode n in _voiceLines.People)
            {
                double px = 0, py = 0;
                if (n.Channel != null && n.Voice > 0)
                {
                    // Their own wave pushes them out along it, further the bigger it is.
                    double ox = n.X - n.Channel.X, oy = n.Y - n.Channel.Y, od = Math.Sqrt(ox * ox + oy * oy);
                    if (od > 1)
                    {
                        double out_ = Talk(n.Voice) * 12 + Shout(n.Voice) * 34;
                        px += ox / od * out_;
                        py += oy / od * out_;
                    }
                }
                foreach (SpatialNode other in _voiceLines.People)
                {
                    if (other == n || other.Channel != n.Channel || other.Voice <= 0)
                        continue;
                    double dx = n.X - other.X, dy = n.Y - other.Y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1 || d > 160)
                        continue;
                    double push = (Talk(other.Voice) * 16 + Shout(other.Voice) * 30) * (1 - d / 160);
                    px += dx / d * push;
                    py += dy / d * push;
                }
                double blend = Math.Min(1, dt * 8);
                n.PushX += (px - n.PushX) * blend;
                n.PushY += (py - n.PushY) * blend;
                if (Math.Abs(n.PushX) < 0.01 && px == 0) n.PushX = 0;
                if (Math.Abs(n.PushY) < 0.01 && py == 0) n.PushY = 0;
                if (n.Voice > 0 || n.PushX != 0 || n.PushY != 0)
                    moving = true;
            }
            return moving;
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
                double x = n.FromX + (n.X - n.FromX) * p + n.PushX + n.GlideX, y = n.FromY + (n.Y - n.FromY) * p + n.PushY + n.GlideY, z = n.FromZ + (n.Z - n.FromZ) * p;
                double tuck = n.Tuckable && _roomNode != null ? Smooth(_tuck) : 0;
                if (tuck > 0)
                {
                    double ox = x - _roomNode.X, oy = y - _roomNode.Y, turn = 1.3 * tuck, keep = 1 - tuck;
                    x = _roomNode.X + (ox * Math.Cos(turn) - oy * Math.Sin(turn)) * keep;
                    y = _roomNode.Y + (ox * Math.Sin(turn) + oy * Math.Cos(turn)) * keep;
                    z = _roomNode.Z + (z - _roomNode.Z) * keep;
                }
                double depth = Perspective - (z + _camZ);
                bool visible = depth > 60 && n.Progress > 0 && tuck < 0.97;
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
                double shown = s * Math.Max(0.001, p) * (1 - tuck);
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
            _sceneDecos.Clear();
            // Under everything, so lines run between the balls rather than over them.
            _voiceLines = new SpatialVoiceLines();
            _voiceLines.Colour = AccentCol;
            _voiceLines.RadiusOf = delegate(SpatialNode n) { return n.WrapRadius * n.ScreenScale * BallSize(n, _clock.Elapsed.TotalSeconds); };
            Panel.SetZIndex(_voiceLines, -100000);
            _world.Children.Add(_voiceLines);
            _hovered = null;
            string hoveredCircle = _hoveredLabel != null && _hoveredLabel.Group != null ? _hoveredLabel.Group.Id : null;
            _hoveredLabel = null;
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
                {
                    // Pulls the channels in, or lets them out. Right-click still has the room's options.
                    _roomNode.Click = delegate { _tuckTo = _tuckTo > 0 ? 0 : 1; };
                }
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
            if (hoveredCircle != null)
                _hoveredLabel = TitleOf(hoveredCircle); // The scene was rebuilt under the pointer: still hovered.
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
            // Not drawn: the circle shows as its title, and dots gather round it when it's hovered or dragged.
            ring.Opacity = 0;

            Border label = new Border();
            label.Background = Brushes.Transparent;
            label.Cursor = Cursors.Hand;
            label.ToolTip = "Drag to move this circle. Click to zoom to it. Right-click to rename or delete it.";
            label.Child = Label(group.Title, 30, Text2, true);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // At the circle's own depth, so the camera's perspective moves them together and the
            // title stays centred over it from anywhere; drawn above what's in front, though.
            SpatialNode title = AddNode(label, label.DesiredSize.Width / 2, label.DesiredSize.Height / 2, group.X, group.LabelY, -140);
            Panel.SetZIndex(label, 200);
            title.Click = delegate { FocusGroup(group); };
            title.RightClick = delegate { _host.SpatialCircleMenu(group.Id); };
            title.Ring = ring;
            title.RingNode = circle;
            title.TitleText = (TextBlock)label.Child;
            title.Group = group;
            title.IsGroupLabel = true;

            if (group.Count == 0)
            {
                TextBlock hint = Label("Drag rooms or friends here", 15, Text2, false);
                hint.IsHitTestVisible = false;
                hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // At the circle's depth and its centre, like its title, so it stays centred from any angle.
                SpatialNode text = AddNode(hint, hint.DesiredSize.Width / 2, hint.DesiredSize.Height / 2, group.X, group.Y, -140);
                Panel.SetZIndex(hint, 200);
                text.Group = group;

                // Or add something new: the "+ New" menu.
                Border add = new Border();
                add.Width = add.Height = 40;
                add.CornerRadius = new CornerRadius(20);
                add.BorderThickness = new Thickness(1.5);
                add.BorderBrush = new SolidColorBrush(Fade(AccentCol, 0x90));
                add.Background = new SolidColorBrush(Fade(SolidBg, 0xC0));
                add.Cursor = Cursors.Hand;
                add.ToolTip = "Host, join or add something new";
                add.Child = CentredLabel("+", 22, AccentCol);
                SpatialNode plus = AddNode(add, 20, 20, group.X, group.Y + 46, -140);
                Panel.SetZIndex(add, 200);
                plus.Group = group;
                plus.Click = delegate { ShowNewMenu(null); };
            }
        }

        private void AddItemNode(SpatialItem item)
        {
            bool isRoom = item.Room != null;
            double size = isRoom ? 92 : 78;
            Grid box = new Grid();
            box.Width = 170;
            box.Height = size + 104; // Room for the name to move down as the ball grows.
            box.Background = Brushes.Transparent;
            double left = (170 - size) / 2, top = 28;
            bool active = isRoom && item.Room.Active;
            bool lit = isRoom ? (item.Room.Online || active) : (item.Friend.Status != "Offline" && item.Friend.IsFriend);
            // How it's lit says who's there, instead of a status dot: online glows in its own colour,
            // away more softly in amber, busy in red; offline is switched off.
            string status = isRoom ? (lit ? "Online" : "Offline") : (item.Friend.IsFriend ? item.Friend.Status : "Offline");
            Color light = status == "Away" ? Warm : status == "Busy" ? Bad : item.Color;
            byte glowAlpha = !lit ? (byte)0 : status == "Away" ? (byte)0x60 : (byte)0xA8;

            // A soft glow, painted rather than computed by an effect so it costs nothing to move.
            Ellipse glow = TopLeft(new Ellipse(), left - 30, top - 30);
            glow.Width = size + 60;
            glow.Height = size + 60;
            RadialGradientBrush glowBrush = new RadialGradientBrush();
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(glowAlpha, light.R, light.G, light.B), 0.55));
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 1));
            glow.Fill = glowBrush;
            box.Children.Add(glow);

            Ellipse orb = TopLeft(new Ellipse(), left, top);
            orb.Width = size;
            orb.Height = size;
            ImageSource picture = isRoom ? item.Room.Picture : null;
            // A picture, or the pattern its name makes.
            RotateTransform turn = null;
            if (picture != null)
                orb.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            else
            {
                turn = new RotateTransform(0, 0.5, 0.5);
                orb.Fill = new ImageBrush(OrbPattern(item.Name, item.Color)) { RelativeTransform = turn }; // The same pattern, lit or not.
            }
            box.Children.Add(orb);
            if (lit)
            {
                // Lit from inside.
                Ellipse inner = TopLeft(new Ellipse(), left, top);
                inner.Width = inner.Height = size;
                RadialGradientBrush glowing = new RadialGradientBrush();
                glowing.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 255, 255, 255), 0));
                glowing.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, light.R, light.G, light.B), 0.6));
                glowing.GradientStops.Add(new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 1));
                inner.Fill = glowing;
                inner.IsHitTestVisible = false;
                box.Children.Add(inner);
            }
            AddGlass(box, left, top, size, lit ? Mix(light, AccentCol, 0.3) : Color.FromRgb(70, 72, 86));

            if (active)
            {
                Ellipse ring = TopLeft(new Ellipse(), left - 11, top - 11);
                ring.Width = size + 22;
                ring.Height = size + 22;
                ring.Stroke = new SolidColorBrush(AccentCol); // The room you're in, in your highlight colour.
                ring.StrokeThickness = 1.5;
                box.Children.Add(ring);
            }

            if (picture == null)
            {
                // Light lettering floating in the glass.
                box.Children.Add(Lettering(isRoom ? Initials(item.Name) : Initials(item.Name).Substring(0, 1), isRoom ? 24 : 22, left + size / 2, top + size / 2));
            }
            if (!lit)
            {
                // Switched off: a dark veil over the whole ball, picture or pattern still showing through.
                Ellipse veil = TopLeft(new Ellipse(), left, top);
                veil.Width = veil.Height = size;
                veil.Fill = new SolidColorBrush(Color.FromArgb(0x8C, 6, 6, 14));
                veil.IsHitTestVisible = false;
                box.Children.Add(veil);
            }

            if (!isRoom)
            {
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
            // Below the ball, and below the ring round the room you're in, which is wider.
            double below = active ? 18 : 8;
            name.Margin = new Thickness(0, top + size + below, 0, 0);
            box.Children.Add(name);
            _mainLabels.Add(name);

            TextBlock sub = Label(SubLine(item), 11.5, Text2, false);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.VerticalAlignment = VerticalAlignment.Top;
            sub.Margin = new Thickness(0, top + size + below + 20, 0, 0);
            box.Children.Add(sub);
            _subLabels.Add(sub);

            box.Cursor = Cursors.Hand;
            // Rooms and friends alike: the effect wraps the ball, which grows and breathes.
            ScaleTransform ballScale = SplitBall(box, name, 85, top + size / 2);
            TranslateTransform labelMove = new TranslateTransform();
            name.RenderTransform = labelMove;
            sub.RenderTransform = labelMove;
            SpatialNode node = AddNode(box, 85, top + size / 2, item.X, item.Y, item.Z);
            box.CacheMode = new BitmapCache(2); // Kept sharp when you zoom in close.
            node.WrapRadius = size / 2;
            node.SizeScale = item.Size;
            node.BallScale = ballScale;
            node.LabelMove = labelMove;
            node.LabelDrop = size / 2 + (active ? 11 : 0); // Out from the ring's edge as it grows, not just the ball's.
            node.PatternTurn = turn;
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
            _roomRx = rx;
            _roomRy = ry;
            int count = _state.Channels.Count, i;
            for (i = 0; i < count; i++)
            {
                SpatialRoomChannel channel = _state.Channels[i];
                double home = -Math.PI / 4 + i * 2 * Math.PI / count;
                double angle = channel.HasLayout ? channel.Angle : home;
                double reach = channel.HasLayout ? channel.Reach : 1;
                double x = room.X + Math.Cos(angle) * rx * reach, y = room.Y + Math.Sin(angle) * ry * reach, z = room.Z + 30;
                bool mine = channel.Id == _state.LocalChannel;
                Color color = mine ? AccentCol : channel.IsText ? Warm : Accent2;

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
                string line = channel.IsText ? "Chat" : mine ? "Double-click for chat" : channel.People.Count > 0 ? channel.People.Count + " in voice" : "Click to join";
                TextBlock sub = Label(line, 11.5, Text2, false);
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sub.VerticalAlignment = VerticalAlignment.Top;
                sub.Margin = new Thickness(0, 106, 0, 0);
                box.Children.Add(sub);
                box.Cursor = Cursors.Hand;
                if (_state.IsHost)
                    box.ToolTip = "Drag to move it round the room. Hold and scroll to resize it.";
                ScaleTransform ballScale = SplitBall(box, name, 85, 45);
                TranslateTransform labelMove = new TranslateTransform();
                name.RenderTransform = labelMove;
                sub.RenderTransform = labelMove;

                SpatialNode node = AddNode(box, 85, 45, x, y, z);
                node.WrapRadius = 35;
                node.BallScale = ballScale;
                node.LabelMove = labelMove;
                node.LabelDrop = 35;
                node.Angle = angle;
                node.Reach = reach;
                node.HomeAngle = home;
                node.SizeScale = channel.HasLayout ? channel.Size : 1;
                node.Tuckable = true;
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
                SpatialNode channelNode = node;
                node.Click = delegate
                {
                    // One click moves you into a voice channel; a second straight after opens its panel.
                    // A text channel has nothing to join, so one click opens it.
                    double at = _clock.Elapsed.TotalSeconds;
                    bool second = _lastChannelClick == channelNode && at - _lastChannelClickAt < 0.4;
                    _lastChannelClick = channelNode;
                    _lastChannelClickAt = at;
                    if (text || second)
                    {
                        _lastChannelClick = null;
                        OpenChannel(id);
                    }
                    else if (id != _state.LocalChannel)
                        _host.SpatialJoinVoice(id);
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
                    Point spot = PersonSpot(node, mid);
                    SpatialNode m = AddNode(face, 45, 22, spot.X, spot.Y, room.Z + 70);
                    face.CacheMode = new BitmapCache(2);
                    m.WrapRadius = 20;
                    m.BallScale = faceScale;
                    m.LabelMove = (TranslateTransform)label.RenderTransform;
                    m.LabelDrop = 24;
                    m.Mid = mid;
                    m.Tuckable = true;
                    m.Person = person;
                    if (!channel.IsText)
                    {
                        m.Channel = node;
                        _voiceLines.People.Add(m);
                    }
                    if (animate)
                    {
                        m.FromX = x; m.FromY = y; m.FromZ = z;
                        m.Progress = 0;
                        m.Delay = 0.25 + j * 0.06;
                    }
                    uint who = person.Id;
                    SpatialNode self = m;
                    m.Click = delegate { RequestProfile(person, self, null, false); };
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
            ring.Stroke = new SolidColorBrush(AccentCol);
            ring.StrokeThickness = 2.5;
            ring.Visibility = person.Speaking ? Visibility.Visible : Visibility.Hidden;
            box.Children.Add(ring);
            _sceneRings.Add(new KeyValuePair<uint, UIElement>(person.Id, ring));
            Border avatar = Avatar(person.Name, 40, null, person.Picture);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            avatar.VerticalAlignment = VerticalAlignment.Top;
            avatar.Margin = new Thickness(0, 2, 0, 0);
            box.Children.Add(avatar);
            if (person.Deco != PictureDeco.None)
            {
                SpatialDecoRing deco = new SpatialDecoRing(person.Deco, person.DecoA, person.DecoB, new Point(45, 22), 20);
                box.Children.Add(deco);
                _sceneDecos.Add(deco);
            }
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.VerticalAlignment = VerticalAlignment.Top;
            // Wider than the ball, so a long name with LIVE and a mute mark isn't cut off.
            line.Margin = new Thickness(-70, 44, -70, 0);
            line.Children.Add(Label(person.Name, 11.5, Text2, false));
            if (person.Live)
                line.Children.Add(LiveTag());
            if (person.Muted || person.Deafened)
                line.Children.Add(MuteMark());
            // Moves down as the ball grows rather than scaling with it, and is drawn once and kept, so
            // it stays sharp and steady while the ball beside it changes every frame.
            line.RenderTransform = new TranslateTransform();
            line.CacheMode = new BitmapCache(2);
            TextOptions.SetTextHintingMode(line, TextHintingMode.Animated);
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

        private void OpenRoom(SpatialItem room)
        {
            _tuck = 0;
            _tuckTo = 0;
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
            _tuck = 0;
            _tuckTo = 0;
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
            // A title stays hovered a moment after the pointer leaves it, so brushing its edge
            // doesn't flick the effect off and on.
            _labelUnderPointer = over != null && over.Ring != null;
            if (_labelUnderPointer)
            {
                _hoveredLabel = over;
                _labelSeenAt = _clock.Elapsed.TotalSeconds;
            }
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
            _draggingChannel = _pressed != null && _pressed.ChannelId > 0 && _pressed.Person == null && _state.IsHost && _roomNode != null;
            bool dragsSomething = _pressed != null && (_pressed.Item != null || _pressed.IsGroupLabel || _draggingPerson || _draggingChannel);
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
            if (_pressed != null && _draggingChannel)
            {
                // Round the room and in or out, within limits, so channels stay near it.
                double ux = (_pressed.X + dx / s - _roomNode.X) / _roomRx, uy = (_pressed.Y + dy / s - _roomNode.Y) / _roomRy;
                double reach = Math.Max(VoipEngine.MinChannelReach, Math.Min(VoipEngine.MaxChannelReach, Math.Sqrt(ux * ux + uy * uy)));
                double angle = Math.Atan2(uy, ux);
                MoveChannel(_pressed, angle, reach, _pressed.SizeScale, false);
                _host.SpatialChannelLayout(_pressed.ChannelId, angle, reach, _pressed.SizeScale);
                return;
            }
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
                _host.SpatialPlaceCircle(pressed.Group.Id, pressed.Group.X - pressed.Group.Origin, pressed.Group.Y);
            else if (pressed.Item != null)
                DropItem(pressed.Item, pressed.X, pressed.Y, pressed.SizeScale);
            else if (pressed.Person != null && _draggingPerson)
                DropPerson(pressed.Person);
            _draggingPerson = false;
            _draggingChannel = false;
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
                double reach = node.WrapRadius * node.SizeScale * 1.5;
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
        private double _dropRadius; // While dragging: how big the circle under the orb would grow to.
        private double _carriedRadius; // The ring's size in the space, carried along when the orb leaves its circle.

        /// <summary>
        /// The circle an orb dropped at x, y belongs to, and how big that circle would grow to fit it.
        /// Touching a circle's edge is enough to join it; leaving its own takes pulling it clear. Too
        /// far out for the circle to stretch to (twice its usual size), it floats free: null.
        /// </summary>
        private SpatialGroup DropCircleFor(SpatialItem item, double x, double y, double radius, out double grownTo)
        {
            SpatialGroup target = null;
            SpatialGroup own = item.Group;
            if (own != null && Math.Sqrt((own.X - x) * (own.X - x) + (own.Y - y) * (own.Y - y)) - own.Radius < radius + 12)
                target = own;
            if (target == null)
                target = GroupAt(x, y, radius);
            grownTo = 0;
            if (target == null)
                return null;
            double reach = Math.Sqrt((target.X - x) * (target.X - x) + (target.Y - y) * (target.Y - y)) + radius + 40;
            if (reach > 2 * target.AutoRadius)
                return null;
            grownTo = Math.Max(target.Radius, reach);
            return target;
        }

        /// <summary>Drops a room or friend: into the circle it's touching, or free, exactly where it is, at its size.</summary>
        private void DropItem(SpatialItem item, double x, double y, double size)
        {
            double grownTo;
            double radius = (item.Room != null ? 46 : 39) * size;
            SpatialGroup target = DropCircleFor(item, x, y, radius, out grownTo);
            // Landed on another orb: nudged aside so both stay clickable.
            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                foreach (SpatialItem other in _items)
                {
                    if (other == item || other.Group != target)
                        continue;
                    double dx = x - other.X, dy = y - other.Y, d = Math.Sqrt(dx * dx + dy * dy), min = radius + other.Radius + 10;
                    if (d >= min)
                        continue;
                    if (d < 0.5)
                    {
                        dx = 1;
                        dy = 0;
                        d = 1;
                    }
                    x += dx / d * (min - d);
                    y += dy / d * (min - d);
                    moved = true;
                }
                if (!moved)
                    break;
            }
            string circle = target == null ? SpatialCircle.FreeId : target.Id;
            double px = target == null ? x : x - target.X, py = target == null ? y : y - target.Y;
            // Shown straight away; the saved state arrives with the next update.
            item.Circle = circle;
            item.Placed = true;
            item.PlaceX = px;
            item.PlaceY = py;
            item.Size = size;
            if (item.Room != null)
                _host.SpatialPlaceRoom(item.Room.Key, circle, px, py, size);
            else
                _host.SpatialPlaceFriend(item.Friend.Id, circle, px, py, size);
            LayoutGroups(_items, _groups, _state.Circles, 0);
            RebuildScene(false);
            RefreshAll();
        }

        /// <summary>
        /// The circle something of the given radius at x, y is touching: as soon as its edge meets the
        /// circle's. Touching two, the one it's furthest into.
        /// </summary>
        private SpatialGroup GroupAt(double x, double y, double margin)
        {
            SpatialGroup best = null;
            double bestDepth = 1e12;
            foreach (SpatialGroup group in Groups)
            {
                double depth = Math.Sqrt((group.X - x) * (group.X - x) + (group.Y - y) * (group.Y - y)) - group.Radius;
                if (depth < margin && depth < bestDepth)
                {
                    best = group;
                    bestDepth = depth;
                }
            }
            return best;
        }

        private string _softCircle; // The circle the loose ring is round, while the pointer stays in it.
        private string _spokeCircle; // The circle whose title's lines are showing, or fading out.
        private SpatialNode _zoomedTitle; // The title of the circle the full ring is round, if any.

        /// <summary>
        /// Rest on someone in a channel for a moment and their card shows, without its controls.
        /// Move off them and it goes a moment later. A card that was clicked open stays.
        /// </summary>
        private void PreviewCards(SpatialNode person, double now)
        {
            if (person != _previewFor)
            {
                _previewFor = person;
                _previewSince = now;
            }
            bool openCard = _card != null && !_cardIsPreview;
            if (openCard || (_cardFor != 0 && !_cardPreview && _card == null))
                return; // A full card is showing or on its way.
            if (person != null)
            {
                bool shown = _cardFor == person.Person.Id && _cardPreview;
                if (!shown && now - _previewSince > 0.18)
                    RequestProfile(person.Person, person, null, true);
            }
            else if (_cardIsPreview && now - _previewSince > 0.25)
                CloseCard();
            else if (_cardPreview && _card == null && now - _previewSince > 0.25)
                _cardFor = 0; // Asked for, not here yet, and no longer wanted.
        }

        /// <summary>A circle's title, which knows where its (unseen) ring is on screen.</summary>
        private SpatialNode TitleOf(string circle)
        {
            foreach (SpatialNode n in _nodes)
                if (n.RingNode != null && n.Group != null && n.Group.Id == circle) return n;
            return null;
        }

        /// <summary>Zoomed in on one circle, by scrolling or flying to it: its title, so the dots ring it. Else null.</summary>
        private SpatialNode ZoomedCircle()
        {
            // Measured against the space you can see, beside the side panel when it's open.
            Size space = SpaceSize();
            double h = _world.ActualHeight, view = Math.Min(space.Width, space.Height);
            if (view <= 0)
                return null;
            SpatialNode best = null;
            double bestOff = 1e12;
            foreach (SpatialNode n in _nodes)
            {
                if (n.RingNode == null || !n.RingNode.OnScreen)
                    continue;
                double r = n.Group.Radius * n.RingNode.ScreenScale;
                double ox = n.RingNode.ScreenX - CentreX(), oy = n.RingNode.ScreenY - h / 2, off = Math.Sqrt(ox * ox + oy * oy);
                // It fills a good part of the view and sits near its middle; a little less to stay.
                bool held = n.Group.Id == _zoomedCircle;
                if (2 * r >= (held ? 0.42 : 0.5) * view && off < (held ? 0.42 : 0.35) * view && off < bestOff)
                {
                    best = n;
                    bestOff = off;
                }
            }
            _zoomedCircle = best == null ? null : best.Group.Id;
            return best;
        }

        private string _zoomedCircle;

        private void MoveItemTo(SpatialItem item, SpatialGroup group)
        {
            // Shown straight away; the saved state arrives with the next update.
            item.Circle = group.Id;
            item.Placed = false; // From the list: laid out with the circle's others.
            if (item.Room != null)
                _host.SpatialMoveRoomToCircle(item.Room.Key, group.Id);
            else
                _host.SpatialMoveFriendToCircle(item.Friend.Id, group.Id);
            LayoutGroups(_items, _groups, _state.Circles, 0);
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
            if (_pressed != null && _pressed.Item != null && _world.IsMouseCaptured && _openRoomKey == null)
            {
                // Hold a room or friend and scroll to grow or shrink it; letting go keeps it.
                _pressed.SizeScale = Math.Max(0.6, Math.Min(1.8, _pressed.SizeScale * Math.Exp(e.Delta * 0.0009)));
                _panMoved = true;
                _dirty = true;
                return;
            }
            if (_draggingChannel && _pressed != null && _world.IsMouseCaptured)
            {
                // Hosts: hold a channel and scroll to grow or shrink it.
                double size = Math.Max(VoipEngine.MinChannelSize, Math.Min(VoipEngine.MaxChannelSize, _pressed.SizeScale * Math.Exp(e.Delta * 0.0009)));
                MoveChannel(_pressed, _pressed.Angle, _pressed.Reach, size, false);
                _host.SpatialChannelLayout(_pressed.ChannelId, _pressed.Angle, _pressed.Reach, size);
                _panMoved = true; // Letting go isn't a click.
                return;
            }
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
            if (e.Delta <= 0 || !zoomedIn)
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
                SpatialGroup target = GroupAt(_camX + (at.X - CentreX()) / scale, _camY + (at.Y - _world.ActualHeight / 2) / scale, 0);
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
                else if (node == null && _openRoomKey == null && !_flat)
                {
                    Point at = e.GetPosition(_world);
                    double scale = ZoomOf(_camZ);
                    ShowNewMenu(new Point(_camX + (at.X - CentreX()) / scale, _camY + (at.Y - _world.ActualHeight / 2) / scale));
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
            _input.PreviewKeyDown += InputPreviewKeyDown;
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
        private SpatialCardEffect _cardEffect, _cardDeco;
        private bool _cardPreview;   // What was asked for: a preview, or the whole card.
        private bool _cardIsPreview; // What's showing.
        private SpatialNode _previewFor; // The person the pointer is resting on, and since when.
        private double _previewSince;
        private FrameworkElement _cardDock; // Your picture in the dock, when your card came from it.
        private double _cardHomeScale, _cardHomeX, _cardHomeY; // Where the card shrinks back to.
        private readonly List<SpatialDecoRing> _sceneDecos = new List<SpatialDecoRing>();
        private readonly List<SpatialDecoRing> _dockDecos = new List<SpatialDecoRing>();
        private uint _cardFor;
        private SpatialNode _cardNode;
        private FrameworkElement _cardRow;
        private FrameworkElement _cardHides; // Their avatar and name in the space, hidden while the card shows them.
        private SpatialNode _roomNode;   // The room's ball, in a room: drop someone on it to take them out of voice.
        private SpatialNode _dragTarget; // The channel or ball under someone being dragged.
        private bool _draggingPerson;

        private void RequestProfile(SpatialPerson person, SpatialNode node, FrameworkElement row)
        {
            RequestProfile(person, node, row, false);
        }

        /// <summary>A preview is the card without its controls, for hovering; clicking opens the whole thing.</summary>
        private void RequestProfile(SpatialPerson person, SpatialNode node, FrameworkElement row, bool preview)
        {
            _cardPreview = preview;
            _cardFor = person.Id;
            _cardNode = node;
            _cardRow = row;
            _cardDock = null;
            _host.SpatialProfile(person.Id);
        }

        /// <summary>Your picture in the dock: your card grows up out of it.</summary>
        private void RequestMyProfile(FrameworkElement picture)
        {
            _cardPreview = false;
            _cardFor = SpatialProfile.Mine;
            _cardNode = null;
            _cardRow = null;
            _cardDock = picture;
            _host.SpatialProfile(SpatialProfile.Mine);
        }

        public void ShowProfile(SpatialProfile profile)
        {
            if (profile.Id != _cardFor)
                return;
            bool preview = _cardPreview;
            // Clicked while its preview shows: it opens out where it is, rather than growing again.
            bool expanding = !preview && _card != null && _cardIsPreview;
            RemoveCard();
            double width = _overlay.ActualWidth, height = _overlay.ActualHeight;
            Point centre;
            double radius;
            Point dock = new Point();
            double dockRadius = 0;
            bool fromDock = _cardDock != null && _cardDock.IsVisible;
            if (fromDock)
            {
                // The card sits just above the dock, its picture straight up from yours.
                radius = 30;
                dockRadius = _cardDock.ActualWidth / 2;
                dock = _cardDock.TranslatePoint(new Point(dockRadius, dockRadius), _overlay);
                centre = dock;
            }
            else if (_cardNode != null && _cardNode.OnScreen)
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
            DropShadowEffect shadow = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };
            card.Effect = shadow;

            FrameworkElement header = CardHeader(profile, radius, false);
            FrameworkElement body = preview ? PreviewHint(profile) : CardBody(profile);
            StackPanel stack = new StackPanel();
            Grid layers = new Grid();
            layers.Children.Add(stack);
            card.Child = layers;
            stack.Children.Add(header);
            stack.Children.Add(body);
            card.Measure(new Size(CardWidth, double.PositiveInfinity));
            double cardHeight = card.DesiredSize.Height;

            // The avatar sits in the card's top corner; with no room below, it sits in the bottom one.
            bool flipY = !fromDock && centre.Y - radius - CardPad + cardHeight > height - 8;
            if (flipY)
            {
                stack.Children.Clear();
                stack.Children.Add(body);
                header = CardHeader(profile, radius, true);
                stack.Children.Add(header);
            }
            _cardEffect = null;
            if (profile.Effect != CardEffect.None || profile.Deco != PictureDeco.None)
            {
                Grid parts = (Grid)header;
                FrameworkElement picture = (FrameworkElement)parts.Children[1];
                _cardEffect = new SpatialCardEffect(profile.Effect, PictureDeco.None, profile.BannerA, profile.BannerB, (int)profile.Id, picture, parts, layers, shadow);
                parts.Children.Insert(1, _cardEffect); // Over the banner, under everything else.
                _cardDeco = new SpatialCardEffect(CardEffect.None, profile.Deco, profile.BannerA, profile.BannerB, (int)profile.Id, picture, parts, layers, null);
                layers.Children.Add(_cardDeco);
            }
            // Always opens to the right, picture first. Near the right edge it slides left to stay on screen.
            double left = Math.Max(8, Math.Min(centre.X - radius - CardPad, width - CardWidth - 8));
            double top = flipY ? centre.Y + radius + CardPad - cardHeight : centre.Y - radius - CardPad;
            if (fromDock)
                top = dock.Y - dockRadius - 14 - cardHeight;
            top = Math.Max(8, top);

            // A preview lets the pointer through, so moving on or clicking the person still works.
            card.IsHitTestVisible = !preview;
            _cardIsPreview = preview;
            if (!preview)
            {
                // A click anywhere else closes it.
                Rectangle catcher = new Rectangle();
                catcher.Width = width;
                catcher.Height = height;
                catcher.Fill = Brushes.Transparent;
                catcher.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; CloseCard(); };
                catcher.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; CloseCard(); };
                _cardLayer.Children.Add(catcher);
            }

            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            if (fromDock)
            {
                // The picture starts on yours in the dock, then rises and grows into the card's.
                Point picture = new Point(left + CardPad + radius, top + CardPad + radius);
                card.RenderTransformOrigin = new Point((picture.X - left) / CardWidth, (picture.Y - top) / cardHeight);
                _cardHomeScale = dockRadius / radius;
                _cardHomeX = dock.X - picture.X;
                _cardHomeY = dock.Y - picture.Y;
            }
            else
            {
                card.RenderTransformOrigin = new Point((centre.X - left) / CardWidth, (centre.Y - top) / cardHeight);
                _cardHomeScale = 0.82;
                _cardHomeX = 0;
                _cardHomeY = 0;
            }
            ScaleTransform grow = new ScaleTransform(_cardHomeScale, _cardHomeScale);
            TranslateTransform slide = new TranslateTransform(_cardHomeX, _cardHomeY);
            TransformGroup moves = new TransformGroup();
            moves.Children.Add(grow);
            moves.Children.Add(slide);
            card.RenderTransform = moves;
            card.Opacity = 0;
            _cardLayer.Children.Add(card);
            _card = card;
            if (fromDock)
            {
                _cardHides = _cardDock;
                _cardHides.Opacity = 0;
            }
            else if (_cardNode != null && _cardNode.OnScreen)
            {
                _cardHides = _cardNode.Element;
                _cardHides.Opacity = 0;
            }

            if (expanding)
            {
                grow.ScaleX = grow.ScaleY = 1;
                slide.X = slide.Y = 0;
                card.Opacity = 1;
                body.Opacity = 0;
                body.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(160))));
                return;
            }

            // Grows out of the avatar.
            Duration time = new Duration(TimeSpan.FromMilliseconds(fromDock ? 240 : 170));
            QuadraticEase ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, time) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, time) { EasingFunction = ease });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(fromDock ? 110 : 170))));
        }

        private void CloseCard()
        {
            Border card = _card;
            if (card == null)
                return;
            _card = null;
            _cardIsPreview = false;
            _cardFor = 0;
            TransformGroup moves = card.RenderTransform as TransformGroup;
            Duration time = new Duration(TimeSpan.FromMilliseconds(_cardDock != null ? 180 : 120));
            DoubleAnimation fade = new DoubleAnimation(0, time);
            fade.Completed += delegate
            {
                if (_card != null)
                    return;
                _cardLayer.Children.Clear();
                _cardEffect = null;
                _cardDeco = null;
                ShowHidden();
            };
            if (moves != null)
            {
                // Back where it came from: into the avatar, or down into your picture in the dock.
                ScaleTransform grow = (ScaleTransform)moves.Children[0];
                TranslateTransform slide = (TranslateTransform)moves.Children[1];
                double scale = _cardDock != null ? _cardHomeScale : 0.85;
                grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, time));
                grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, time));
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_cardHomeX, time));
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_cardHomeY, time));
            }
            card.BeginAnimation(OpacityProperty, fade);
        }

        private void RemoveCard()
        {
            _cardLayer.Children.Clear();
            _card = null;
            _cardIsPreview = false;
            _cardEffect = null;
            _cardDeco = null;
            ShowHidden();
        }

        private void ShowHidden()
        {
            if (_cardHides != null)
                _cardHides.Opacity = 1;
            _cardHides = null;
        }

        /// <summary>
        /// The picture in the corner, exactly where the avatar is, with the name beside it, on
        /// their banner. The banner is the first child and the avatar the second.
        /// </summary>
        private FrameworkElement CardHeader(SpatialProfile profile, double radius, bool atBottom)
        {
            double size = 2 * radius;
            Color tint = ColorFor(profile.Name);
            Grid header = new Grid();
            header.MinHeight = Math.Max(size + 2 * CardPad, profile.Banner == BannerKind.Auto ? 0 : 92);

            // Rounded to match the card's corners on its side; the card doesn't clip it.
            Border banner = new Border();
            banner.CornerRadius = atBottom ? new CornerRadius(0, 0, 15, 15) : new CornerRadius(15, 15, 0, 0);
            Color a = Color.FromRgb((byte)(profile.BannerA >> 16), (byte)(profile.BannerA >> 8), (byte)profile.BannerA);
            Color b = Color.FromRgb((byte)(profile.BannerB >> 16), (byte)(profile.BannerB >> 8), (byte)profile.BannerB);
            if (profile.Banner == BannerKind.Picture && profile.BannerPicture != null)
                banner.Background = new ImageBrush(profile.BannerPicture) { Stretch = Stretch.UniformToFill };
            else if (profile.Banner == BannerKind.Gradient)
                banner.Background = new LinearGradientBrush(a, b, 0);
            else if (profile.Banner == BannerKind.Colour)
                banner.Background = new SolidColorBrush(a);
            else
                banner.Background = new LinearGradientBrush(Fade(tint, 0x99), Fade(tint, 0x00), atBottom ? 270 : 90);
            header.Children.Add(banner);
            if (profile.Banner != BannerKind.Auto)
            {
                // Keeps the name readable on any banner, and blends it into the card.
                Border shade = new Border();
                shade.CornerRadius = banner.CornerRadius;
                shade.Background = new LinearGradientBrush(Fade(SolidBg, 0x30), Fade(SolidBg, 0xB0), atBottom ? 270 : 90);
                banner.Child = shade;
            }

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
                TextBlock status = Label(String.Empty, 13, Text2, false);
                SetTextWithEmoji(status, profile.Status);
                status.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(status);
            }
            words.Children.Add(Label(profile.State, 12, Muted, false));
            header.Children.Add(words);
            return header;
        }

        /// <summary>A preview's only line under the header: what clicking gives you.</summary>
        private FrameworkElement PreviewHint(SpatialProfile profile)
        {
            string what = profile.IsLocal ? "Click for your status and profile"
                : profile.ViewerIsHost ? "Click for volume, mute, move and kick" : "Click for volume and mute";
            TextBlock hint = Label(what, 11.5, Muted, false);
            hint.Margin = new Thickness(CardPad, 0, CardPad, CardPad - 2);
            return hint;
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
                Action local = entry.Local;
                Action click = children != null
                    ? (Action)delegate { FillMenu(frame, children, entries); }
                    : delegate
                    {
                        _menu.IsOpen = false;
                        if (local != null)
                            local();
                        else
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
            // One space for rooms and friends now; just a count of each.
            int on = 0;
            foreach (SpatialFriend friend in _state.Friends)
                if (friend.IsFriend && friend.Status != "Offline") on++;
            TextBlock counts = Label(_rooms.Count + (_rooms.Count == 1 ? " room" : " rooms") + "  \u00B7  " + on + (on == 1 ? " friend" : " friends") + " online", 13, Text2, false);
            counts.VerticalAlignment = VerticalAlignment.Center;
            _top.Children.Add(counts);
            int unread = FriendsUnread();
            if (unread > 0)
            {
                Border badge = Badge(unread);
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                _top.Children.Add(badge);
            }
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
                    _host.SpatialCircleMenu(target2.Id);
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
            List<SpatialItem> loose = Current.FindAll(delegate(SpatialItem m) { return m.Group == null && m.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0; });
            if (loose.Count > 0)
            {
                Border head = new Border();
                head.Padding = new Thickness(13, 6, 13, 4);
                DockPanel headLine = new DockPanel();
                TextBlock count = Label(loose.Count.ToString(), 11, Muted, false);
                DockPanel.SetDock(count, Dock.Right);
                headLine.Children.Add(count);
                headLine.Children.Add(Label("NOT IN A CIRCLE", 11, Muted, true));
                head.Child = headLine;
                _navList.Children.Add(head);
                shown += loose.Count;
                AddNavRows(loose);
            }
            if (shown == 0 && find.Length > 0)
            {
                TextBlock note = Label("Nothing matches.", 12.5, Muted, false);
                note.Margin = new Thickness(16);
                _navList.Children.Add(note);
            }
            _navFoot.Children.Clear();
            // Two buttons: everything you can make or add, and your friend code (with the rarer
            // things behind "...").
            _navFoot.Children.Add(Chip("+ New", delegate { ShowNewMenu(null); }));
            Border code = Chip("My code", _host.SpatialCopyFriendCode);
            code.ToolTip = "Copy your friend code, to give to someone so they can add you";
            _navFoot.Children.Add(code);
            Border more = Chip("\u22EF", delegate
            {
                List<SpatialMenuEntry> entries = new List<SpatialMenuEntry>();
                entries.Add(LocalItem("Backup", _host.SpatialBackupMenu));
                ShowMenu(entries);
            });
            more.ToolTip = "More";
            _navFoot.Children.Add(more);
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
                words.Children.Add(Label(item.Name, 13, item.Room != null && item.Room.Active ? AccentCol : TextCol, false));
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
            ring.Stroke = new SolidColorBrush(AccentCol);
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
                    FrameworkElement lead = channel.IsText ? CentredLabel("#", 15, Text2) : Glyph("", 14, mine ? AccentCol : Text2);
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
            composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composeRow.ColumnDefinitions.Add(new ColumnDefinition());
            composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Border attach = DockButton("\uE723", false, false, "Send a picture (or paste one with Ctrl+V)", delegate { _host.SpatialAttachPicture(_openFriend, _openChannel); });
            attach.Margin = new Thickness(0, 0, 6, 0);
            composeRow.Children.Add(attach);
            Grid.SetColumn(compose, 1);
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
            Grid.SetColumn(emoji, 2);
            composeRow.Children.Add(emoji);
            Border send = DockButton("", false, false, "Send", SendInput);
            Grid.SetColumn(send, 3);
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

        /// <summary>
        /// Ctrl+V with a picture on the clipboard, such as a screenshot, sends it as a picture. This
        /// runs before the box sees the key, which would otherwise do nothing with a picture.
        /// </summary>
        private void InputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool paste = (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) || (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            if (!paste)
                return;
            bool picture;
            try { picture = Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); }
            catch { picture = false; }
            if (!picture)
                return;
            _host.SpatialPastePicture(_openFriend, _openChannel);
            e.Handled = true;
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

        /// <summary>A picture in chat: fitted into the column, click to see it full size.</summary>
        private FrameworkElement ShotView(SpatialLine message)
        {
            double scale = Math.Min(1, Math.Min(380.0 / message.ShotWidth, 300.0 / message.ShotHeight));
            double w = Math.Max(60, message.ShotWidth * scale), h = Math.Max(40, message.ShotHeight * scale);
            Border frame = new Border();
            frame.Width = w;
            frame.Height = h;
            frame.Margin = new Thickness(0, 6, 0, 2);
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.CornerRadius = new CornerRadius(10);
            frame.BorderBrush = new SolidColorBrush(LineCol);
            frame.BorderThickness = new Thickness(1);
            if (message.Shot != null)
            {
                frame.Background = new ImageBrush(message.Shot) { Stretch = Stretch.UniformToFill };
                frame.Cursor = Cursors.Hand;
                frame.ToolTip = "Click to see it full size";
                string id = message.ShotId, friend = message.FriendId;
                frame.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; _host.SpatialOpenShot(id, friend); };
            }
            else
            {
                frame.Background = new SolidColorBrush(Raise);
                frame.Child = CentredLabel(message.ShotComing ? "Receiving picture\u2026" : "Picture not available", 12, Muted);
            }
            return frame;
        }

        /// <summary>
        /// A YouTube link as a card. Nothing is fetched from YouTube unless someone asks for the
        /// preview; Play opens the video in the browser.
        /// </summary>
        private FrameworkElement VideoCard(string videoId)
        {
            Border card = new Border();
            card.Margin = new Thickness(0, 6, 0, 2);
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.Width = 320;
            card.CornerRadius = new CornerRadius(10);
            card.Background = new SolidColorBrush(Raise);
            card.BorderBrush = new SolidColorBrush(LineCol);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(10);
            StackPanel inside = new StackPanel();
            card.Child = inside;
            DockPanel top = new DockPanel();
            TextBlock mark = Label("\u25B6", 16, Color.FromRgb(0xFF, 0x33, 0x33), true);
            mark.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(mark, Dock.Left);
            top.Children.Add(mark);
            StackPanel words = new StackPanel();
            words.Children.Add(Label("YouTube video", 13, TextCol, true));
            words.Children.Add(Label("youtu.be/" + videoId, 11, Muted, false));
            top.Children.Add(words);
            inside.Children.Add(top);
            Border preview = new Border();
            preview.Height = 0;
            preview.CornerRadius = new CornerRadius(8);
            preview.Margin = new Thickness(0, 8, 0, 0);
            inside.Children.Add(preview);
            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.Margin = new Thickness(0, 8, 0, 0);
            Border play = Chip("Play on YouTube", delegate { _host.SpatialOpenVideo(videoId); });
            play.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(play);
            Border show = null;
            show = Chip("Show preview", delegate
            {
                // Only now does this PC contact YouTube, for the thumbnail.
                try
                {
                    System.Windows.Media.Imaging.BitmapImage thumb = new System.Windows.Media.Imaging.BitmapImage();
                    thumb.BeginInit();
                    thumb.UriSource = new Uri(ChatPicture.YouTubeThumbnail(videoId));
                    thumb.EndInit();
                    preview.Height = 168;
                    preview.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                    preview.Cursor = Cursors.Hand;
                    preview.MouseLeftButtonUp += delegate { _host.SpatialOpenVideo(videoId); };
                    show.Visibility = Visibility.Collapsed;
                }
                catch { }
            });
            show.ToolTip = "Loads the video's picture from YouTube";
            buttons.Children.Add(show);
            inside.Children.Add(buttons);
            return card;
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
            if (!String.IsNullOrEmpty(message.Text))
            {
                TextBlock body = Label(String.Empty, 14, TextCol, false);
                SetTextWithEmoji(body, message.Text);
                body.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(body);
            }
            if (message.ShotId != null)
                words.Children.Add(ShotView(message));
            if (message.VideoId != null)
                words.Children.Add(VideoCard(message.VideoId));
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
            Grid me = new Grid();
            Border meButton = Clickable(delegate { RequestMyProfile(me); });
            meButton.CornerRadius = new CornerRadius(10);
            StackPanel meLine = new StackPanel();
            meLine.Orientation = Orientation.Horizontal;
            // Exactly the picture's size, so the deco circles it rather than the dock row's height.
            me.Width = 38;
            me.Height = 38;
            me.VerticalAlignment = VerticalAlignment.Center;
            me.Children.Add(Avatar(s.MyName ?? "?", 38, s.MyStatus, s.MyPicture));
            _dockDecos.Clear();
            if (s.MyDeco != PictureDeco.None)
            {
                SpatialDecoRing deco = new SpatialDecoRing(s.MyDeco, s.MyDecoA, s.MyDecoB, new Point(19, 19), 19);
                me.Children.Add(deco);
                _dockDecos.Add(deco);
            }
            meLine.Children.Add(me);
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
                TextBlock state = Label(s.InVoice ? "Voice connected" + (s.Ping > 0 ? " · " + s.Ping + " ms" : String.Empty) : "In the room", 13, s.InVoice ? AccentCol : Text2, true);
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

        // ----- Orbs: a glass shell, and inside one with no picture, a pattern made from its name. -----

        private static readonly Dictionary<string, ImageSource> _patterns = new Dictionary<string, ImageSource>();

        /// <summary>
        /// The inside of an orb with no picture: soft clouds of its colour and the theme's two
        /// accents, with marbled contour lines over them, all placed by its name. The same name
        /// always gives the same pattern, so each room and friend can be told apart at a glance.
        /// Drawn once and kept.
        /// </summary>
        private static ImageSource OrbPattern(string name, Color colour)
        {
            string key = (name ?? String.Empty) + "|" + colour + "|" + AccentCol + "|" + Accent2;
            ImageSource cached;
            if (_patterns.TryGetValue(key, out cached))
                return cached;
            if (_patterns.Count > 256)
                _patterns.Clear();
            const double S = 128, C = 64;
            int seed = 17;
            foreach (char ch in name ?? String.Empty)
                seed = unchecked(seed * 31 + Char.ToLowerInvariant(ch));
            Random random = new Random(seed);
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushClip(new EllipseGeometry(new Point(C, C), C, C));
                RadialGradientBrush body = new RadialGradientBrush();
                body.GradientOrigin = new Point(0.36, 0.3);
                body.Center = new Point(0.45, 0.4);
                body.RadiusX = body.RadiusY = 0.75;
                body.GradientStops.Add(new GradientStop(Mix(colour, Colors.White, 0.12), 0));
                body.GradientStops.Add(new GradientStop(Mix(colour, Color.FromRgb(6, 6, 18), 0.45), 0.55));
                body.GradientStops.Add(new GradientStop(Mix(colour, Color.FromRgb(4, 4, 12), 0.82), 1));
                dc.DrawRectangle(body, null, new Rect(0, 0, S, S));

                // Clouds of colour.
                Color[] clouds = { colour, AccentCol, Accent2, Mix(colour, AccentCol, 0.5) };
                for (int i = 0; i < clouds.Length; i++)
                {
                    double r = 26 + random.NextDouble() * 30;
                    Point at = new Point(C + (random.NextDouble() - 0.5) * 70, C + (random.NextDouble() - 0.5) * 70);
                    RadialGradientBrush cloud = new RadialGradientBrush();
                    cloud.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(i == 0 ? 0x90 : 0x60), clouds[i].R, clouds[i].G, clouds[i].B), 0));
                    cloud.GradientStops.Add(new GradientStop(Color.FromArgb(0, clouds[i].R, clouds[i].G, clouds[i].B), 1));
                    dc.DrawEllipse(cloud, null, at, r, r);
                }

                // A sweeping band, like a current through it.
                double a0 = random.NextDouble() * Math.PI * 2;
                StreamGeometry band = new StreamGeometry();
                using (StreamGeometryContext ctx = band.Open())
                {
                    ctx.BeginFigure(new Point(C + Math.Cos(a0) * 70, C + Math.Sin(a0) * 70), false, false);
                    ctx.BezierTo(new Point(C + Math.Cos(a0 + 1.4) * 20, C + Math.Sin(a0 + 1.4) * 20),
                        new Point(C + Math.Cos(a0 + 2.2) * 30, C + Math.Sin(a0 + 2.2) * 30),
                        new Point(C + Math.Cos(a0 + Math.PI) * 70, C + Math.Sin(a0 + Math.PI) * 70), true, true);
                }
                Pen current = new Pen(new SolidColorBrush(Color.FromArgb(0x38, AccentCol.R, AccentCol.G, AccentCol.B)), 13);
                current.StartLineCap = current.EndLineCap = PenLineCap.Round;
                dc.DrawGeometry(null, current, band);

                // Marbled contour lines round a point off centre.
                Point focus = new Point(C + (random.NextDouble() - 0.5) * 36, C + (random.NextDouble() - 0.5) * 36);
                int f1 = 2 + random.Next(3), f2 = 5 + random.Next(4);
                double p1 = random.NextDouble() * 6.3, p2 = random.NextDouble() * 6.3;
                for (int k = 0; k < 7; k++)
                {
                    double baseR = 9 + k * 11;
                    StreamGeometry loop = new StreamGeometry();
                    using (StreamGeometryContext ctx = loop.Open())
                    {
                        for (int i = 0; i <= 56; i++)
                        {
                            double t = i / 56.0 * Math.PI * 2;
                            double r = baseR * (1 + 0.13 * Math.Sin(f1 * t + p1 + k * 0.4) + 0.06 * Math.Sin(f2 * t + p2 - k * 0.3));
                            Point p = new Point(focus.X + Math.Cos(t) * r, focus.Y + Math.Sin(t) * r);
                            if (i == 0)
                                ctx.BeginFigure(p, false, true);
                            else
                                ctx.LineTo(p, true, true);
                        }
                    }
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(0x16 + k * 5), 255, 255, 255)), 1.1), loop);
                }
                dc.Pop();
            }
            System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)S, (int)S, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            _patterns[key] = bitmap;
            return bitmap;
        }

        /// <summary>
        /// The glass over an orb, picture or pattern: a soft rim of its own colour, a crisp edge
        /// that catches the light from the top left, a glint near the top and a little light
        /// bouncing off the bottom. None of it takes the pointer.
        /// </summary>
        private static void AddGlass(Panel into, double left, double top, double size, Color rim)
        {
            Ellipse glow = TopLeft(new Ellipse(), left, top);
            glow.Width = glow.Height = size;
            RadialGradientBrush rimLight = new RadialGradientBrush();
            rimLight.GradientStops.Add(new GradientStop(Color.FromArgb(0, rim.R, rim.G, rim.B), 0.7));
            rimLight.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, rim.R, rim.G, rim.B), 1));
            glow.Fill = rimLight;
            glow.IsHitTestVisible = false;
            into.Children.Add(glow);

            Ellipse edge = TopLeft(new Ellipse(), left, top);
            edge.Width = edge.Height = size;
            LinearGradientBrush light = new LinearGradientBrush();
            light.StartPoint = new Point(0.2, 0);
            light.EndPoint = new Point(0.8, 1);
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, 255, 255, 255), 0));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 255, 255, 255), 0.55));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, rim.R, rim.G, rim.B), 1));
            edge.Stroke = light;
            edge.StrokeThickness = Math.Max(1, size * 0.016);
            edge.IsHitTestVisible = false;
            into.Children.Add(edge);

            if (size < 26)
                return; // Too small for highlights to read.
            Ellipse glint = TopLeft(new Ellipse(), left + size * 0.15, top + size * 0.09);
            glint.Width = size * 0.46;
            glint.Height = size * 0.26;
            glint.RenderTransform = new RotateTransform(-28, size * 0.23, size * 0.13);
            RadialGradientBrush shine = new RadialGradientBrush();
            shine.GradientStops.Add(new GradientStop(Color.FromArgb(0x9A, 255, 255, 255), 0));
            shine.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            glint.Fill = shine;
            glint.IsHitTestVisible = false;
            into.Children.Add(glint);

            Ellipse bounce = TopLeft(new Ellipse(), left + size * 0.2, top + size * 0.74);
            bounce.Width = size * 0.6;
            bounce.Height = size * 0.18;
            RadialGradientBrush below = new RadialGradientBrush();
            below.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, rim.R, rim.G, rim.B), 0));
            below.GradientStops.Add(new GradientStop(Color.FromArgb(0, rim.R, rim.G, rim.B), 1));
            bounce.Fill = below;
            bounce.IsHitTestVisible = false;
            into.Children.Add(bounce);
        }

        private static Typeface LetteringFace = new Typeface(new FontFamily("Segoe UI Variable Display Light, Segoe UI Light, Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

        /// <summary>
        /// Letters as a shape rather than text, centred on a point. A shape scales smoothly as
        /// its orb grows and breathes, where text would snap to new pixels each frame and shimmer.
        /// </summary>
        private static FrameworkElement Lettering(string text, double size, double centreX, double centreY)
        {
            FormattedText letters = new FormattedText(text ?? String.Empty, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LetteringFace, size, Brushes.White);
            Geometry shape = letters.BuildGeometry(new Point(0, 0));
            shape.Freeze();
            Rect bounds = shape.Bounds;
            Path path = new Path();
            path.Data = shape;
            path.Fill = new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255));
            path.IsHitTestVisible = false;
            if (bounds.IsEmpty)
                return path;
            // Centred on the ink itself, not the line's box, so it sits in the middle of the orb.
            return TopLeft(path, centreX - bounds.X - bounds.Width / 2, centreY - bounds.Y - bounds.Height / 2);
        }

        private static readonly Dictionary<string, ImageSource> _emoji = new Dictionary<string, ImageSource>();

        /// <summary>
        /// Text with its emoji drawn as the colour emoji pack's pictures, as the Lightweight layout
        /// does: WPF can't draw colour emoji itself. Without the pack, they stay as text.
        /// </summary>
        private static void SetTextWithEmoji(TextBlock block, string text)
        {
            block.Inlines.Clear();
            foreach (object part in EmojiPack.Tokenize(text ?? String.Empty))
            {
                EmojiEntry entry = part as EmojiEntry;
                ImageSource picture = entry == null ? null : EmojiPicture(entry);
                if (picture == null)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(entry != null ? entry.Text : (string)part));
                    continue;
                }
                double size = block.FontSize * 1.3;
                Image image = new Image();
                image.Source = picture;
                image.Width = size;
                image.Height = size;
                image.Margin = new Thickness(1, 0, 1, 0);
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                System.Windows.Documents.InlineUIContainer holder = new System.Windows.Documents.InlineUIContainer(image);
                holder.BaselineAlignment = BaselineAlignment.Center;
                block.Inlines.Add(holder);
            }
        }

        private static ImageSource EmojiPicture(EmojiEntry entry)
        {
            ImageSource picture;
            if (_emoji.TryGetValue(entry.FileName, out picture))
                return picture;
            byte[] png = EmojiPack.GetPng(entry);
            if (png != null)
            {
                try
                {
                    System.Windows.Media.Imaging.BitmapImage bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new System.IO.MemoryStream(png);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    picture = bitmap;
                }
                catch
                {
                    picture = null;
                }
            }
            if (picture == null)
                return null; // Not installed yet: tried again next time, so Repair downloads show up.
            if (_emoji.Count > 512)
                _emoji.Clear();
            _emoji[entry.FileName] = picture;
            return picture;
        }

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
            label.FontSize = (size < 13 ? size + 1 : size) * _textScale; // The smallest sizes read better a step up.
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
                avatar.Background = new ImageBrush(OrbPattern(safe, ColorFor(safe)));
                inner.Children.Add(Lettering(safe.Substring(0, 1).ToUpperInvariant(), size * 0.44, size / 2, size / 2));
            }
            AddGlass(inner, 0, 0, size, Mix(ColorFor(safe), AccentCol, 0.35));
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

        private static SpatialMenuEntry LocalItem(string text, Action action)
        {
            SpatialMenuEntry entry = new SpatialMenuEntry();
            entry.Text = text;
            entry.Enabled = true;
            entry.Local = action;
            return entry;
        }

        /// <summary>
        /// "+ New": everything you can make or add. From empty space, the new circle goes where you
        /// right-clicked (a place in the space, or null for the next free spot).
        /// </summary>
        private void ShowNewMenu(Point? at)
        {
            List<SpatialMenuEntry> entries = new List<SpatialMenuEntry>();
            entries.Add(LocalItem("Host a room", delegate { _host.SpatialAddRoom(true); }));
            entries.Add(LocalItem("Join with a code", delegate { _host.SpatialAddRoom(false); }));
            entries.Add(LocalItem("Add a friend", _host.SpatialAddFriend));
            SpatialMenuEntry line = new SpatialMenuEntry();
            line.Separator = true;
            entries.Add(line);
            if (at.HasValue)
            {
                Point place = at.Value;
                entries.Add(LocalItem("New circle here", delegate { _host.SpatialNewCircle(place.X, place.Y); }));
            }
            else
                entries.Add(LocalItem("New circle", delegate { _host.SpatialNewCircle(null, null); }));
            ShowMenu(entries);
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
            if (status == "Online") return AccentCol; // Online in your highlight colour, not a fixed green.
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

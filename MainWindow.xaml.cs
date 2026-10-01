using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    enum View { Idle, Media, Timer, Volume, Charge, Toast, Notice, MediaBig, IdleBig, TimerBig, TimerSet, Menu }

    /// <summary>What a click has opened; None is the compact pill.</summary>
    enum Panel { None, Player, Timer, TimerSet, Menu }

    readonly record struct Dims(double W, double H, double R);

    static readonly Dictionary<View, Dims> Sizes = new()
    {
        [View.Idle] = new(118, 34, 17),
        [View.Media] = new(210, 34, 17),
        [View.Timer] = new(132, 34, 17),
        [View.Volume] = new(250, 34, 17),
        [View.Charge] = new(230, 34, 17),
        [View.Toast] = new(340, 68, 30),
        [View.Notice] = new(320, 64, 29),
        [View.MediaBig] = new(380, 176, 40),
        [View.IdleBig] = new(320, 124, 38),
        [View.TimerBig] = new(330, 92, 40),
        [View.TimerSet] = new(300, 190, 38),
        [View.Menu] = new(280, 208, 34),
    };

    const double HostWidth = 620;
    const double BubbleWidth = 78, BubbleGap = 7; // the split-off bubble and the gap between it and the pill
    const int MaxMinutes = 99; // the countdown always reads mm:ss
    const int HeadsetEvery = 300; // ticks between looks at the headphones' charge: it moves slowly
    const int HeadsetLow = 20, HeadsetCritical = 10; // percent: passing each on the way down is worth a warning
    const double VolumeTrack = 162;
    const double SeekTrack = 260;
    const double SeekThin = 6, SeekHover = 9, SeekDrag = 12; // bar thickness: resting, under the pointer, while scrubbing
    const double EqFrame = 0.012; // seconds: caps the bars at 60–80 fps on high-refresh displays
    const double MediaWidth = 210; // compact player without a lyric line
    const double MediaMaxWidth = 440; // it widens to fit the line being sung, up to this
    const double MediaNameWidth = 300; // ...while a track name is cut off here: it just sits there, no need to be big
    const double LyricInset = 77; // cover on the left + bars on the right of the lyric box
    const double LyricEdge = 8; // faded strip on each side of the lyric box
    const double LyricSpeed = 36; // px per second, when the line lasts long enough to take it easy
    const double LyricGap = 4; // seconds of silence in the lyrics before the track name fills in
    static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(200); // the line lands as it is sung, not after
    static readonly TimeSpan PausedGrace = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(550); // open panel, pointer gone
    static readonly TimeSpan BubbleLinger = TimeSpan.FromSeconds(2.5); // ...longer when it was opened from the bubble
    static readonly CultureInfo Ru = new("ru-RU");
    static readonly Color SwitchOff = Color.FromRgb(0x39, 0x39, 0x3D);
    static readonly Color SwitchOn = Color.FromRgb(0x30, 0xD1, 0x58);

    readonly Dictionary<View, FrameworkElement> _views;
    readonly Spring _w = new(34), _h = new(34), _r = new(17), _scale = new(1), _offset = new(0);
    readonly Spring _seekX = new(0), _seekH = new(SeekThin);
    readonly Spring _split = new(0); // 0: the bubble is tucked behind the pill, 1: it stands on its own
    readonly Spring _bubbleScale = new(1); // the bubble answers the pointer by itself, not along with the pill
    readonly RectangleGeometry _clip = new();
    readonly ImageBrush _art = new() { Stretch = Stretch.UniformToFill };
    readonly SolidColorBrush _accent = new(Colors.White);
    readonly SolidColorBrush _switchBrush = new(SwitchOff);
    readonly AudioService _audio = new();
    readonly SpectrumService _spectrum = new();
    readonly float[] _bands = new float[SpectrumService.Bands];
    readonly MediaService _media;
    readonly LyricsService _lyrics = new();
    readonly NetworkService _network;
    readonly Countdown _timer = new();
    readonly Alarm _alarm = new();
    readonly Stopwatch _time = Stopwatch.StartNew();
    readonly DispatcherTimer _tick, _transientTimer, _collapseTimer;
    readonly View? _forced;
    readonly double _forcedTimer;

    View _current = View.Idle;
    View? _transient;
    Panel _panel;
    bool _hover, _pressed, _hidden, _animating, _eqRunning, _seekRunning, _scrubbing;
    bool _bubbleHover, _bubblePressed;
    bool _ringing; // the countdown ran out and the alarm is still going
    int _minutes = 25, _timerShown = -1;
    double _lastFrame, _eqFrame, _seekFrame;
    double _scrub, _scrubUntil; // fraction under the pointer; it stays on the bar until the player reports the jump
    (int At, int Total) _seekLabel = (-1, -1);
    int _ticks;
    float _lastVolume = -1;
    bool _lastMuted, _lastPlugged, _powerKnown;
    int _headset = -1; // charge of the output device in percent; -1: it reports none
    Guid _headsetId; // ...and the device that number belongs to
    string _lastTitle = "";
    DateTime _lastPlaying = DateTime.MinValue;
    LyricsService.Line[] _lyricLines = [];
    int _lyricIndex = -1;
    string _lyricTitle = "";
    bool _lyricNamed;
    double _mediaWidth = MediaWidth;
    TextBlock _lyric;
    IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        _views = new()
        {
            [View.Idle] = IdleView,
            [View.Media] = MediaView,
            [View.Timer] = TimerView,
            [View.Volume] = VolumeView,
            [View.Charge] = ChargeView,
            [View.Toast] = ToastView,
            [View.Notice] = NoticeView,
            [View.MediaBig] = MediaBigView,
            [View.IdleBig] = IdleBigView,
            [View.TimerBig] = TimerBigView,
            [View.TimerSet] = TimerSetView,
            [View.Menu] = MenuView,
        };
        foreach (FrameworkElement v in _views.Values)
        {
            v.RenderTransformOrigin = new Point(0.5, 0.5);
            v.RenderTransform = new ScaleTransform(1, 1);
            v.Visibility = Visibility.Collapsed;
            v.Opacity = 0;
        }
        IdleView.Visibility = Visibility.Visible;
        IdleView.Opacity = 1;

        Host.Clip = _clip;
        ArtSmall.Background = ArtToast.Background = ArtBig.Background = _art;
        EqSmall.Fill = EqToast.Fill = EqBig.Fill = _accent;
        SwitchTrack.Background = _switchBrush;
        _lyric = LyricA;
        _scale.Tune(320, 20);
        _offset.Tune(260, 26);
        _r.Tune(300, 30);
        _seekX.Tune(170, 26);
        _seekH.Tune(420, 26);
        _split.Tune(260, 22);
        _bubbleScale.Tune(320, 20);

        // debug aid: `DynamicIsland.exe --view MediaBig` pins one state, `--timer 90` starts a 90 s countdown
        string[] args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--view");
        if (flag >= 0 && flag + 1 < args.Length && Enum.TryParse(args[flag + 1], true, out View forced)) _forced = forced;
        flag = Array.IndexOf(args, "--timer");
        if (flag >= 0 && flag + 1 < args.Length && double.TryParse(args[flag + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            _forcedTimer = seconds;

        _media = new MediaService(Dispatcher);
        _media.Changed += OnMediaChanged;
        _lyrics.Changed += () => UpdateLyric();
        _network = new NetworkService(Dispatcher);
        _network.Changed += OnNetworkChanged;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => Tick();
        _transientTimer = new DispatcherTimer();
        _transientTimer.Tick += (_, _) =>
        {
            _transientTimer.Stop();
            _transient = null;
            Quiet();
            UpdateView();
        };
        _collapseTimer = new DispatcherTimer { Interval = CollapseDelay };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_hover) return;
            _panel = Panel.None;
            UpdateView();
        };

        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // no Alt-Tab entry, and clicking the island never steals focus from the app you're in
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        InitializeDesktopControls();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Place();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        UpdateClock();
        UpdateSwitch(false);
        Intro();
        _tick.Start();
        if (_forcedTimer > 0) StartTimer(TimeSpan.FromSeconds(_forcedTimer));

        try { await _media.StartAsync(); }
        catch (Exception ex) { App.Log(ex); }
        try { await _network.StartAsync(); }
        catch (Exception ex) { App.Log(ex); }
    }

    void Place()
    {
        ApplyDesktopLayout();
    }

    void Intro()
    {
        _scale.Value = 0.3;
        _offset.Value = -50;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)));
        UpdateView();
        SetTargets();
    }

    // ───────────────────────── state ─────────────────────────

    bool MediaActive => _media.HasTrack && (_media.IsPlaying || DateTime.UtcNow - _lastPlaying < PausedGrace);

    void UpdateView()
    {
        View target = _forced ?? _panel switch
        {
            Panel.Menu => View.Menu,
            Panel.TimerSet => View.TimerSet,
            Panel.Timer when _timer.Active => View.TimerBig,
            Panel.Timer or Panel.Player => _media.HasTrack ? View.MediaBig : View.IdleBig,
            // the music keeps the pill; a running timer takes it only when nothing plays (otherwise it is the bubble)
            _ => _transient ?? (MediaActive ? View.Media : _timer.Active ? View.Timer : View.Idle),
        };
        if (target == View.Toast && !_media.HasTrack) target = View.Idle;
        if (target == _current)
        {
            SyncEq();
            return;
        }

        Dims from = SizeOf(_current), to = SizeOf(target);
        bool growing = to.W * to.H >= from.W * from.H;
        // overshoot a little when growing, settle firmly when shrinking
        _w.Tune(growing ? 300 : 340, growing ? 22 : 30);
        _h.Tune(growing ? 300 : 340, growing ? 22 : 30);

        _current = target;
        Swap(_views[target]);
        SetTargets();

        SyncEq();
        if (target == View.MediaBig) StartSeek();
        if (target == View.Media) UpdateLyric(true);
    }

    bool EqVisible => _current is View.Media or View.Toast or View.MediaBig;

    // the bars only cost frames (and audio capture) while they are on screen; once paused they settle and stop
    void SyncEq()
    {
        _spectrum.Active = EqVisible && _media.IsPlaying;
        if (!EqVisible || _eqRunning) return;
        _eqRunning = true;
        _eqFrame = _time.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnEqFrame;
    }

    /// <param name="force">Too important to skip: closes whatever is open instead of giving way to it.</param>
    void ShowTransient(View view, double seconds, bool force = false)
    {
        if (force) _panel = Panel.None;
        else if (_panel != Panel.None || _hidden) return;
        _transient = view;
        _transientTimer.Stop();
        _transientTimer.Interval = TimeSpan.FromSeconds(seconds);
        _transientTimer.Start();
        UpdateView();
    }

    Dims SizeOf(View view) => view == View.Media ? Sizes[view] with { W = _mediaWidth } : Sizes[view];

    void SetTargets()
    {
        Dims d = SizeOf(_current);
        bool compact = d.H < 40;
        _w.Target = d.W;
        _h.Target = d.H;
        _r.Target = d.R;
        // the timer splits off whenever the compact pill is showing something else
        _split.Target = _timer.Active && compact && _current != View.Timer ? 1 : 0;
        // a ringing timer shows itself even over a fullscreen app
        _offset.Target = _hidden && !_ringing ? -(d.H + 30) : 0;
        _scale.Target = _pressed ? (compact ? 0.93 : 0.975) : _hover && compact ? 1.07 : 1;
        _bubbleScale.Target = _bubblePressed ? 0.93 : _bubbleHover ? 1.07 : 1;
        Animate();
    }

    // ───────────────────────── animation ─────────────────────────

    void Animate()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _time.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double now = _time.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastFrame, 0.05);
        _lastFrame = now;
        if (dt <= 0) return;

        bool moving = _w.Advance(dt);
        moving |= _h.Advance(dt);
        moving |= _r.Advance(dt);
        moving |= _scale.Advance(dt);
        moving |= _offset.Advance(dt);
        moving |= _split.Advance(dt);
        moving |= _bubbleScale.Advance(dt);
        ApplyShape();

        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    void ApplyShape()
    {
        double w = Math.Max(_w.Value, 24), h = Math.Max(_h.Value, 24);
        double r = Math.Clamp(_r.Value, 0, Math.Min(w, h) / 2);

        Pill.Width = Shadow.Width = w;
        Pill.Height = Shadow.Height = h;
        Pill.CornerRadius = Shadow.CornerRadius = new CornerRadius(r);
        Shadow.Opacity = Math.Clamp((h - 40) / 50, 0, 1);

        // the compact player is the one view that changes size on its own: keep its ends on the pill's ends
        if (_current == View.Media) MediaView.Width = w;

        _clip.Rect = new Rect((HostWidth - w) / 2, 0, w, h);
        _clip.RadiusX = _clip.RadiusY = r;

        double scale = Math.Max(_scale.Value, 0.01);
        IslandScale.ScaleX = IslandScale.ScaleY = scale;
        RootMove.Y = _offset.Value;

        // the bubble rides the pill's right end: hidden behind it, then out past the gap, its content fading in last.
        // It follows that end as the pill swells under the pointer, but keeps its own size
        double split = _split.Value;
        Bubble.Visibility = split > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        BubbleMove.X = (w * scale - BubbleWidth) / 2 + (BubbleGap + BubbleWidth) * split;
        BubbleScale.ScaleX = BubbleScale.ScaleY = Math.Max(_bubbleScale.Value, 0.01);
        BubbleBody.Opacity = Math.Clamp(split * 2 - 1, 0, 1);
    }

    void Swap(FrameworkElement next)
    {
        foreach (FrameworkElement v in _views.Values)
            if (v != next && v.Visibility == Visibility.Visible) FadeOut(v);
        FadeIn(next);
    }

    void FadeOut(FrameworkElement v)
    {
        v.IsHitTestVisible = false;
        var blur = new BlurEffect { Radius = 0 };
        v.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(12, Ms(170)));

        var scale = (ScaleTransform)v.RenderTransform;
        var shrink = new DoubleAnimation(0.9, Ms(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        var fade = new DoubleAnimation(0, Ms(140));
        fade.Completed += (_, _) =>
        {
            if (_views[_current] == v) return;
            v.Visibility = Visibility.Collapsed;
            v.Effect = null;
        };
        v.BeginAnimation(OpacityProperty, fade);
    }

    void FadeIn(FrameworkElement v)
    {
        bool fresh = v.Visibility != Visibility.Visible || v.Opacity < 0.05;
        v.Visibility = Visibility.Visible;
        v.IsHitTestVisible = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TimeSpan delay = TimeSpan.FromMilliseconds(70);

        var blur = new BlurEffect { Radius = 12 };
        v.Effect = blur;
        var sharpen = new DoubleAnimation(0, Ms(300)) { BeginTime = delay, EasingFunction = ease };
        sharpen.Completed += (_, _) =>
        {
            // drop the effect so text is rendered crisp again
            if (ReferenceEquals(v.Effect, blur)) v.Effect = null;
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);

        var scale = (ScaleTransform)v.RenderTransform;
        var grow = new DoubleAnimation(1, Ms(380)) { BeginTime = delay, EasingFunction = ease };
        if (fresh) grow.From = 0.86;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        v.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(240)) { BeginTime = delay });
    }

    static Duration Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // ───────────────────────── periodic work ─────────────────────────

    void Tick()
    {
        _ticks++;
        PollVolume();
        if (_ticks % HeadsetEvery == 1) ReadHeadset(_audio.Device);
        UpdateLyric();
        UpdateTimer();
        RefreshDesktopVisibility();
        if (_ticks % 5 == 0) CheckFullscreen();
        if (_ticks % 10 == 0)
        {
            UpdateClock();
            PollPower();
            Native.KeepOnTop(_hwnd);
            if (_media.IsPlaying) _lastPlaying = DateTime.UtcNow;
            UpdateView();
        }
    }

    void OnEqFrame(object? sender, EventArgs e)
    {
        double now = _time.Elapsed.TotalSeconds;
        double dt = now - _eqFrame;
        if (dt < EqFrame) return;
        _eqFrame = now;
        dt = Math.Min(dt, 0.05);

        bool playing = _media.IsPlaying;
        // no loopback capture (exotic device format): fall back to the plain output peak
        float[]? bands = _spectrum.Read(_bands) ? _bands : null;
        double level = 0;
        if (bands == null)
        {
            float peak = _audio.Peak();
            level = peak < 0 ? 0.4 : peak;
        }

        bool moving = EqSmall.Tick(bands, level, playing, now, dt);
        moving |= EqToast.Tick(bands, level, playing, now, dt);
        moving |= EqBig.Tick(bands, level, playing, now, dt);

        moving |= Glow.Tick(EqBig, now, dt);

        if (!EqVisible || (!playing && !moving))
        {
            CompositionTarget.Rendering -= OnEqFrame;
            _eqRunning = false;
        }
    }

    void UpdateClock()
    {
        DateTime now = DateTime.Now;
        IdleClock.Text = BigClock.Text = now.ToString("HH:mm");
        BigDate.Text = now.ToString("dddd, d MMMM", Ru);
    }

    void CheckFullscreen()
    {
        bool hidden = Native.IsForegroundFullscreen(_hwnd);
        if (hidden == _hidden) return;
        _hidden = hidden;
        SetTargets();
        RefreshDesktopVisibility();
    }

    void PollVolume()
    {
        if (!_audio.TryGetVolume(out float level, out bool muted)) return;
        if (_audio.TakeSwitch(out AudioService.Output device))
        {
            // another device has its own level: that is not a volume change, so no HUD on top of the notice
            _lastVolume = -1;
            ReadHeadset(device, true);
        }
        bool first = _lastVolume < 0;
        if (!first && Math.Abs(level - _lastVolume) < 0.004 && muted == _lastMuted) return;

        _lastVolume = level;
        _lastMuted = muted;

        int percent = (int)Math.Round(level * 100);
        string icon = muted || percent == 0 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        VolIcon.Text = InfoVolIcon.Text = icon;
        VolText.Text = percent.ToString();
        InfoVol.Text = muted ? "выкл" : percent + "%";
        VolFill.BeginAnimation(WidthProperty, new DoubleAnimation(muted ? 0 : VolumeTrack * level, Ms(first ? 0 : 140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        if (!first) ShowTransient(View.Volume, 1.6);
    }

    void PollPower()
    {
        if (!Native.TryGetBattery(out int percent, out bool plugged))
        {
            InfoBatRow.Visibility = Visibility.Collapsed;
            return;
        }

        InfoBatRow.Visibility = Visibility.Visible;
        InfoBat.Text = percent + "%";
        InfoBat.Foreground = plugged ? ChargeText.Foreground : Brushes.White;

        if (_powerKnown && plugged && !_lastPlugged)
        {
            ChargeText.Text = percent + "%";
            ChargeFill.BeginAnimation(WidthProperty, new DoubleAnimation(0, 20 * percent / 100.0, Ms(700))
            {
                BeginTime = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ShowTransient(View.Charge, 3);
        }
        _powerKnown = true;
        _lastPlugged = plugged;
    }

    /// <summary>Puts the charge of the output device (Bluetooth headphones report one) into the expanded views.</summary>
    /// <param name="announce">The device has just taken over the sound: introduce it, charge included.</param>
    async void ReadHeadset(AudioService.Output? output, bool announce = false)
    {
        int level = output is { } bound ? await Headset.ChargeAsync(bound.Container) : -1;
        // swapped while this was being read: the new device gets a read of its own
        if (output != _audio.Device) return;

        AudioService.Output device = output ?? default;
        // another device's charge is nothing to compare with
        int was = device.Container == _headsetId ? _headset : -1;
        _headset = level;
        _headsetId = device.Container;

        bool known = level >= 0, low = known && level <= HeadsetLow;
        string icon = device.Headphones ? "" : "";
        Brush red = (Brush)FindResource("Red");
        InfoHeadsetRow.Visibility = PlayerHeadset.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        InfoHeadsetIcon.Text = PlayerHeadsetIcon.Text = icon;
        InfoHeadset.Text = PlayerHeadsetText.Text = level + "%";
        InfoHeadset.Foreground = low ? red : Brushes.White;
        PlayerHeadsetIcon.Foreground = PlayerHeadsetText.Foreground = low ? red : (Brush)FindResource("Dim");
        if (output == null) return;

        string name = device.Name.Length > 0 ? device.Name : "Вывод звука";
        if (known) name += " · " + level + "%";
        bool Passed(int mark) => was > mark && level <= mark;
        if (announce)
            Notify(icon, Brushes.White, device.Kind.Length > 0 ? device.Kind : "Аудиоустройство", name);
        else if (known && (Passed(HeadsetLow) || Passed(HeadsetCritical)))
            Notify(icon, red, "Низкий заряд", name);
    }

    // ───────────────────────── notices ─────────────────────────

    /// <summary>One-off notice in the pill: an icon, a title and a line of detail.</summary>
    void Notify(string icon, Brush tint, string title, string text, double seconds = 3.2, bool force = false)
    {
        // nothing talks over a ringing timer
        if (_ringing && !force) return;

        bool shown = _current == View.Notice;
        NoticeIcon.Text = icon;
        NoticeIcon.Foreground = tint;
        NoticeTitle.Text = title;
        NoticeText.Text = text;
        ShowTransient(View.Notice, seconds, force);
        // one notice replacing another: no view change to animate, so blur the new text in
        if (shown) FadeIn(NoticeView);
    }

    void OnNetworkChanged(NetworkService.State was, NetworkService.State now)
    {
        if (now.Vpn != was.Vpn)
        {
            string[] before = was.Vpn.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] after = now.Vpn.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (after.Except(before).FirstOrDefault() is { } up)
                Notify("", (Brush)FindResource("Green"), "VPN включён", up);
            else if (before.Except(after).FirstOrDefault() is { } down)
                Notify("", (Brush)FindResource("Dim"), "VPN отключён", down);
            // a tunnel going up or down also reshuffles the connection underneath: one notice is enough
            return;
        }

        if (now.Link == NetworkService.Link.None)
        {
            Notify("", (Brush)FindResource("Red"), "Нет сети", "Подключение потеряно");
            return;
        }

        bool wifi = now.Link == NetworkService.Link.Wifi;
        string title = now.Link switch
        {
            NetworkService.Link.Wired => "Ethernet",
            _ when now.Name.Length > 0 => now.Name,
            NetworkService.Link.Wifi => "Wi-Fi",
            _ => "Мобильная сеть",
        };
        if (now.Internet)
            Notify(wifi ? "" : "", (Brush)FindResource("Green"), title, wifi ? "Wi-Fi подключён" : "Сеть подключена");
        else
            Notify(wifi ? "" : "", (Brush)FindResource("Orange"), title, "Без доступа к интернету");
    }

    // ───────────────────────── timer ─────────────────────────

    void StartTimer(TimeSpan total)
    {
        _timer.Start(total);
        BigTimerLabel.Text = "Таймер · " + Span(total);
        _panel = Panel.None;
        SyncTimer();
        UpdateView();
        SetTargets();
    }

    void StopTimer()
    {
        _timer.Stop();
        MenuTimer.Text = "";
    }

    static string Span(TimeSpan t) =>
        t.TotalSeconds >= 60 ? (int)Math.Round(t.TotalMinutes) + " мин" : (int)t.TotalSeconds + " с";

    // running or paused: the pause button and how bright the digits are
    void SyncTimer()
    {
        bool running = _timer.Running;
        TimerPauseIcon.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TimerPlayIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        TimerText.Opacity = BubbleText.Opacity = BigTimer.Opacity = running ? 1 : 0.5;
        _timerShown = -1;
        UpdateTimer();
    }

    // the countdown shows in the compact pill, the bubble, the expanded view and the menu row
    void UpdateTimer()
    {
        if (!_timer.Active) return;
        TimeSpan left = _timer.Left;
        if (left <= TimeSpan.Zero)
        {
            TimerDone();
            return;
        }

        TimerRing.Progress = BubbleRing.Progress = _timer.Share;
        // round up, so it opens on the full time and hits 0:00 as it rings
        int seconds = (int)Math.Ceiling(left.TotalSeconds);
        if (seconds == _timerShown) return;
        _timerShown = seconds;
        TimerText.Text = BubbleText.Text = BigTimer.Text = MenuTimer.Text = Format(TimeSpan.FromSeconds(seconds));
    }

    void TimerDone()
    {
        string total = Span(_timer.Total);
        StopTimer();
        _ringing = true;
        _alarm.Ring();

        var pulse = new DoubleAnimation(1, 1.2, Ms(420))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        NoticePulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        NoticePulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        Notify("", (Brush)FindResource("Orange"), "Таймер", "Время вышло · " + total, 12, true);
        SetTargets();
    }

    /// <summary>Silences the alarm of a finished timer.</summary>
    void Quiet()
    {
        if (!_ringing) return;
        _ringing = false;
        _alarm.Stop();
        NoticePulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NoticePulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    void SetMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, MaxMinutes);
        SetupText.Down = minutes < _minutes; // the digits roll the way the number goes
        _minutes = minutes;
        SetupText.Text = _minutes + ":00";
    }

    void TimerRow_Click(object sender, RoutedEventArgs e)
    {
        _panel = _timer.Active ? Panel.Timer : Panel.TimerSet;
        UpdateView();
    }

    void TimerLess_Click(object sender, RoutedEventArgs e) => SetMinutes(_minutes - 1);
    void TimerMore_Click(object sender, RoutedEventArgs e) => SetMinutes(_minutes + 1);
    void TimerPreset_Click(object sender, RoutedEventArgs e) => SetMinutes(int.Parse((string)((Button)sender).Tag));
    void TimerStart_Click(object sender, RoutedEventArgs e) => StartTimer(TimeSpan.FromMinutes(_minutes));

    void TimerToggle_Click(object sender, RoutedEventArgs e)
    {
        _timer.Toggle();
        SyncTimer();
    }

    void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        _panel = Panel.None;
        UpdateView();
        SetTargets();
    }

    // the bubble has its own hover and press: pointing at one of the two must not move the other

    void Bubble_MouseEnter(object sender, MouseEventArgs e)
    {
        _bubbleHover = true;
        SetTargets();
    }

    void Bubble_MouseLeave(object sender, MouseEventArgs e)
    {
        _bubbleHover = _bubblePressed = false;
        SetTargets();
    }

    void Bubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // handled: the press stays with the bubble instead of squeezing the pill
        e.Handled = true;
        _bubblePressed = true;
        SetTargets();
    }

    void Bubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_bubblePressed) return;
        // the bubble opens its own activity, not the pill's
        e.Handled = true;
        _bubblePressed = false;
        Open(Panel.Timer);
        UpdateView();
        SetTargets();

        // the expanded timer may not reach as far as the pointer: give it time to get there before closing again
        _collapseTimer.Interval = BubbleLinger;
        _collapseTimer.Start();
    }

    // ───────────────────────── media ─────────────────────────

    void OnMediaChanged()
    {
        string title = _media.HasTrack ? _media.Title : "";
        TitleBig.Text = ToastTitle.Text = title;
        ArtistBig.Text = ToastArtist.Text = string.IsNullOrWhiteSpace(_media.Artist) ? "Неизвестный исполнитель" : _media.Artist;

        if (!ReferenceEquals(_art.ImageSource, _media.Art))
        {
            _art.ImageSource = _media.Art;
            _art.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, Ms(350)));
            var tint = new ColorAnimation(_media.Accent, Ms(450));
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, tint);
            Glow.BeginAnimation(Aura.ColorProperty, tint);
        }

        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (_media.IsPlaying) _lastPlaying = DateTime.UtcNow;

        bool newTrack = title.Length > 0 && title != _lastTitle;
        _lastTitle = title;
        if (newTrack && _media.IsPlaying) ShowTransient(View.Toast, 3.2);
        _lyrics.Track(title, _media.Artist);
        UpdateView();
        UpdateLyric();
    }

    // ───────────────────────── lyrics ─────────────────────────

    // the fade is a fixed strip at each end, whatever the current width of the box
    void LyricBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(e.NewSize.Width, 2 * LyricEdge);
        LyricMask.EndPoint = new Point(width, 0);
        LyricMaskIn.Offset = LyricEdge / width;
        LyricMaskOut.Offset = 1 - LyricEdge / width;
    }

    /// <param name="snap">The view is just appearing: drop the stale line instead of animating it away.</param>
    void UpdateLyric(bool snap = false)
    {
        if (_current != View.Media) return;

        LyricsService.Line[] lines = _lyrics.For(_media.Duration);
        TimeSpan at = _media.Position + LyricLead;
        int index = lines.Length - 1;
        while (index >= 0 && lines[index].Time > at) index--;

        string title = _media.HasTrack ? _media.Title : "";
        string text = index < 0 ? "" : lines[index].Text;
        TimeSpan end = index + 1 < lines.Length ? lines[index + 1].Time : _media.Duration;
        double seconds = (end - at).TotalSeconds;
        // no lyrics, the intro, or a long break: the track name takes the place of the line
        bool named = text.Length == 0 && (index < 0 || seconds >= LyricGap);
        if (ReferenceEquals(lines, _lyricLines) && index == _lyricIndex && (!named || title == _lyricTitle)) return;

        _lyricLines = lines;
        _lyricIndex = index;
        _lyricTitle = title;
        ShowLyric(named ? title : text, seconds, snap, named);
    }

    /// <summary>Slides the previous line up and out, the new one in from below; a line that does not fit scrolls while it is sung.</summary>
    void ShowLyric(string text, double seconds, bool snap, bool named)
    {
        TextBlock old = _lyric;
        // already on screen: nothing to clear, or the same track name (lyrics arriving mid-intro)
        if (text == old.Text && (text.Length == 0 || (named && _lyricNamed))) return;
        TextBlock next = _lyric = old == LyricA ? LyricB : LyricA;
        _lyricNamed = named;

        var leave = (TranslateTransform)old.RenderTransform;
        var blurOut = new BlurEffect { Radius = 0 };
        old.Effect = blurOut;
        blurOut.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(6, Ms(snap ? 0 : 220)));
        leave.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, Ms(snap ? 0 : 260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        old.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(snap ? 0 : 200)));

        // the name is dimmed so it never reads as a lyric, and is cut with an ellipsis rather than scrolled
        next.Foreground = named ? (Brush)FindResource("Dim") : Brushes.White;
        next.TextTrimming = named ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        next.MaxWidth = named ? MediaNameWidth - LyricInset - 2 * LyricEdge : double.PositiveInfinity;
        next.Text = text;
        next.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = next.DesiredSize.Width;
        Canvas.SetTop(next, Math.Round((LyricBox.Height - next.DesiredSize.Height) / 2));

        // the pill stretches to the line; only a line longer than the widest pill has to scroll
        _mediaWidth = text.Length == 0
            ? MediaWidth
            : Math.Clamp(width + 2 * LyricEdge + LyricInset, MediaWidth, MediaMaxWidth);
        double box = _mediaWidth - LyricInset;
        double overflow = width - (box - 2 * LyricEdge);
        if (!snap) _w.Tune(280, 30); // line to line the pill glides, no bounce
        SetTargets();

        var enter = (TranslateTransform)next.RenderTransform;
        DoubleAnimationUsingKeyFrames? scroll = null;
        if (overflow > 0)
        {
            // hold the start for a moment, then reach the end shortly before the next line comes in
            double hold = Math.Min(0.6, seconds * 0.2);
            double run = Math.Min(overflow / LyricSpeed, Math.Max(seconds - hold - 0.5, 0.6));
            // keyframes, not BeginTime: while a delayed animation waits, the block sits at its previous scroll offset
            scroll = new DoubleAnimationUsingKeyFrames { Duration = Ms((hold + run) * 1000) };
            scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(LyricEdge, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(LyricEdge, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))));
            scroll.KeyFrames.Add(new EasingDoubleKeyFrame(LyricEdge - overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + run)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }
        enter.X = overflow > 0 ? LyricEdge : (box - width) / 2;
        enter.BeginAnimation(TranslateTransform.XProperty, scroll);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var blurIn = new BlurEffect { Radius = 6 };
        next.Effect = blurIn;
        var sharpen = new DoubleAnimation(0, Ms(300)) { EasingFunction = ease };
        sharpen.Completed += (_, _) =>
        {
            if (ReferenceEquals(next.Effect, blurIn)) next.Effect = null;
        };
        blurIn.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
        enter.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Ms(380)) { EasingFunction = ease });
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(300)));
    }

    // ───────────────────────── seek bar ─────────────────────────

    // the bar runs on its own frames while the expanded player is open: it sweeps in from empty,
    // glides to wherever playback jumps, and swells under the pointer
    void StartSeek()
    {
        _seekX.Value = _seekX.Velocity = 0;
        if (_seekRunning) return;
        _seekRunning = true;
        _seekFrame = _time.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnSeekFrame;
    }

    void OnSeekFrame(object? sender, EventArgs e)
    {
        double now = _time.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _seekFrame, 0.05);
        _seekFrame = now;
        if (_current != View.MediaBig && !_scrubbing)
        {
            CompositionTarget.Rendering -= OnSeekFrame;
            _seekRunning = false;
            return;
        }
        if (dt <= 0) return;

        TimeSpan duration = _media.Duration;
        bool known = duration.TotalSeconds >= 1;
        double played = known ? Math.Clamp(_media.Position / duration, 0, 1) : 0;
        // after a drop the player takes a moment to report the new position: don't flick back meanwhile
        if (!_scrubbing && now < _scrubUntil && Math.Abs(played - _scrub) < 0.02) _scrubUntil = 0;
        double shown = _scrubbing || now < _scrubUntil ? _scrub : played;

        double track = SeekArea.ActualWidth > 0 ? SeekArea.ActualWidth : SeekTrack;
        _seekX.Target = track * shown;
        _seekH.Target = _scrubbing ? SeekDrag : SeekArea.IsMouseOver ? SeekHover : SeekThin;
        _seekX.Advance(dt);
        _seekH.Advance(dt);

        double thick = Math.Max(_seekH.Value, 2);
        SeekBar.Height = thick;
        SeekBack.CornerRadius = SeekFill.CornerRadius = new CornerRadius(thick / 2);
        SeekFill.Width = Math.Clamp(_seekX.Value, 0, track);

        // while scrubbing the labels read the spot under the pointer
        TimeSpan at = duration * shown;
        var label = known ? ((int)at.TotalSeconds, (int)duration.TotalSeconds) : (-1, -1);
        if (label == _seekLabel) return;
        _seekLabel = label;
        PosText.Text = known ? Format(at) : "–:––";
        RemText.Text = known ? "-" + Format(duration - at) : "–:––";
    }

    static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    void Play_Click(object sender, RoutedEventArgs e) => _media.TogglePlay();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    double SeekFraction(MouseEventArgs e) => Math.Clamp(e.GetPosition(SeekArea).X / SeekArea.ActualWidth, 0, 1);

    void Seek_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SeekArea.ActualWidth <= 0 || _media.Duration.TotalSeconds < 1) return;

        _scrubbing = true;
        _scrub = SeekFraction(e);
        _seekX.Tune(900, 60); // stick to the pointer
        PosText.Foreground = RemText.Foreground = Brushes.White;
        SeekArea.CaptureMouse();
    }

    void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing) _scrub = SeekFraction(e);
    }

    void Seek_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // swallow the release so it doesn't collapse the island
        e.Handled = true;
        if (!_scrubbing) return;

        EndScrub();
        _scrubUntil = _time.Elapsed.TotalSeconds + 1;
        _media.Seek(_scrub);
        SeekArea.ReleaseMouseCapture();
    }

    // capture taken away mid-drag: let go without seeking
    void Seek_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_scrubbing) EndScrub();
    }

    void EndScrub()
    {
        _scrubbing = false;
        _seekX.Tune(170, 26);
        PosText.Foreground = RemText.Foreground = (Brush)FindResource("Dim");
    }

    // ───────────────────────── pointer ─────────────────────────

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _hover = true;
        _collapseTimer.Stop();
        SetTargets();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover = _pressed = false;
        SetTargets();
        if (_panel == Panel.None) return;
        _collapseTimer.Interval = CollapseDelay;
        _collapseTimer.Start();
    }

    void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        SetTargets();
    }

    void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;

        // a click silences a ringing timer, closes whatever is open, or opens what the pill is showing
        if (_ringing || _panel != Panel.None) Open(Panel.None);
        else Open(MediaActive || !_timer.Active ? Panel.Player : Panel.Timer);
        UpdateView();
        SetTargets();
    }

    void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Open(_panel == Panel.Menu ? Panel.None : Panel.Menu);
        UpdateSwitch(false);
        UpdateView();
    }

    void Open(Panel panel)
    {
        _panel = panel;
        _transient = null;
        _transientTimer.Stop();
        Quiet();
    }

    void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_current == View.TimerSet) SetMinutes(_minutes + (e.Delta > 0 ? 1 : -1));
        else _audio.Nudge(e.Delta > 0 ? 0.02f : -0.02f);
        e.Handled = true;
    }

    // ───────────────────────── menu ─────────────────────────

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(!Autostart.Enabled); }
        catch (Exception ex) { App.Log(ex); }
        UpdateSwitch(true);
    }

    void UpdateSwitch(bool animate)
    {
        bool on = Autostart.Enabled;
        Duration d = Ms(animate ? 220 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _switchBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? SwitchOn : SwitchOff, d));
        SwitchKnob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 16 : 0, d) { EasingFunction = ease });
    }

    void Exit_Click(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _alarm.Stop();
        if (!DesktopVisible)
        {
            Application.Current.Shutdown();
            return;
        }
        var fade = new DoubleAnimation(0, Ms(220));
        fade.Completed += (_, _) => Application.Current.Shutdown();
        Root.BeginAnimation(OpacityProperty, fade);
        _scale.Target = _bubbleScale.Target = 0.5;
        Animate();
    }
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DynamicIsland;

/// <summary>Now-playing info from whatever app owns the system media session (Spotify, browser, ...).</summary>
sealed class MediaService
{
    readonly Dispatcher _ui;
    Manager? _manager;
    Session? _session;
    int _version;

    TimeSpan _position, _duration;
    DateTime _positionAt = DateTime.UtcNow;
    DateTimeOffset _timelineStamp;
    double _rate = 1;

    public MediaService(Dispatcher ui) => _ui = ui;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource? Art { get; private set; }
    public Color Accent { get; private set; } = Colors.White;
    public bool IsPlaying { get; private set; }
    public bool HasTrack => _session != null && Title.Length > 0;
    public TimeSpan Duration => _duration;

    public TimeSpan Position
    {
        get
        {
            TimeSpan p = _position;
            if (IsPlaying) p += (DateTime.UtcNow - _positionAt) * _rate;
            if (p < TimeSpan.Zero) return TimeSpan.Zero;
            return p > _duration ? _duration : p;
        }
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        _manager = await Manager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(Attach);
        _manager.SessionsChanged += (_, _) => _ui.InvokeAsync(Attach);
        Attach();
    }

    void Attach()
    {
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnProperties;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }
        }

        _session = Pick();
        _timelineStamp = default;

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnProperties;
            _session.PlaybackInfoChanged += OnPlayback;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        _ = RefreshAsync();
    }

    Session? Pick()
    {
        try
        {
            Session? current = _manager?.GetCurrentSession();
            if (current != null && Playing(current)) return current;
            foreach (Session s in _manager!.GetSessions())
                if (Playing(s)) return s;
            return current;
        }
        catch
        {
            return null;
        }
    }

    static bool Playing(Session s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == Status.Playing; }
        catch { return false; }
    }

    void OnProperties(Session s, MediaPropertiesChangedEventArgs e) => _ui.InvokeAsync(() => _ = RefreshAsync());

    void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => _ui.InvokeAsync(() =>
    {
        ReadPlayback();
        ReadTimeline();
        Changed?.Invoke();
    });

    void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => _ui.InvokeAsync(() =>
    {
        ReadTimeline();
        Changed?.Invoke();
    });

    async Task RefreshAsync()
    {
        Session? session = _session;
        int version = ++_version;

        if (session == null)
        {
            Title = Artist = "";
            Art = null;
            Accent = Colors.White;
            IsPlaying = false;
            Changed?.Invoke();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;

            string title = props.Title ?? "";
            bool sameTrack = title == Title;
            ImageSource? art = null;
            Color accent = Colors.White;

            if (props.Thumbnail != null)
            {
                try
                {
                    using var source = await props.Thumbnail.OpenReadAsync();
                    using var stream = source.AsStreamForRead();
                    var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    buffer.Position = 0;
                    (art, accent) = Decode(buffer);
                }
                catch { }
                if (version != _version) return;
            }

            Title = title;
            Artist = props.Artist ?? "";
            // browsers briefly drop the thumbnail while updating metadata — keep the old one
            if (art != null || !sameTrack)
            {
                Art = art;
                Accent = accent;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        ReadPlayback();
        ReadTimeline();
        Changed?.Invoke();
    }

    void ReadPlayback()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == Status.Playing;
            if (playing != IsPlaying)
            {
                // freeze / resume our own clock: not every app pushes a timeline update here
                _position = Position;
                _positionAt = DateTime.UtcNow;
                IsPlaying = playing;
            }
            _rate = info.PlaybackRate ?? 1;
        }
        catch { }
    }

    void ReadTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            _duration = t.EndTime - t.StartTime;
            if (t.LastUpdatedTime == _timelineStamp) return;

            _timelineStamp = t.LastUpdatedTime;
            _position = t.Position - t.StartTime;
            DateTime at = t.LastUpdatedTime.UtcDateTime;
            _positionAt = at.Year < 2000 ? DateTime.UtcNow : at;
        }
        catch { }
    }

    public async void TogglePlay()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
        catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); }
        catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); }
        catch { }
    }

    public async void Seek(double fraction)
    {
        Session? session = _session;
        if (session == null || _duration <= TimeSpan.Zero) return;
        try
        {
            var target = TimeSpan.FromTicks((long)(_duration.Ticks * Math.Clamp(fraction, 0, 1)));
            TimeSpan start = session.GetTimelineProperties().StartTime;
            if (await session.TryChangePlaybackPositionAsync((start + target).Ticks))
            {
                _position = target;
                _positionAt = DateTime.UtcNow;
                Changed?.Invoke();
            }
        }
        catch { }
    }

    static (ImageSource, Color) Decode(MemoryStream data)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 192;
        bitmap.StreamSource = data;
        bitmap.EndInit();
        bitmap.Freeze();
        return (bitmap, AccentOf(bitmap));
    }

    /// <summary>Saturation-weighted average of the cover, lifted so it reads on black.</summary>
    static Color AccentOf(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(source,
                new ScaleTransform(24.0 / source.PixelWidth, 24.0 / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                double min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                double sat = max == 0 ? 0 : (max - min) / max;
                double weight = sat * sat * (max / 255) + 0.01;
                b += px[i] * weight;
                g += px[i + 1] * weight;
                r += px[i + 2] * weight;
                total += weight;
            }
            r /= total; g /= total; b /= total;

            double peak = Math.Max(r, Math.Max(g, b));
            if (peak < 1) return Colors.White;
            double lift = 235 / peak;
            r *= lift; g *= lift; b *= lift;

            // keep it from going fully neon: pull a little towards white
            const double white = 0.18;
            return Color.FromRgb(
                (byte)(r + (255 - r) * white),
                (byte)(g + (255 - g) * white),
                (byte)(b + (255 - b) * white));
        }
        catch
        {
            return Colors.White;
        }
    }
}

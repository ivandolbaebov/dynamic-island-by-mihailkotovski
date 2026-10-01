using System.IO;
using System.Media;

namespace DynamicIsland;

/// <summary>The island's one countdown.</summary>
sealed class Countdown
{
    TimeSpan _left;
    DateTime _resumed;

    public TimeSpan Total { get; private set; }

    /// <summary>Set and not finished yet: counting down or paused.</summary>
    public bool Active { get; private set; }

    public bool Running { get; private set; }

    public TimeSpan Left
    {
        get
        {
            TimeSpan left = Running ? _left - (DateTime.UtcNow - _resumed) : _left;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Share of the time still to go, 1 → 0.</summary>
    public double Share => Total > TimeSpan.Zero ? Left / Total : 0;

    public void Start(TimeSpan total)
    {
        Total = _left = total;
        _resumed = DateTime.UtcNow;
        Active = Running = true;
    }

    /// <summary>Pause / resume.</summary>
    public void Toggle()
    {
        if (!Active) return;
        if (Running) _left = Left;
        else _resumed = DateTime.UtcNow;
        Running = !Running;
    }

    public void Stop()
    {
        Active = Running = false;
        _left = TimeSpan.Zero;
    }
}

/// <summary>Rings until told to stop: the Windows alarm sound, looped.</summary>
sealed class Alarm
{
    SoundPlayer? _player;

    public void Ring()
    {
        Stop();
        try
        {
            string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
            if (!File.Exists(wav))
            {
                SystemSounds.Exclamation.Play();
                return;
            }
            _player = new SoundPlayer(wav);
            _player.PlayLooping();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public void Stop()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch { }
        _player = null;
    }
}

using System.Runtime.InteropServices;

namespace DynamicIsland;

/// <summary>Live spectrum of whatever the default playback device is outputting (WASAPI loopback).</summary>
sealed class SpectrumService
{
    public const int Bands = SpectrumAnalyzer.Bands;

    const int ERender = 0, EMultimedia = 1, ClsCtxAll = 23;
    const int StreamLoopback = 0x00020000, BufferSilent = 2;
    const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;
    const long BufferDuration = 2_000_000; // 200 ms, in 100 ns units
    const int PollMs = 8, SilenceMs = 80, LingerMs = 3000, RetryMs = 2000, DeviceCheckMs = 2000;

    readonly object _gate = new();
    readonly float[] _bands = new float[Bands];
    readonly float[] _scratch = new float[Bands];
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _active, _failed;

    // capture thread only
    IAudioClient? _client;
    IAudioCaptureClient? _capture;
    SpectrumAnalyzer? _analyzer;
    string? _deviceId;
    int _channels;
    bool _float;
    float[] _floats = Array.Empty<float>();
    short[] _shorts = Array.Empty<short>();
    float[] _mono = Array.Empty<float>();
    long _lastData;
    bool _fresh;

    /// <summary>Capture runs only while someone is watching; it winds down a few seconds after.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!value) return;
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Spectrum", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            _wake.Set();
        }
    }

    /// <summary>Copies the current band powers (bass → treble). False when loopback capture is unavailable.</summary>
    public bool Read(float[] bands)
    {
        if (_failed) return false;
        lock (_gate) Array.Copy(_bands, bands, Bands);
        return true;
    }

    void Run()
    {
        long lastActive = Environment.TickCount64, lastCheck = 0;
        while (true)
        {
            long now = Environment.TickCount64;
            if (_active) lastActive = now;
            else if (now - lastActive > LingerMs)
            {
                Close();
                _wake.WaitOne();
                continue;
            }

            try
            {
                if (_capture == null)
                {
                    _failed = !Open();
                    if (_failed)
                    {
                        Close();
                        Thread.Sleep(RetryMs);
                        continue;
                    }
                    lastCheck = now;
                }
                else if (now - lastCheck > DeviceCheckMs)
                {
                    // headphones plugged in: follow the new default device
                    lastCheck = now;
                    if (DefaultDeviceId() != _deviceId)
                    {
                        Close();
                        continue;
                    }
                }

                if (!Drain())
                {
                    Close();
                    Thread.Sleep(200);
                    continue;
                }
                Analyze();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _failed = true;
                Close();
                Thread.Sleep(RetryMs);
                continue;
            }
            Thread.Sleep(PollMs);
        }
    }

    bool Open()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) != 0 || device == null) return false;
            if (device.GetId(out _deviceId) != 0) return false;

            Guid iid = typeof(IAudioClient).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? client) != 0) return false;
            _client = client as IAudioClient;
            if (_client == null || _client.GetMixFormat(out IntPtr format) != 0) return false;

            int rate;
            try
            {
                int tag = (ushort)Marshal.ReadInt16(format, 0);
                _channels = (ushort)Marshal.ReadInt16(format, 2);
                rate = Marshal.ReadInt32(format, 4);
                int bits = (ushort)Marshal.ReadInt16(format, 14);
                // WAVEFORMATEXTENSIBLE keeps the real tag in the first field of its SubFormat guid
                if (tag == FormatExtensible) tag = (ushort)Marshal.ReadInt16(format, 24);
                _float = tag == FormatFloat && bits == 32;
                if (_channels == 0 || rate <= 0 || !(_float || (tag == FormatPcm && bits == 16))) return false;
                if (_client.Initialize(0, StreamLoopback, BufferDuration, 0, format, IntPtr.Zero) != 0) return false;
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }

            iid = typeof(IAudioCaptureClient).GUID;
            if (_client.GetService(ref iid, out object? capture) != 0) return false;
            _capture = capture as IAudioCaptureClient;
            if (_capture == null || _client.Start() != 0) return false;

            _analyzer = new SpectrumAnalyzer(rate);
            _lastData = 0;
            return true;
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    static string? DefaultDeviceId()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out IMMDevice? device) != 0 || device == null) return null;
            device.GetId(out string? id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    void Close()
    {
        try { _client?.Stop(); }
        catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client != null) Marshal.ReleaseComObject(_client);
        _capture = null;
        _client = null;
        _analyzer = null;
        lock (_gate) Array.Clear(_bands);
    }

    /// <summary>Pulls every pending packet; false once the device is gone.</summary>
    bool Drain()
    {
        while (true)
        {
            if (_capture!.GetNextPacketSize(out uint frames) < 0) return false;
            if (frames == 0) return true;
            if (_capture.GetBuffer(out IntPtr data, out frames, out int flags, out _, out _) < 0) return false;
            if (frames == 0) return true;
            Push(data, (int)frames, (flags & BufferSilent) != 0);
            _capture.ReleaseBuffer(frames);
        }
    }

    void Push(IntPtr data, int frames, bool silent)
    {
        int count = frames * _channels;
        if (_mono.Length < frames) _mono = new float[frames];

        if (silent)
        {
            Array.Clear(_mono, 0, frames);
        }
        else if (_float)
        {
            if (_floats.Length < count) _floats = new float[count];
            Marshal.Copy(data, _floats, 0, count);
            for (int i = 0, s = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _floats[s++];
                _mono[i] = sum / _channels;
            }
        }
        else
        {
            if (_shorts.Length < count) _shorts = new short[count];
            Marshal.Copy(data, _shorts, 0, count);
            for (int i = 0, s = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _shorts[s++];
                _mono[i] = sum / (_channels * 32768f);
            }
        }

        _analyzer!.Push(_mono, frames);
        _lastData = Environment.TickCount64;
        _fresh = true;
    }

    void Analyze()
    {
        // loopback delivers nothing while nobody plays, so the last window would otherwise stick
        if (Environment.TickCount64 - _lastData > SilenceMs)
        {
            lock (_gate) Array.Clear(_bands);
            return;
        }
        if (!_fresh) return;
        _fresh = false;

        _analyzer!.Analyze(_scratch);
        lock (_gate) Array.Copy(_scratch, _bands, Bands);
    }
}

/// <summary>Turns a mono sample stream into the power of log-spaced bands.</summary>
sealed class SpectrumAnalyzer
{
    public const int Bands = 40;

    const double MinHz = 45, MaxHz = 14000;
    const double TiltDb = 2; // per octave: music carries less energy up high, this evens the bands out

    readonly int _size;
    readonly float[] _ring;
    readonly double[] _window, _re, _im, _power;
    readonly double[] _edges = new double[Bands + 1];
    readonly double[] _tilt = new double[Bands];
    int _head;

    public SpectrumAnalyzer(int rate)
    {
        // ~43 ms window: enough resolution for the bass without smearing the beat
        _size = 1024;
        while (_size < rate * 0.04) _size <<= 1;

        _ring = new float[_size];
        _window = new double[_size];
        _re = new double[_size];
        _im = new double[_size];
        _power = new double[_size / 2];
        for (int i = 0; i < _size; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_size - 1));

        double top = Math.Min(MaxHz, rate * 0.45);
        for (int b = 0; b <= Bands; b++)
            _edges[b] = MinHz * Math.Pow(top / MinHz, (double)b / Bands) * _size / rate;
        for (int b = 0; b < Bands; b++)
        {
            double centerHz = Math.Sqrt(_edges[b] * _edges[b + 1]) * rate / _size;
            _tilt[b] = Math.Pow(10, TiltDb * Math.Log2(centerHz / 1000) / 10);
        }
    }

    public void Push(float[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _ring[_head] = samples[i];
            _head = (_head + 1) & (_size - 1);
        }
    }

    /// <summary>Fills <paramref name="bands"/> (bass → treble) with the power of the latest window; a full-scale sine reads about 1.</summary>
    public void Analyze(float[] bands)
    {
        for (int i = 0; i < _size; i++)
        {
            _re[i] = _ring[(_head + i) & (_size - 1)] * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);

        double norm = 16.0 / ((double)_size * _size);
        for (int k = 0; k < _power.Length; k++) _power[k] = (_re[k] * _re[k] + _im[k] * _im[k]) * norm;

        for (int b = 0; b < Bands; b++)
        {
            // bin k covers [k - 0.5, k + 0.5): weight each by its overlap with the band
            double lo = _edges[b], hi = _edges[b + 1], sum = 0;
            int last = Math.Min((int)(hi + 0.5), _power.Length - 1);
            for (int k = (int)(lo + 0.5); k <= last; k++)
            {
                double overlap = Math.Min(hi, k + 0.5) - Math.Max(lo, k - 0.5);
                if (overlap > 0) sum += overlap * _power[k];
            }
            bands[b] = (float)(sum * _tilt[b]);
        }
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2;
            double angle = -2 * Math.PI / len;
            double stepR = Math.Cos(angle), stepI = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double wr = 1, wi = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                    double next = wr * stepR - wi * stepI;
                    wi = wr * stepI + wi * stepR;
                    wr = next;
                }
            }
        }
    }
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

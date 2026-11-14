using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Core;

/// <summary>Captures system output through low-latency WASAPI loopback (25 ms buffer)
/// and produces AGC-adjusted spectrum targets. UI smoothing is frame-rate independent.</summary>
public class AudioVisualizerService : IDisposable
{
    private const int FftSize = 2048;      // 2^11
    private const int FftM = 11;
    public const int BarCount = 20;

    /// <summary>NAudio's default 100 ms buffer limits the spectrum to 10 FPS;
    /// this subclass uses the same loopback mode with a 25 ms buffer.</summary>
    private sealed class LowLatencyLoopback : WasapiCapture
    {
        public LowLatencyLoopback(MMDevice device) : base(device, false, 25) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            => AudioClientStreamFlags.Loopback;
    }

    private LowLatencyLoopback? _capture;
    private readonly float[] _ring = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private int _ringPos;
    private readonly object _lock = new();
    private int _sampleRate = 48000;
    private float _agcPeak = 1e-4f;

    public bool Running { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new LowLatencyLoopback(device);
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => Running = false;
            _capture.StartRecording();
            Running = true;
        }
        catch
        {
            _capture?.Dispose();
            _capture = null;
            Running = false;
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
        Running = false;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var channels = _capture?.WaveFormat.Channels ?? 2;
        var samples = e.BytesRecorded / 4; // 32-bit float
        lock (_lock)
        {
            for (int i = 0; i + channels <= samples; i += channels)
            {
                float mono = 0;
                for (int c = 0; c < channels; c++)
                    mono += BitConverter.ToSingle(e.Buffer, (i + c) * 4);
                _ring[_ringPos] = mono / channels;
                _ringPos = (_ringPos + 1) % FftSize;
            }
        }
    }

    /// <summary>Returns instantaneous, unsmoothed target values in the 0..1 range.</summary>
    public void GetTargets(float[] targets)
    {
        lock (_lock)
        {
            for (int i = 0; i < FftSize; i++)
            {
                var s = _ring[(_ringPos + i) % FftSize];
                _fft[i].X = (float)(s * FastFourierTransform.HammingWindow(i, FftSize));
                _fft[i].Y = 0;
            }
        }

        FastFourierTransform.FFT(true, FftM, _fft);

        // Logarithmic bands from 45 Hz to 14 kHz.
        const double fLo = 45, fHi = 14000;
        float frameMax = 0;
        for (int b = 0; b < BarCount; b++)
        {
            double f0 = fLo * Math.Pow(fHi / fLo, (double)b / BarCount);
            double f1 = fLo * Math.Pow(fHi / fLo, (double)(b + 1) / BarCount);
            int i0 = Math.Max(1, (int)(f0 * FftSize / _sampleRate));
            int i1 = Math.Max(i0 + 1, (int)(f1 * FftSize / _sampleRate));
            i1 = Math.Min(i1, FftSize / 2);

            double sum = 0;
            for (int i = i0; i < i1; i++)
                sum += Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);

            // High frequencies are naturally weaker, so apply a gentle slope.
            targets[b] = (float)(sum / (i1 - i0)) * (1f + b * 0.12f);
            if (targets[b] > frameMax) frameMax = targets[b];
        }

        // AGC: peak tracking produces a full spectrum regardless of volume.
        _agcPeak = Math.Max(frameMax, _agcPeak * 0.988f);
        if (_agcPeak < 1e-5f) _agcPeak = 1e-5f;

        for (int b = 0; b < BarCount; b++)
            targets[b] = (float)Math.Pow(Math.Clamp(targets[b] / _agcPeak, 0, 1), 0.6);
    }

    public void Dispose() => Stop();
}

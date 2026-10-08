using System.Diagnostics;
using LiveTranscriber.Core;
using LiveTranscriber.Core.Audio;
using NAudio.Wave;

namespace LiveTranscriber.Desktop.Audio;

/// <summary>Capture only render-device playback using WASAPI loopback; never opens a microphone.</summary>
public sealed class WasapiSpeakerSource : IAudioSource
{
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _capture;
    private PcmMonoResampler? _converter;
    private Timer? _silenceTimer;
    private long _lastPacket;
    private volatile bool _active;

    public event Action<float[]>? SamplesCaptured;
    public event Action<Exception>? Failed;

    public void Start()
    {
        lock (_gate)
        {
            if (_active) throw new InvalidOperationException("Capture already started.");
            var capture = new WasapiLoopbackCapture();
            try
            {
                WaveFormat format = capture.WaveFormat;
                bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                    format is WaveFormatExtensible extensible &&
                    extensible.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
                _converter = new PcmMonoResampler(
                    format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
                capture.DataAvailable += DataAvailable;
                capture.RecordingStopped += RecordingStopped;
                _capture = capture;
                _lastPacket = Stopwatch.GetTimestamp();
                _active = true;
                capture.StartRecording();
                // WASAPI does not always deliver callbacks during silence.
                _silenceTimer = new Timer(_ => SendSilenceWhenIdle(), null, 100, 100);
            }
            catch
            {
                _active = false;
                _capture = null;
                capture.Dispose();
                throw;
            }
        }
    }

    private void DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (!_active || _converter is null) return;
            float[] samples = _converter.Convert(e.Buffer, e.BytesRecorded);
            Interlocked.Exchange(ref _lastPacket, Stopwatch.GetTimestamp());
            if (samples.Length > 0) SamplesCaptured?.Invoke(samples);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void SendSilenceWhenIdle()
    {
        if (!_active) return;
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket)) < TimeSpan.FromMilliseconds(250))
            return;
        try { SamplesCaptured?.Invoke(new float[1_600]); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Failed?.Invoke(e.Exception);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _silenceTimer?.Dispose();
            _silenceTimer = null;
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;
            _converter = null;
        }
    }

    public void Dispose() => Stop();
}

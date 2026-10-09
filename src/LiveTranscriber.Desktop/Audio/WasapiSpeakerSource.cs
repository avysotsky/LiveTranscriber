using System.Diagnostics;
using System.IO;
using LiveTranscriber.Core;
using LiveTranscriber.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveTranscriber.Desktop.Audio;

/// <summary>Captures all rendered audio from the default output device; never opens a microphone.</summary>
public sealed class WasapiSpeakerSource : IAudioSource
{
    private readonly object _gate = new();
    private readonly WasapiRecorder _recorder;
    private readonly PcmMonoResampler _resampler;
    private Timer? _silenceTimer;
    private long _lastPacket;
    private volatile bool _active;
    private bool _disposed;

    public event Action<float[]>? SamplesCaptured;
    public event Action<Exception>? Failed;

    public WasapiSpeakerSource()
    {
        _recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
        var format = _recorder.WaveFormat;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible &&
            extensible.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
        _resampler = new PcmMonoResampler(format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
        _recorder.DataAvailable += DataAvailable;
        _recorder.RecordingStopped += RecordingStopped;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active) throw new InvalidOperationException("Capture already started.");
            _lastPacket = Stopwatch.GetTimestamp();
            _active = true;
            try
            {
                _recorder.StartRecording();
                _silenceTimer = new Timer(_ => SendSilenceWhenIdle(), null, 100, 100);
            }
            catch { _active = false; throw; }
        }
    }

    private void DataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags,
        long devicePosition, long qpcPosition)
    {
        if (!_active) return;
        try
        {
            // Buffer lifetime is only the native callback: make an owned copy before conversion.
            byte[] bytes = (flags & AudioClientBufferFlags.Silent) != 0
                ? new byte[data.Length] : data.ToArray();
            float[] samples = _resampler.Convert(bytes, bytes.Length);
            Interlocked.Exchange(ref _lastPacket, Stopwatch.GetTimestamp());
            if (samples.Length != 0) SamplesCaptured?.Invoke(samples);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void SendSilenceWhenIdle()
    {
        if (!_active || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket)) < TimeSpan.FromMilliseconds(250))
            return;
        try { SamplesCaptured?.Invoke(new float[1600]); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Failed?.Invoke(e.Exception);
        else if (_active) Failed?.Invoke(new IOException("Windows speaker loopback stopped unexpectedly."));
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            _silenceTimer?.Dispose();
            _silenceTimer = null;
            _recorder.StopRecording();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Stop(); }
        finally
        {
            _disposed = true;
            _recorder.DataAvailable -= DataAvailable;
            _recorder.RecordingStopped -= RecordingStopped;
            _recorder.Dispose();
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using LiveTranscriber.Core;
using LiveTranscriber.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveTranscriber.Desktop.Audio;

/// <summary>
/// Captures render streams of a selected PID and its child processes (never a microphone).
/// Requires Windows 10 2004 / build 19041 or newer and a working process-loopback backend.
/// </summary>
public sealed class ProcessLoopbackSource : IAudioSource
{
    private readonly WasapiRecorder _recorder;
    private readonly PcmMonoResampler _resampler;
    private readonly object _gate = new();
    private Timer? _silenceTimer;
    private long _lastPacket;
    private volatile bool _running;
    private bool _disposed;

    public event Action<float[]>? SamplesCaptured;
    public event Action<Exception>? Failed;

    private ProcessLoopbackSource(WasapiRecorder recorder)
    {
        _recorder = recorder;
        WaveFormat format = recorder.WaveFormat;
        _resampler = new PcmMonoResampler(format.SampleRate, format.Channels, format.BitsPerSample, true);
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnStopped;
    }

    public static async Task<ProcessLoopbackSource> CreateAsync(int processId, CancellationToken cancellationToken = default)
    {
        new CaptureSourceSelection(CaptureSourceMode.ProcessLoopback, processId).Validate();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException(
                "Application capture requires Windows 10 build 19041 or newer. " +
                "Select system playback explicitly if needed; no automatic fallback is performed.");

        using (var selected = Process.GetProcessById(processId))
        {
            if (selected.HasExited) throw new InvalidOperationException("Selected application has exited.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // NAudio activates WASAPI asynchronously. Do not block the WPF UI thread.
        var recorder = await BuildRecorderOnWorkerAsync(processId, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessLoopbackSource(recorder);
        }
        catch
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static Task<WasapiRecorder> BuildRecorderOnWorkerAsync(int processId, CancellationToken cancellationToken) =>
        Task.Run(async () => await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            .BuildAsync().ConfigureAwait(false), cancellationToken);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("Capture already started.");
            _lastPacket = Stopwatch.GetTimestamp();
            _running = true;
            try
            {
                _recorder.StartRecording();
                _silenceTimer = new Timer(_ => InsertSilenceIfIdle(), null, 100, 100);
            }
            catch
            {
                _running = false;
                throw;
            }
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags,
        long devicePosition, long qpcPosition)
    {
        if (!_running) return;
        try
        {
            // NAudio span belongs to the native capture callback; copying is mandatory here.
            byte[] bytes = (flags & AudioClientBufferFlags.Silent) != 0
                ? new byte[data.Length]
                : data.ToArray();
            float[] samples = _resampler.Convert(bytes, bytes.Length);
            Interlocked.Exchange(ref _lastPacket, Stopwatch.GetTimestamp());
            if (samples.Length != 0) SamplesCaptured?.Invoke(samples);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void InsertSilenceIfIdle()
    {
        if (!_running || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket)) < TimeSpan.FromMilliseconds(250))
            return;
        try { SamplesCaptured?.Invoke(new float[1600]); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Failed?.Invoke(args.Exception);
        else if (_running) Failed?.Invoke(new IOException("Selected application audio capture stopped."));
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
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
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.RecordingStopped -= OnStopped;
            _recorder.Dispose();
        }
    }
}

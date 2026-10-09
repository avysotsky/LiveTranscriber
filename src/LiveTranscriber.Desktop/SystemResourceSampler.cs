using System.Diagnostics;

namespace LiveTranscriber.Desktop;

/// <summary>Non-persistent rolling sample of THIS application's CPU share and working-set RAM.</summary>
internal sealed class SystemResourceSampler
{
    private readonly Process _current = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastStamp;
    private bool _initialized;

    public (double CpuPercent, double MemoryMiB) Sample()
    {
        _current.Refresh();
        TimeSpan cpu = _current.TotalProcessorTime;
        long stamp = Stopwatch.GetTimestamp();
        double memory = _current.WorkingSet64 / (1024d * 1024d);
        if (!_initialized)
        {
            _initialized = true;
            _lastCpu = cpu;
            _lastStamp = stamp;
            return (0, memory);
        }

        double elapsedSeconds = (stamp - _lastStamp) / (double)Stopwatch.Frequency;
        double cpuSeconds = (cpu - _lastCpu).TotalSeconds;
        _lastCpu = cpu;
        _lastStamp = stamp;
        double value = elapsedSeconds > 0
            ? Math.Clamp(cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100d, 0d, 100d)
            : 0d;
        return (value, memory);
    }
}

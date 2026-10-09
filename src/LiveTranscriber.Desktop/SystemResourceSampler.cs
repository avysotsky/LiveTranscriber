using System.Diagnostics;

namespace LiveTranscriber.Desktop;

/// <summary>
/// Rolling CPU and working-set measurements for LiveTranscriber PLUS its optional
/// offline Python translator subprocess. Cloud mode continues to measure this app.
/// </summary>
internal sealed class SystemResourceSampler
{
    private readonly Process _current = Process.GetCurrentProcess();
    private readonly Func<int?>? _extraProcessId;
    private TimeSpan _lastCpu;
    private TimeSpan _lastExtraCpu;
    private int? _lastExtraId;
    private long _lastStamp;
    private bool _initialized;

    public SystemResourceSampler(Func<int?>? extraProcessId = null)
    {
        _extraProcessId = extraProcessId;
    }

    public (double CpuPercent, double MemoryMiB) Sample()
    {
        _current.Refresh();
        TimeSpan cpu = _current.TotalProcessorTime;
        long stamp = Stopwatch.GetTimestamp();
        double memory = _current.WorkingSet64 / (1024d * 1024d);
        double extraCpuSeconds = 0;

        int? id = _extraProcessId?.Invoke();
        if (id is > 0)
        {
            try
            {
                using Process worker = Process.GetProcessById(id.Value);
                worker.Refresh();
                if (!worker.HasExited)
                {
                    memory += worker.WorkingSet64 / (1024d * 1024d);
                    TimeSpan workerCpu = worker.TotalProcessorTime;
                    if (_lastExtraId == id)
                        extraCpuSeconds = Math.Max(0, (workerCpu - _lastExtraCpu).TotalSeconds);
                    _lastExtraId = id;
                    _lastExtraCpu = workerCpu;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
            {
                _lastExtraId = null;
            }
        }
        else _lastExtraId = null;

        if (!_initialized)
        {
            _initialized = true;
            _lastCpu = cpu;
            _lastStamp = stamp;
            return (0, memory);
        }

        double elapsedSeconds = (stamp - _lastStamp) / (double)Stopwatch.Frequency;
        double cpuSeconds = Math.Max(0, (cpu - _lastCpu).TotalSeconds) + extraCpuSeconds;
        _lastCpu = cpu;
        _lastStamp = stamp;
        double value = elapsedSeconds > 0
            ? Math.Clamp(cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100d, 0d, 100d)
            : 0d;
        return (value, memory);
    }
}

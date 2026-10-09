namespace LiveTranscriber.Core;

/// <summary>Capture scope is explicit: a failed app capture never silently falls back to system output.</summary>
public enum CaptureSourceMode { DeviceLoopback, ProcessLoopback }

public sealed record CaptureSourceSelection(CaptureSourceMode Mode, int? ProcessId = null)
{
    public void Validate()
    {
        if (Mode == CaptureSourceMode.DeviceLoopback && ProcessId is null) return;
        if (Mode == CaptureSourceMode.ProcessLoopback && ProcessId is > 0) return;
        throw new ArgumentException(Mode switch
        {
            CaptureSourceMode.DeviceLoopback => "System output mode must not have a process ID.",
            CaptureSourceMode.ProcessLoopback => "Select an application with a valid positive process ID.",
            _ => "Unsupported capture source mode."
        });
    }
}

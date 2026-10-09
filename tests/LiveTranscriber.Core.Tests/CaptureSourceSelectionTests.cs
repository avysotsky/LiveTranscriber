using LiveTranscriber.Core;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public class CaptureSourceSelectionTests
{
    [Fact]
    public void DeviceCaptureWithoutPidIsValid() =>
        new CaptureSourceSelection(CaptureSourceMode.DeviceLoopback).Validate();

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ApplicationCaptureRequiresPositivePid(int? pid) =>
        Assert.Throws<ArgumentException>(() => new CaptureSourceSelection(CaptureSourceMode.ProcessLoopback, pid).Validate());

    [Fact]
    public void ApplicationCaptureWithPidIsValid() =>
        new CaptureSourceSelection(CaptureSourceMode.ProcessLoopback, 123).Validate();

    [Fact]
    public void DeviceCaptureCannotSilentlyUseAppPid() =>
        Assert.Throws<ArgumentException>(() => new CaptureSourceSelection(CaptureSourceMode.DeviceLoopback, 123).Validate());
}

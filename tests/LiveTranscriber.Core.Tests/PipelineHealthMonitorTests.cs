using LiveTranscriber.Core;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class PipelineHealthMonitorTests
{
    private static PipelineHealthReading Reading(
        double cpu = 12, double queue = 0, double rtf = 0.3, double loss = 0,
        bool cloud = false, double processed = 10) =>
        new(cpu, queue, rtf, loss, cloud, processed);

    [Fact]
    public void HealthyStateRequiresThreeCleanSamples()
    {
        var sut = new PipelineHealthMonitor();
        Assert.Equal(PipelineHealth.WarmingUp, sut.Observe(Reading()));
        Assert.Equal(PipelineHealth.WarmingUp, sut.Observe(Reading()));
        Assert.Equal(PipelineHealth.Healthy, sut.Observe(Reading()));
    }

    [Fact]
    public void CpuPeakAloneDoesNotImmediatelyTriggerPressure()
    {
        var sut = new PipelineHealthMonitor();
        Assert.Equal(PipelineHealth.WarmingUp, sut.Observe(Reading(cpu: 30)));
        Assert.Equal(PipelineHealth.WarmingUp, sut.Observe(Reading(cpu: 30)));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading(cpu: 30)));
    }

    [Fact]
    public void DroppedAudioAlertsImmediatelyAndRecoversAfterHealthySamples()
    {
        var sut = new PipelineHealthMonitor();
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading(loss: 0.1)));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading()));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading()));
        Assert.Equal(PipelineHealth.Healthy, sut.Observe(Reading()));
    }

    [Fact]
    public void CloudClientUploadTimeIsNotMistakenForLocalModelRtf()
    {
        var sut = new PipelineHealthMonitor();
        sut.Observe(Reading(cloud: true, rtf: 3));
        sut.Observe(Reading(cloud: true, rtf: 3));
        Assert.Equal(PipelineHealth.Healthy, sut.Observe(Reading(cloud: true, rtf: 3)));
    }

    [Fact]
    public void QueueBacklogCanTriggerPressure()
    {
        var sut = new PipelineHealthMonitor();
        sut.Observe(Reading(queue: 0.7));
        sut.Observe(Reading(queue: 0.7));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading(queue: 0.7)));
    }

    [Fact]
    public void InsufficientAudioStaysWarmingUp()
    {
        var sut = new PipelineHealthMonitor();
        for (int i = 0; i < 5; i++)
            Assert.Equal(PipelineHealth.WarmingUp,
                sut.Observe(Reading(cpu: 100, processed: 0.1)));
    }
}

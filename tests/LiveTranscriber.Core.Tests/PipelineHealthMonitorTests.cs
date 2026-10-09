using LiveTranscriber.Core;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class PipelineHealthMonitorTests
{
    private static PipelineHealthReading Reading(
        double cpu = 12, double queue = 0, double rtf = 0.3, double loss = 0,
        bool cloud = false, double processed = 10, int chunks = 0, long nearEvents = 0) =>
        new(cpu, queue, rtf, loss, cloud, processed, chunks, 12, nearEvents);

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
    public void ThreeSamplesAtNineOfTwelveQueueSlotsTriggerPressure()
    {
        var sut = new PipelineHealthMonitor();
        sut.Observe(Reading(queue: 0.09, chunks: 9));
        sut.Observe(Reading(queue: 0.09, chunks: 9));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading(queue: 0.09, chunks: 9)));
    }

    [Fact]
    public void CrossingHighWatermarkAlertsEvenAfterQueueIsDrained()
    {
        var sut = new PipelineHealthMonitor();
        Assert.Equal(PipelineHealth.UnderPressure,
            sut.Observe(Reading(queue: 0, chunks: 0, nearEvents: 1)));
        Assert.Equal(PipelineHealth.UnderPressure, sut.Observe(Reading()));
        sut.Observe(Reading());
        Assert.Equal(PipelineHealth.Healthy, sut.Observe(Reading()));
    }

    [Fact]
    public void UnderThresholdQueueDoesNotCreateFalsePressure()
    {
        var sut = new PipelineHealthMonitor();
        for (int i = 0; i < 3; ++i)
            sut.Observe(Reading(queue: 0.08, chunks: 8));
        Assert.Equal(PipelineHealth.Healthy, sut.State);
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

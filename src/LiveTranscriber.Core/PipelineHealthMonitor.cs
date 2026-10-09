namespace LiveTranscriber.Core;

public enum PipelineHealth { WarmingUp, Healthy, UnderPressure }

public sealed record PipelineHealthReading(
    double ApplicationCpuPercent,
    double QueuedAudioSeconds,
    double LocalProcessingRatio,
    double NewlyDroppedAudioSeconds,
    bool IsCloud,
    double ProcessedAudioSeconds,
    int QueuedChunks = 0,
    int QueueCapacityChunks = 24,
    long NewNearCapacityEvents = 0);

/// <summary>
/// In-memory health indicator with 3-sample hysteresis to avoid status flicker.
/// Never switches ASR engines or transmits audio automatically.
/// </summary>
public sealed class PipelineHealthMonitor
{
    private int _consecutiveBad;
    private int _consecutiveGood;
    public PipelineHealth State { get; private set; } = PipelineHealth.WarmingUp;

    public PipelineHealth Observe(PipelineHealthReading reading)
    {
        if (reading.ProcessedAudioSeconds < 0.3)
            return State;

        bool lostAudio = reading.NewlyDroppedAudioSeconds > 0;
        bool approachedCapacity = reading.NewNearCapacityEvents > 0;
        bool queueBusy = reading.QueueCapacityChunks > 0
            && reading.QueuedChunks >= Math.Ceiling(reading.QueueCapacityChunks * 0.75);
        bool pressure = lostAudio || approachedCapacity || queueBusy
            || reading.ApplicationCpuPercent >= 20
            || (!reading.IsCloud && reading.LocalProcessingRatio >= 0.9);

        if (pressure)
        {
            _consecutiveGood = 0;
            _consecutiveBad++;
            if (lostAudio || approachedCapacity || _consecutiveBad >= 3)
                State = PipelineHealth.UnderPressure;
        }
        else
        {
            _consecutiveBad = 0;
            _consecutiveGood++;
            if (_consecutiveGood >= 3)
                State = PipelineHealth.Healthy;
        }
        return State;
    }
}

namespace LiveTranscriber.Core;

/// <summary>
/// Thread-safe snapshot of one capture/ASR session. Durations are derived from
/// 16 kHz mono samples. None of these fields contains audio or transcript text.
/// </summary>
public sealed record PipelineMetrics(
    long DroppedChunks,
    double DroppedAudioSeconds,
    double QueuedAudioSeconds,
    double PeakQueuedAudioSeconds,
    double ProcessedAudioSeconds,
    double ProcessingRatio,
    long ProcessedChunks,
    int QueuedChunks,
    int PeakQueuedChunks,
    int QueueCapacityChunks,
    long NearCapacityEvents);

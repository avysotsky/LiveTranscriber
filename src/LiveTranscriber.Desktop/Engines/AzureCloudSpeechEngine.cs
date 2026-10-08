using LiveTranscriber.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace LiveTranscriber.Desktop.Engines;

/// <summary>Opt-in cloud ASR. Audio leaves the PC only after selecting Cloud and pressing Start.</summary>
public sealed class AzureCloudSpeechEngine : ISpeechEngine
{
    private PushAudioInputStream? _input;
    private AudioConfig? _audio;
    private SpeechRecognizer? _recognizer;

    public event Action<TranscriptUpdate>? TextAvailable;
    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        string? key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        string? region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("Set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION in the launching environment.");

        var config = SpeechConfig.FromSubscription(key, region);
        config.SpeechRecognitionLanguage = "en-US";
        _input = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        _audio = AudioConfig.FromStreamInput(_input);
        _recognizer = new SpeechRecognizer(config, _audio);
        _recognizer.Recognizing += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Result.Text))
                TextAvailable?.Invoke(new TranscriptUpdate(e.Result.Text, false));
        };
        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech &&
                !string.IsNullOrWhiteSpace(e.Result.Text))
                TextAvailable?.Invoke(new TranscriptUpdate(e.Result.Text, true));
        };
        _recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                Failed?.Invoke(new InvalidOperationException($"Azure Speech canceled: {e.ErrorCode}. {e.ErrorDetails}"));
        };
        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
    }

    public ValueTask ProcessAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_input is null) throw new InvalidOperationException("Cloud ASR is not started.");
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
            short pcm = (short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)pcm;
            bytes[i * 2 + 1] = (byte)(pcm >> 8);
        }
        _input.Write(bytes);
        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _input?.Close();
        if (_recognizer is not null)
            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        _recognizer?.Dispose();
        _audio?.Dispose();
        _input?.Dispose();
        _recognizer = null;
        _audio = null;
        _input = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

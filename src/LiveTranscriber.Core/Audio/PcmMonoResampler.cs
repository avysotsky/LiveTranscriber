namespace LiveTranscriber.Core.Audio;

/// <summary>
/// Converts interleaved PCM 16/24/32 or IEEE float32 to mono float32 at 16 kHz.
/// Maintains fractional sample position across capture buffer boundaries.
/// A lightweight linear interpolation is used to minimize CPU on a busy host.
/// </summary>
public sealed class PcmMonoResampler
{
    public const int OutputRate = 16_000;
    private readonly int _channels;
    private readonly int _bits;
    private readonly bool _float;
    private readonly int _blockAlign;
    private readonly double _step;
    private long _inputFrames;
    private double _next;
    private float _previous;
    private bool _hasPrevious;

    public PcmMonoResampler(int inputRate, int channels, int bitsPerSample, bool isFloat)
    {
        if (inputRate < 8_000 || inputRate > 384_000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (isFloat ? bitsPerSample != 32 : bitsPerSample is not (16 or 24 or 32))
            throw new NotSupportedException("Expected signed PCM16/24/32 or IEEE float32.");

        _channels = channels;
        _bits = bitsPerSample;
        _float = isFloat;
        _blockAlign = channels * (bitsPerSample / 8);
        _step = inputRate / (double)OutputRate;
    }

    public float[] Convert(byte[] bytes, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (count < 0 || count > bytes.Length || count % _blockAlign != 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Buffer must end at a sample-frame boundary.");
        int frames = count / _blockAlign;
        if (frames == 0) return [];

        var mono = new float[frames];
        int sampleBytes = _bits / 8;
        for (int frame = 0; frame < frames; ++frame)
        {
            double total = 0;
            for (int channel = 0; channel < _channels; ++channel)
            {
                int index = frame * _blockAlign + channel * sampleBytes;
                float value = _float ? BitConverter.ToSingle(bytes, index) : _bits switch
                {
                    16 => BitConverter.ToInt16(bytes, index) / 32768f,
                    24 => Read24(bytes, index) / 8388608f,
                    32 => BitConverter.ToInt32(bytes, index) / 2147483648f,
                    _ => throw new InvalidOperationException()
                };
                total += float.IsFinite(value) ? value : 0f;
            }
            mono[frame] = (float)(total / _channels);
        }

        long start = _inputFrames;
        long end = start + frames - 1;
        var output = new List<float>((int)Math.Ceiling(frames / _step) + 2);
        while (_next <= end)
        {
            long leftIndex = (long)Math.Floor(_next);
            long rightIndex = leftIndex + 1;
            float left = leftIndex >= start ? mono[(int)(leftIndex - start)]
                : _hasPrevious && leftIndex == start - 1 ? _previous
                : throw new InvalidOperationException("Invalid resampler state.");
            double fraction = _next - leftIndex;
            if (rightIndex > end && fraction > 1e-9) break;
            float right = rightIndex <= end ? mono[(int)(rightIndex - start)] : left;
            output.Add((float)(left + (right - left) * fraction));
            _next += _step;
        }
        _previous = mono[^1];
        _hasPrevious = true;
        _inputFrames += frames;
        return output.ToArray();
    }

    private static int Read24(byte[] bytes, int p)
    {
        int value = bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16;
        return (value << 8) >> 8;
    }
}

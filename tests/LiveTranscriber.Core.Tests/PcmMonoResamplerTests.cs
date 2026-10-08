using LiveTranscriber.Core.Audio;
using Xunit;

namespace LiveTranscriber.Core.Tests;

public sealed class PcmMonoResamplerTests
{
    [Fact]
    public void Pcm16Mono_PreservesSignAndCount()
    {
        var converter = new PcmMonoResampler(16000, 1, 16, false);
        byte[] bytes = [0, 0, 0xFF, 0x7F, 0, 0x80, 0, 0];
        float[] output = converter.Convert(bytes, bytes.Length);
        Assert.Equal(4, output.Length);
        Assert.InRange(output[1], 0.99f, 1f);
        Assert.Equal(-1f, output[2]);
    }

    [Fact]
    public void StereoFloat32_AveragesChannels()
    {
        var converter = new PcmMonoResampler(16000, 2, 32, true);
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(bytes, 4);
        BitConverter.GetBytes(1f).CopyTo(bytes, 8);
        BitConverter.GetBytes(0f).CopyTo(bytes, 12);
        float[] output = converter.Convert(bytes, bytes.Length);
        Assert.Equal(2, output.Length);
        Assert.Equal(0f, output[0], 4);
        Assert.Equal(0.5f, output[1], 4);
    }

    [Fact]
    public void SplitBuffers_ProduceIdenticalResampling()
    {
        var first = new PcmMonoResampler(48000, 2, 32, true);
        var split = new PcmMonoResampler(48000, 2, 32, true);
        var bytes = new byte[4800 * 8];
        for (int i = 0; i < 4800; i++)
        {
            BitConverter.GetBytes((float)Math.Sin(i * 0.01)).CopyTo(bytes, i * 8);
            BitConverter.GetBytes((float)Math.Sin(i * 0.01)).CopyTo(bytes, i * 8 + 4);
        }
        float[] whole = first.Convert(bytes, bytes.Length);
        byte[] left = bytes[..(1234 * 8)];
        byte[] right = bytes[(1234 * 8)..];
        float[] joined = [.. split.Convert(left, left.Length), .. split.Convert(right, right.Length)];
        Assert.Equal(1600, whole.Length);
        Assert.Equal(whole.Length, joined.Length);
        for (int i = 0; i < whole.Length; i++)
            Assert.InRange(Math.Abs(whole[i] - joined[i]), 0, 0.00001);
    }

    [Fact]
    public void InvalidFormatsAreRejected()
    {
        Assert.Throws<NotSupportedException>(() => new PcmMonoResampler(48000, 2, 8, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmMonoResampler(0, 2, 32, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmMonoResampler(48000, 2, 16, false).Convert(new byte[3], 3));
    }
}

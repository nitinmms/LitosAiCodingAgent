using System.Buffers.Binary;

namespace Litos.SoftwareFactory.Tray;

public enum AlertSound
{
    /// <summary>One struck bell. The default: distinct from the web app's beeps.</summary>
    Ding,

    /// <summary>Two bell notes, a little more noticeable.</summary>
    DingDong,

    /// <summary>The web app's "meal ready": three short beeps (domain/sound.ts).</summary>
    Beeps,

    None,
}

/// <summary>
/// The alert sounds, rendered as 16-bit mono PCM WAV in memory, so no sound file ships and every
/// shell plays the same thing (Windows: SoundPlayer; macOS: afplay; Linux: paplay). The notes
/// match the proposal page's Web Audio versions, and the beeps match the web app's.
/// </summary>
public static class Chime
{
    public const int SampleRate = 44_100;

    /// <summary>The sound as a complete WAV file, or null for <see cref="AlertSound.None"/>.</summary>
    public static byte[]? Render(AlertSound sound)
    {
        var samples = sound switch
        {
            AlertSound.Ding => Mix(1.6, s => Bell(s, 1318.5, 0.02)),
            AlertSound.DingDong => Mix(2.0, s =>
            {
                Bell(s, 1318.5, 0.02);
                Bell(s, 1046.5, 0.40);
            }),
            AlertSound.Beeps => Mix(1.0, s =>
            {
                foreach (var offset in new[] { 0.0, 0.32, 0.64 })
                    Beep(s, 1400, offset + 0.02, 0.2, 0.18);
            }),
            _ => null,
        };
        return samples is null ? null : Wav(samples);
    }

    private static float[] Mix(double seconds, Action<float[]> draw)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        draw(samples);

        // Never clip: two bells overlapping could pass full scale.
        var peak = samples.Max(Math.Abs);
        if (peak > 0.9f)
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= 0.9f / peak;
        }

        return samples;
    }

    /// <summary>A struck bell: the fundamental and two inharmonic partials, each decaying.</summary>
    private static void Bell(float[] samples, double frequency, double start)
    {
        const double volume = 0.30;
        Partial(samples, frequency, start, 1.5, volume);
        Partial(samples, frequency * 2.76, start, 0.6, volume * 0.28);
        Partial(samples, frequency * 5.4, start, 0.25, volume * 0.12);
    }

    /// <summary>A sine with an 8 ms rise, then an exponential fall to near silence at <paramref name="duration"/>.</summary>
    private static void Partial(float[] samples, double frequency, double start, double duration, double volume)
    {
        const double attack = 0.008, floor = 0.0008;
        var first = (int)(start * SampleRate);
        var count = Math.Min((int)(duration * SampleRate), samples.Length - first);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var level = t < attack ? volume * t / attack : volume * Math.Pow(floor / volume, (t - attack) / (duration - attack));
            samples[first + i] += (float)(level * Math.Sin(2 * Math.PI * frequency * t));
        }
    }

    /// <summary>A triangle-wave beep with a 10 ms rise and 40 ms fall, so it does not click.</summary>
    private static void Beep(float[] samples, double frequency, double start, double duration, double volume)
    {
        var first = (int)(start * SampleRate);
        var count = Math.Min((int)(duration * SampleRate), samples.Length - first);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var level = t < 0.01 ? volume * t / 0.01 : t > duration - 0.04 ? volume * Math.Max(0, (duration - t) / 0.04) : volume;
            var phase = t * frequency % 1.0;
            var triangle = 4 * Math.Abs(phase - 0.5) - 1;
            samples[first + i] += (float)(level * triangle);
        }
    }

    private static byte[] Wav(float[] samples)
    {
        const int headerSize = 44, bytesPerSample = 2;
        var dataSize = samples.Length * bytesPerSample;
        var wav = new byte[headerSize + dataSize];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], headerSize - 8 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(headerSize + i * bytesPerSample)..], value);
        }

        return wav;
    }
}

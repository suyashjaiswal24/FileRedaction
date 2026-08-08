using NAudio.Wave;
using FileRedaction.Models;

namespace FileRedaction.Services;

public interface IAudioRedactionService
{
    string CreateRedactedAudio(string decodedWavPath, List<AudioTimeRange> rangesToBeep);
}

public class AudioRedactionService : IAudioRedactionService
{
    private const double BeepHz = 1000.0;
    private const double BeepAmplitude = 0.7;
    private const double PaddingSecs = 0.05;   // 50 ms padding around each range
    private const double FadeSecs = 0.01;      // 10 ms fade-in/out

    private readonly ILogger<AudioRedactionService> _logger;

    public AudioRedactionService(ILogger<AudioRedactionService> logger) => _logger = logger;

    public string CreateRedactedAudio(string decodedWavPath, List<AudioTimeRange> rangesToBeep)
    {
        _logger.LogInformation("Creating redacted audio — {Count} range(s) to beep", rangesToBeep.Count);

        using var reader = new WaveFileReader(decodedWavPath);
        var format = reader.WaveFormat; // 16 kHz 16-bit mono (from transcription step)
        int sampleRate = format.SampleRate;

        // Read all PCM bytes → short samples
        var ms = new MemoryStream();
        var buf = new byte[4096];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, read);

        var bytes = ms.ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        // Merge overlapping/adjacent ranges before beeping
        var merged = MergeRanges(rangesToBeep);

        int fadeSamples = (int)(FadeSecs * sampleRate);
        int paddingSamples = (int)(PaddingSecs * sampleRate);

        foreach (var range in merged)
        {
            int start = Math.Max(0, TicksToSamples(range.StartTicks, sampleRate) - paddingSamples);
            int end   = Math.Min(samples.Length, TicksToSamples(range.EndTicks, sampleRate) + paddingSamples);

            _logger.LogInformation("  Beeping samples [{Start}–{End}] ({Secs:F2}s)", start, end, (end - start) / (double)sampleRate);

            int len = end - start;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / sampleRate;
                double fade = ComputeFade(i, len, fadeSamples);
                samples[start + i] = (short)(short.MaxValue * BeepAmplitude * Math.Sin(2 * Math.PI * BeepHz * t) * fade);
            }
        }

        var outPath = Path.Combine(Path.GetTempPath(), $"redacted_audio_{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(outPath, format);
        var outBytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, outBytes, 0, outBytes.Length);
        writer.Write(outBytes, 0, outBytes.Length);

        _logger.LogInformation("Redacted audio written: {Path}", outPath);
        return outPath;
    }

    private static int TicksToSamples(long ticks, int sampleRate) =>
        (int)(ticks / 10_000_000.0 * sampleRate);

    private static double ComputeFade(int i, int totalLen, int fadeSamples)
    {
        if (i < fadeSamples) return (double)i / fadeSamples;
        if (i > totalLen - fadeSamples) return (double)(totalLen - i) / fadeSamples;
        return 1.0;
    }

    private static List<AudioTimeRange> MergeRanges(List<AudioTimeRange> ranges)
    {
        if (ranges.Count == 0) return ranges;
        var sorted = ranges.OrderBy(r => r.StartTicks).ToList();
        var merged = new List<AudioTimeRange> { sorted[0] };
        foreach (var r in sorted.Skip(1))
        {
            var last = merged[^1];
            if (r.StartTicks <= last.EndTicks)
                merged[^1] = new AudioTimeRange { StartTicks = last.StartTicks, EndTicks = Math.Max(last.EndTicks, r.EndTicks) };
            else
                merged.Add(r);
        }
        return merged;
    }
}

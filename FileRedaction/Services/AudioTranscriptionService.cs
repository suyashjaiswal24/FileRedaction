using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using FileRedaction.Models;

namespace FileRedaction.Services;

// INTEGRATION NOTE ─────────────────────────────────────────────────────────────
// POC calls Azure Speech SDK directly from this service.
//
// Production integration (WhistleB IVR pattern):
//   1. Upload the audio file to Azure Blob Storage → obtain a FileRef (GUID).
//   2. Replace this service entirely with a Service Bus publisher:
//        serviceBusSender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
//            new { Type = "TranscriptionVoice", Payload = new TranscriptionVoicePayload {
//                OrgId = ..., CaseId = ..., MessageId = ..., FileId = ...,
//                FileRef = <blobGuid>, FileName = file.FileName,
//                TrackingId = Guid.NewGuid(), TranscriptionId = Guid.NewGuid(),
//                Key = ..., CryptoDetails = ..., SourceLanguage = ...
//            }})));
//        Topic: VoiceTranscriptionTopic  Subscription: Transcription
//   3. The existing TranscriptionSpeechVoice Azure Function downloads + decrypts the blob,
//      calls Speech SDK — but it MUST be extended to include word-level timestamps
//      (see INTEGRATION NOTE in AudioRedactionController.ProcessAsync for the required
//      changes to UpdateVoiceTranscriptionPayload and the Azure Function output).
//   4. The result arrives via POST /api/audio/transcription-result (see AudioRedactionController).
// ───────────────────────────────────────────────────────────────────────────────
public interface IAudioTranscriptionService
{
    /// <summary>Returns the decoded WAV path (caller owns temp file), transcript words, detected language.</summary>
    Task<(string decodedWavPath, List<AudioTranscriptWord> words, string detectedLanguage, string fullTranscript)>
        TranscribeAsync(string filePath, string language = "en-US");
}

public class AudioTranscriptionService : IAudioTranscriptionService
{
    private readonly string _apiKey;
    private readonly string _region;
    private readonly ILogger<AudioTranscriptionService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public AudioTranscriptionService(IConfiguration config, ILogger<AudioTranscriptionService> logger)
    {
        // INTEGRATION NOTE: In production these config keys are replaced by:
        //   Azure:ServiceBus:ConnectionString + Azure:ServiceBus:VoiceTranscriptionTopic
        // The Speech SDK key/region move into the TranscriptionSpeechVoice Azure Function.
        _apiKey = config["Azure:SpeechService:Key"]
            ?? throw new InvalidOperationException("Azure:SpeechService:Key is not configured.");
        _region = config["Azure:SpeechService:Region"]
            ?? throw new InvalidOperationException("Azure:SpeechService:Region is not configured.");
        _logger = logger;
    }

    public async Task<(string, List<AudioTranscriptWord>, string, string)> TranscribeAsync(string filePath, string language = "en-US")
    {
        _logger.LogInformation("═══ AUDIO TRANSCRIPTION ═══  File: {File}  Language: {Lang}", Path.GetFileName(filePath), language);

        var decodedWav = DecodeToPcmWav(filePath);
        _logger.LogInformation("Decoded to 16 kHz WAV: {Path}", decodedWav);

        var speechConfig = SpeechConfig.FromSubscription(_apiKey, _region);
        speechConfig.SpeechRecognitionLanguage = language;   // user-selected locale, e.g. "en-US", "de-DE"
        speechConfig.RequestWordLevelTimestamps();
        speechConfig.OutputFormat = OutputFormat.Detailed;

        using var audioConfig = AudioConfig.FromWavFileInput(decodedWav);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var rawWords = new List<RawSpeechWord>();

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech) return;

            var json = e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
            _logger.LogDebug("Speech segment JSON: {Json}", json);

            var detail = JsonSerializer.Deserialize<SpeechDetailResult>(json, JsonOpts);
            if (detail?.NBest is { Length: > 0 } nBest && nBest[0].Words is { } words)
                rawWords.AddRange(words);
        };

        recognizer.Canceled += (_, e) =>
            _logger.LogWarning("Speech recognition canceled: {Reason} {Details}", e.Reason, e.ErrorDetails);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recognizer.SessionStopped += (_, _) => tcs.TrySetResult(true);
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                tcs.TrySetException(new InvalidOperationException($"Speech error: {e.ErrorCode} — {e.ErrorDetails}"));
            else
                tcs.TrySetResult(false);
        };

        await recognizer.StartContinuousRecognitionAsync();
        await tcs.Task;
        await recognizer.StopContinuousRecognitionAsync();

        // Build fullTranscript and assign character offsets
        var sb = new StringBuilder();
        var transcriptWords = new List<AudioTranscriptWord>();
        foreach (var rw in rawWords)
        {
            var tw = new AudioTranscriptWord
            {
                Word = rw.Word,
                TextOffset = sb.Length,
                TextLength = rw.Word.Length,
                AudioOffsetTicks = rw.Offset,
                AudioDurationTicks = rw.Duration
            };
            transcriptWords.Add(tw);
            sb.Append(rw.Word);
            sb.Append(' ');
        }
        var fullTranscript = sb.ToString().TrimEnd();

        // Strip region code for the PII service: "en-US" → "en"
        var piiLang = language.Contains('-') ? language.Split('-')[0] : language;

        _logger.LogInformation("Transcription complete: {Words} words, lang={Lang}, chars={Chars}",
            transcriptWords.Count, piiLang, fullTranscript.Length);

        return (decodedWav, transcriptWords, piiLang, fullTranscript);
    }

    /// <summary>Decodes any supported audio format to a 16 kHz 16-bit mono WAV temp file.</summary>
    private static string DecodeToPcmWav(string filePath)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"audio_pcm_{Guid.NewGuid():N}.wav");
        var targetFormat = new WaveFormat(16000, 16, 1);

        using var reader = new NAudio.Wave.MediaFoundationReader(filePath);
        using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(outPath, resampler);
        return outPath;
    }

    // ── Speech SDK JSON contract ─────────────────────────────────────────────

    private record SpeechDetailResult(
        [property: JsonPropertyName("NBest")] SpeechNBest[]? NBest);

    private record SpeechNBest(
        [property: JsonPropertyName("Display")] string Display = "",
        [property: JsonPropertyName("Words")] RawSpeechWord[]? Words = null);

    private record RawSpeechWord(
        [property: JsonPropertyName("Word")] string Word = "",
        [property: JsonPropertyName("Offset")] long Offset = 0,
        [property: JsonPropertyName("Duration")] long Duration = 0);
}

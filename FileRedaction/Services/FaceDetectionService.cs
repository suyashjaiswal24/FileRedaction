using Azure;
using Azure.AI.Vision.Face;

namespace FileRedaction.Services;

public interface IFaceDetectionService
{
    bool IsConfigured { get; }
    Task<List<FaceDetectionResult>> DetectFacesAsync(Stream imageStream);
}

public record FaceDetectionResult(int Left, int Top, int Width, int Height);

public class FaceDetectionService : IFaceDetectionService
{
    private readonly FaceClient? _client;
    private readonly ILogger<FaceDetectionService> _logger;

    public bool IsConfigured => _client is not null;

    public FaceDetectionService(IConfiguration config, ILogger<FaceDetectionService> logger)
    {
        _logger = logger;

        var endpoint = config["Azure:FaceService:Endpoint"];
        var apiKey   = config["Azure:FaceService:ApiKey"];

        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey))
            _client = new FaceClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        else
            _logger.LogDebug("Azure Face Service not configured — face detection disabled");
    }

    public async Task<List<FaceDetectionResult>> DetectFacesAsync(Stream imageStream)
    {
        if (_client is null) return [];

        try
        {
            // detection_03: best for unconstrained real-world photos (masks, angles, distance)
            // recognition_04: latest model, required even when not doing identification
            // returnFaceId = false: avoids Responsible AI gating that only covers identification
            var response = await _client.DetectAsync(
                BinaryData.FromStream(imageStream),
                FaceDetectionModel.Detection03,
                FaceRecognitionModel.Recognition04,
                returnFaceId: false);

            _logger.LogInformation("Face SDK detected {Count} face(s)", response.Value.Count);

            return response.Value
                .Select(f => new FaceDetectionResult(
                    f.FaceRectangle.Left,
                    f.FaceRectangle.Top,
                    f.FaceRectangle.Width,
                    f.FaceRectangle.Height))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Face SDK detection failed");
            return [];
        }
    }
}

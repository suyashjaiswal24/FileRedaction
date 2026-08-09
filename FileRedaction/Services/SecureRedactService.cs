using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileRedaction.Services;

public interface ISecureRedactService
{
    Task<string> UploadMediaAsync(string filePath, string fileName);
    Task<string> GetMediaStatusAsync(string mediaId);
    Task<string> RedactMediaAsync(string mediaId);
    Task<string> PublishMediaAsync(string mediaId, string versionId);
}

public class SecureRedactService : ISecureRedactService
{
    private const string BaseUrl = "https://app.secureredact.co.uk/api/v3";

    // NOTE: The following multipart field names (blur_faces, blur_license_plates, redact_text,
    // redact_audio) and JSON response field names (media_id, version_id, status, url) should be
    // verified against the official SecureRedact v3 API documentation before production use.

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SecureRedactService> _logger;

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SecureRedactService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<SecureRedactService> logger)
    {
        _clientId = config["SecureRedact:ClientId"]
            ?? throw new InvalidOperationException("SecureRedact:ClientId is not configured.");
        _clientSecret = config["SecureRedact:ClientSecret"]
            ?? throw new InvalidOperationException("SecureRedact:ClientSecret is not configured.");
        _httpClient = httpClientFactory.CreateClient(nameof(SecureRedactService));
        _logger = logger;
    }

    public async Task<string> UploadMediaAsync(string filePath, string fileName)
    {
        var token = await GetTokenAsync();
        _logger.LogInformation("Uploading video '{File}' to SecureRedact", fileName);

        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
        form.Add(fileContent, "file", fileName);

        // Request all available redaction types
        // NOTE: verify exact param names against SecureRedact docs — these are the common ones
        form.Add(new StringContent("true"), "blur_faces");
        form.Add(new StringContent("true"), "blur_license_plates");
        form.Add(new StringContent("true"), "redact_text");
        form.Add(new StringContent("true"), "redact_audio");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = form;

        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Upload response {Status}: {Body}", resp.StatusCode, body);
        resp.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<MediaUploadResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty upload response from SecureRedact.");
        return result.MediaId;
    }

    public async Task<string> GetMediaStatusAsync(string mediaId)
    {
        var token = await GetTokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/media/{mediaId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _httpClient.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonSerializer.Deserialize<MediaStatusResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty status response from SecureRedact.");
        return result.Status;
    }

    public async Task<string> RedactMediaAsync(string mediaId)
    {
        var token = await GetTokenAsync();
        _logger.LogInformation("Triggering redaction for media {Id}", mediaId);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/media/{mediaId}/redact");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Redact response {Status}: {Body}", resp.StatusCode, body);
        resp.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<MediaRedactResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty redact response from SecureRedact.");
        return result.VersionId;
    }

    public async Task<string> PublishMediaAsync(string mediaId, string versionId)
    {
        var token = await GetTokenAsync();
        _logger.LogInformation("Publishing media {Id} version {Ver}", mediaId, versionId);

        using var form = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("version_id", versionId) });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/media/{mediaId}/publish");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = form;

        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogInformation("Publish response {Status}: {Body}", resp.StatusCode, body);
        resp.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<MediaPublishResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty publish response from SecureRedact.");
        return result.Url;
    }

    // ── Token management ─────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync()
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            // Standard OAuth2 client_credentials grant — some SecureRedact endpoints expect this
            req.Content = new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });

            var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("SecureRedact token response {Status}: {Body}", (int)resp.StatusCode, body);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"SecureRedact auth failed ({(int)resp.StatusCode}): {body}");
            resp.EnsureSuccessStatusCode();

            var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOpts)
                ?? throw new InvalidOperationException("Empty token response from SecureRedact.");

            _cachedToken = token.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 60);
            _logger.LogInformation("SecureRedact token fetched, expires in {Sec}s", token.ExpiresIn);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string GetMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4"  => "video/mp4",
            ".mov"  => "video/quicktime",
            ".avi"  => "video/x-msvideo",
            ".mkv"  => "video/x-matroska",
            ".webm" => "video/webm",
            ".wmv"  => "video/x-ms-wmv",
            _       => "application/octet-stream"
        };

    // ── JSON contracts ────────────────────────────────────────────────────────

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken = "",
        [property: JsonPropertyName("expires_in")]   int ExpiresIn = 3600);

    private record MediaUploadResponse(
        [property: JsonPropertyName("media_id")] string MediaId = "");

    private record MediaStatusResponse(
        [property: JsonPropertyName("status")] string Status = "");

    private record MediaRedactResponse(
        [property: JsonPropertyName("version_id")] string VersionId = "");

    private record MediaPublishResponse(
        [property: JsonPropertyName("url")] string Url = "");
}

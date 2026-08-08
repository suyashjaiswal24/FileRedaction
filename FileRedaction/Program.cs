using FileRedaction.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddScoped<IDocumentIntelligenceService, DocumentIntelligenceService>();
builder.Services.AddScoped<IPiiDetectionService, PiiDetectionService>();
builder.Services.AddScoped<IRedactionService, RedactionService>();
builder.Services.AddSingleton<IOfficeConversionService, OfficeConversionService>();

builder.Services.AddSingleton<AudioSessionStore>();
builder.Services.AddScoped<IAudioTranscriptionService, AudioTranscriptionService>();
builder.Services.AddScoped<IAudioRedactionService, AudioRedactionService>();

// Named HttpClient for the Azure AI Language Service (PII detection)
builder.Services.AddHttpClient(nameof(PiiDetectionService), (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var apiKey = cfg["AzureLanguageService:ApiKey"]
        ?? throw new InvalidOperationException("AzureLanguageService:ApiKey is not configured.");
    var timeoutSecs = cfg.GetValue("AzureLanguageService:TimeoutSeconds", 5);

    // No BaseAddress — PiiDetectionService builds the full absolute URL itself to
    // avoid .NET Uri combining encoding the ':' in ':analyze-text'.
    client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
    client.Timeout = TimeSpan.FromSeconds(timeoutSecs);
});

// Allow Vite dev server to call the API during development
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173")
         .AllowAnyHeader()
         .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();
app.MapControllers();

// Serve the React SPA for any unmatched route (production build in wwwroot)
app.MapFallbackToFile("index.html");

app.Run();

# FileRedaction — POC

A web app that automatically finds and hides personally identifiable information (PII) from documents, audio files, and video files. Built as a proof-of-concept to explore Azure AI services for redaction.

---

## What It Does

Upload a file → the app reads it, finds sensitive info like names, phone numbers, email addresses, bank accounts, etc. → you review and choose what to hide → download the clean version with PII permanently removed.

**Three modes:**

| Mode | Supported Formats | What Gets Redacted |
|------|------------------|-------------------|
| **Document** | PDF, TXT, Word (.docx/.doc), Excel (.xlsx/.xls), PowerPoint (.pptx), Images (PNG, JPG, TIFF, BMP, GIF, WebP) | Text blacked out or replaced with `█`, faces blurred/boxed |
| **Audio** | WAV, MP3, M4A, AAC, OGG, FLAC, WMA | PII speech segments replaced with a 1 kHz beep tone |
| **Video** | MP4, MOV, AVI, MKV, WebM, WMV | Faces, licence plates, on-screen text, spoken audio — all via SecureRedact |

---

## How Each Mode Works (Step by Step)

### Document Mode

#### Step 1 — Upload
- User drops or selects a file (max 20 MB).
- Server immediately saves it to a temp folder and returns a `sessionId`.
- Background processing starts right away (the UI does not wait).

#### Step 2 — Background Processing (the heavy lifting)
The server runs three things in order while the browser polls every 2 seconds:

**Phase 1 — `extracting` — Text extraction**

- **Plain text (`.txt`):** File is read directly. No AI needed. Every word gets its exact character offset stored.
- **Images (PNG, JPG, etc.):** Sent to **Azure Document Intelligence** (`prebuilt-read` model) which uses OCR to find every word and its pixel position on the image.
  - If the image is larger than 4 MB, it is compressed (resized to ~90% of size) before sending to DI, then the pixel coordinates are scaled back to the original image size so highlights land in the right place.
- **PDFs:** Sent to **Azure Document Intelligence** which extracts text with exact bounding box coordinates in points (1 point = 1/72 inch).
- **Office files (Word, Excel, PowerPoint):** Also sent to Azure Document Intelligence. Additionally, they are lazily converted to PDF using **Aspose** the first time a preview is needed.

**Phase 2 — `detecting` — PII detection**

- The full extracted text is sent to **Azure AI Language Service** (PII detection endpoint).
- The Language Service returns a list of entities: each entity has a category (Person, PhoneNumber, Email, CreditCardNumber, IBAN, etc.), a confidence score, and the exact character offset + length in the text.
- These offsets are used later to highlight/redact the right positions in the document.
- If an entity appears multiple times, all occurrences are tracked (deduplication keeps the best confidence score and merges all character ranges).

**Phase 3 — `detecting_faces` — Face detection (optional)**

- Runs only if **Azure Face API** credentials are configured.
- For image files: the image is sent directly to the Face API (up to 6 MB limit).
- For PDFs: each page is rendered to a PNG at 150 DPI using **Aspose.PDF**, then each PNG is sent to the Face API. Pixel coordinates returned by the Face API are converted to PDF point coordinates.
- Each detected face becomes a "Face #1", "Face #2" etc. entity that appears in the entity list just like any PII entity.
- Face detection failures are non-fatal — the app continues without faces if the API is unavailable or fails.

#### Step 3 — Review Entities
- The browser receives the list of all detected entities.
- Entities are grouped by category with colour coding (e.g. Person = blue, PhoneNumber = green, Face = pink).
- All entities are pre-selected. You can deselect any you want to keep.
- You can also **manually add words** — type any word that wasn't auto-detected and it will be added to the redaction list.

#### Step 4 — Preview
- Click "Preview Redaction" to see a highlighted version before committing.
- **PDFs / Images:** A copy of the file is generated with yellow highlights over every selected entity. Faces get a yellow outlined box.
- **Excel:** A copy of the spreadsheet is generated with yellow cell highlights, then exported to HTML and shown inline in the browser.
- **TXT:** A self-contained HTML page is generated with `<mark>` highlights at exact character positions, shown inline in the browser.
- **Incremental preview:** If you deselect/reselect entities after previewing, only the newly added highlights are drawn on the existing preview (faster than regenerating from scratch).

#### Step 5 — Redact & Download
- Click "Confirm & Redact" — the actual redaction runs:

| File type | What happens |
|-----------|-------------|
| **PDF** | `RedactionAnnotation` (black filled rectangles) are applied over each entity's bounding box. For manually added words, `TextFragmentAbsorber` finds all occurrences in the PDF text layer. For faces, black boxes are placed using the PDF point coordinates. |
| **Image** (PNG, JPG, etc.) | Black rectangles are drawn directly onto the image pixels at the bounding box positions returned by Document Intelligence. |
| **TXT** | Each entity's precise character range is replaced with `█` characters, back-to-front order to avoid position shifting. Downloaded as a `.txt` file. |
| **Excel** | Cell values containing PII text are replaced with `█████` in the spreadsheet. Downloaded as `.xlsx`. |
| **Word / PowerPoint** | Converted to PDF first (Aspose), then redacted the same as PDF. |

- The redacted file is automatically downloaded by the browser.

---

### Audio Mode

#### Step 1 — Upload + Transcribe
- User uploads an audio file (any common format).
- NAudio's `MediaFoundationReader` decodes the audio to a standardised 16 kHz, 16-bit mono WAV.
- The WAV is sent to **Azure Cognitive Services Speech SDK** with word-level timestamps enabled.
- Every word in the transcript gets its exact start/end time (in 100-nanosecond ticks).

#### Step 2 — PII Detection
- The full transcript text is sent to **Azure AI Language Service** — same PII service as documents.
- Each PII entity is matched back to its word(s) in the timestamped transcript to get the audio time range.

#### Step 3 — Review & Beep
- The browser shows the transcript with PII entities highlighted.
- You choose which entities to beep out (or search/add words manually).
- Click "Redact Audio" — the server generates a new WAV where every selected time range is replaced with a 1 kHz sine wave beep.
  - A 50 ms padding is added around each range so the beep doesn't cut off suddenly.
  - A 10 ms fade-in/fade-out is applied to avoid audio clicks.
  - Overlapping/adjacent beep ranges are merged before processing.
- The redacted WAV is downloaded.

---

### Video Mode

Video redaction is fully handled by the **SecureRedact v3 API** (a third-party service). The app acts as a thin wrapper:

1. File is uploaded to SecureRedact.
2. SecureRedact automatically detects faces, licence plates, on-screen text, and spoken audio — no config needed.
3. The app polls SecureRedact's status endpoint every few seconds.
4. Once detection is complete, the app triggers redaction (`POST /media/{id}/redact`).
5. When redaction finishes, the app publishes the result (`POST /media/{id}/publish`).
6. A secure download URL is returned and the browser downloads the redacted video.

Authentication is OAuth2 client credentials — the token is cached and reused until expiry.

---

## Azure Services Used

| Service | What It Does in This App | SDK / API |
|---------|--------------------------|-----------|
| **Azure Document Intelligence** | Reads text + word positions from PDFs, images, Office files | `Azure.AI.FormRecognizer` SDK (v4) |
| **Azure AI Language Service** | Detects PII entities (names, phones, emails, bank details, etc.) | REST API (`analyze-text` endpoint) |
| **Azure Cognitive Services Speech** | Transcribes audio with word-level timestamps | `Microsoft.CognitiveServices.Speech` SDK |
| **Azure AI Vision Face** | Detects human faces in images and PDFs | `Azure.AI.Vision.Face` SDK (v1.0.0-beta.2) |

---

## Third-Party Libraries Used

| Library | Purpose |
|---------|---------|
| **Aspose.PDF** | PDF highlight preview, permanent redaction (black boxes), Office→PDF conversion, rendering PDF pages to PNG for face detection |
| **Aspose.Words** | Word/RTF/ODT → PDF conversion |
| **Aspose.Cells** | Excel highlight preview, Excel redaction, Excel → HTML export for inline preview |
| **Aspose.Slides** | PowerPoint → PDF conversion |
| **Aspose.Drawing** | Image compression (resize large images before sending to Azure DI), bitmap manipulation (indexed pixel format handling for barcodes) |
| **NAudio** | Decode any audio format to PCM WAV, write redacted WAV with beep tones |
| **React + TypeScript** | Frontend UI (Vite build tool) |
| **Axios** | HTTP calls from frontend to backend API |

---

## Configuration

All secrets go in `appsettings.Development.json`. **This file is gitignored — never commit it.**

```json
{
  "Azure": {
    "DocumentIntelligence": {
      "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
      "ApiKey": "<your DI API key>"
    },
    "SpeechService": {
      "Key": "<your Speech API key>",
      "Region": "<e.g. eastus>"
    },
    "FaceService": {
      "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
      "ApiKey": "<your Face API key>"
    }
  },
  "AzureLanguageService": {
    "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
    "ApiKey": "<your Language API key>",
    "TimeoutSeconds": 5
  },
  "SecureRedact": {
    "ClientId": "<your SecureRedact client ID>",
    "ClientSecret": "<your SecureRedact client secret>"
  }
}
```

**Face detection is optional.** If `Azure:FaceService:Endpoint` or `Azure:FaceService:ApiKey` are missing or empty, face detection is silently skipped. Everything else still works.

---

## Running Locally

**Prerequisites:** .NET 8 SDK, Node 18+

```bash
# Clone the repo, then from the FileRedaction/ project folder:
dotnet run --project FileRedaction
```

The backend starts on `http://localhost:5000` (or as configured). During development, the React frontend runs on `http://localhost:5173` (Vite dev server) with CORS allowed. For production, `npm run build` outputs static files to `wwwroot` and the .NET app serves them directly.

---

## API Endpoints

All endpoints are under `/api/redaction/` (documents), `/api/audio/` (audio), and `/api/video/` (video).

### Document Endpoints

| Method | URL | Description |
|--------|-----|-------------|
| `POST` | `/api/redaction/upload` | Upload a document. Returns `sessionId` immediately; processing runs in background. |
| `GET` | `/api/redaction/{sessionId}/status` | Poll for processing status. Returns `{ status, phase, entities }` when ready. |
| `GET` | `/api/redaction/{sessionId}/search-words` | Search the extracted word list (for manual entity addition). |
| `POST` | `/api/redaction/{sessionId}/add-entity` | Manually add a word/phrase to the entity list. |
| `POST` | `/api/redaction/preview` | Generate a highlighted preview. Returns a URL to the preview file. |
| `POST` | `/api/redaction/redact` | Apply permanent redaction. Streams the redacted file for download. |
| `GET` | `/api/redaction/file/{token}` | Serves a temp file by base64-encoded path token (restricted to system temp dir). |
| `GET` | `/api/redaction/preview-html/{dirToken}/{*path}` | Serves Aspose-generated Excel HTML previews (multi-file with CSS/sheet companions). |

### Processing Phases (returned in `/status`)

| Phase value | Shown in UI as |
|-------------|---------------|
| `extracting` | "Extracting text with Azure Document Intelligence…" (or "Analyzing image…" for images, "Reading plain text file…" for TXT) |
| `detecting` | "Detecting PII entities…" |
| `detecting_faces` | "Detecting faces with Azure Face API…" |
| *(empty / ready)* | Processing complete |

---

## Project Structure

```
FileRedaction/
├── Controllers/
│   ├── RedactionController.cs         # Document upload → status → preview → redact
│   ├── AudioRedactionController.cs    # Audio upload → transcribe → PII → beep → download
│   └── VideoRedactionController.cs    # Video upload → SecureRedact polling → download
│
├── Services/
│   ├── DocumentIntelligenceService.cs # Azure DI wrapper. Handles image compression + coord scaling.
│   ├── PiiDetectionService.cs         # Azure Language Service wrapper. Tracks char offsets + deduplication.
│   ├── RedactionService.cs            # PDF/image highlight preview + permanent black-box redaction (Aspose.PDF).
│   │                                  # Also handles indexed pixel format images (barcodes) via ToArgb32 helper.
│   ├── OfficeConversionService.cs     # Office→PDF conversion, Excel highlight/redact, Excel→HTML export.
│   ├── FaceDetectionService.cs        # Azure Face API wrapper. Returns pixel bounding boxes per face.
│   ├── AudioTranscriptionService.cs   # Azure Speech SDK: decodes audio → transcribes → returns word timestamps.
│   ├── AudioRedactionService.cs       # Reads PCM WAV, replaces time ranges with 1 kHz beep, writes new WAV.
│   ├── SecureRedactService.cs         # SecureRedact v3 API client: auth + upload + status + redact + publish.
│   ├── SessionStore.cs                # In-memory store for document processing sessions (thread-safe).
│   ├── AudioSessionStore.cs           # In-memory store for audio processing sessions.
│   └── VideoSessionStore.cs           # In-memory store for video processing sessions.
│
├── Models/
│   ├── RedactionModels.cs             # Document session/entity/request/response models.
│   │                                  # PiiEntityResult includes BoundingRegions, CharRanges, PdfFaceBoxes.
│   └── AudioRedactionModels.cs        # Audio session/entity/word timestamp models.
│
├── ClientApp/src/
│   ├── App.tsx                        # Top-level: mode tabs (Document/Audio/Video) + step stepper.
│   ├── api.ts                         # All Axios API calls to the backend.
│   ├── types.ts                       # TypeScript type definitions.
│   └── components/
│       ├── FileUpload.tsx             # Document drag-and-drop zone + file type validation.
│       ├── EntitySelector.tsx         # Entity checklist with colour codes, face icon, manual word search.
│       ├── DocumentPreview.tsx        # Preview iframe (PDF via embed, HTML inline), redact confirm button.
│       ├── AudioRedaction.tsx         # Full audio flow in one component (upload → review → beep → download).
│       └── VideoRedaction.tsx         # Full video flow in one component (upload → poll → download).
│
├── Program.cs                         # DI registrations, CORS, Kestrel limits (500 MB max for video).
├── appsettings.json                   # Non-secret defaults (Face/DI/Language endpoints left empty).
├── appsettings.Development.json       # ⚠️ GITIGNORED — real API keys go here only.
└── FileRedaction.csproj               # NuGet packages.
```

---

## Key Technical Details

### Why coordinates need scaling (images)
Azure Document Intelligence has a 4 MB limit for images on the free tier. Large images (e.g. a 5 MB photo of an ID card) are compressed to JPEG before being sent to DI. DI returns word positions relative to the compressed image. The app captures the original image dimensions before compression and scales all returned coordinates back up — so when Aspose draws black boxes, they land on the right pixels in the original image.

### Why face coordinates need conversion (PDFs)
The Face API returns pixel positions. PDFs use a point coordinate system (72 points per inch) with the origin at the **bottom-left** corner of the page. When rendering a PDF page at 150 DPI:
- `x_points = pixel_x × (72 / 150)`
- `y_bottom_points = page_height_points − (pixel_top + pixel_height) × (72 / 150)`
- `y_top_points = page_height_points − pixel_top × (72 / 150)`

### Why TXT redaction uses character offsets
When you replace text at position X, everything after it shifts. If you naively replace from front to back, all subsequent offsets are wrong. The app collects all character ranges for selected entities, sorts them **back-to-front** (largest offset first), and replaces from the end of the string forward so earlier offsets are never disturbed.

### Why Excel preview is shown as HTML (not PDF)
Aspose.Cells exports Excel to a multi-file HTML bundle (a `preview.html` with companion CSS and per-sheet `.htm` files). This HTML uses JavaScript for tab switching, so it must be served in a plain `<iframe>` — no `sandbox` attribute, because sandbox blocks inline scripts.

### Why barcode images fail without special handling
GDI+ (Windows imaging library) cannot create a `Graphics` object from a 1-bit or 8-bit indexed bitmap (barcodes are typically 1bpp black-and-white). The `ToArgb32` helper converts any indexed-format bitmap to a 32-bit ARGB copy first, then drawing proceeds normally.

### Beep generation
The 1 kHz tone is a pure sine wave: `sample = sin(2π × 1000 × t)` at amplitude 0.7 (70% of max short value). Short 10 ms linear fade-in/fade-out prevents audible clicks at the beep boundaries.

---

## Known Limitations

- **Document upload cap:** 20 MB (configurable in `RedactionController.cs`).
- **Video upload cap:** 500 MB (Kestrel limit in `Program.cs`).
- **Face detection image cap:** 6 MB (Azure Face API hardware limit — images larger than this are skipped with a warning).
- **TXT and Excel face detection:** Not supported — face detection only runs on image files and PDFs.
- **Audio format:** Redacted output is always WAV (16 kHz 16-bit mono). The original input format is not preserved.
- **Manual entity addition for TXT:** Manual words are added as "Manual" category entities but do not have character offsets — they fall back to case-insensitive text search at redaction time.
- **Session storage:** Sessions are in-memory only. Restarting the server clears all sessions. Temp files are written to the OS temp directory and are not cleaned up automatically.

---

## Notes for Production Integration

### Audio
The audio flow calls Azure Speech SDK directly (synchronous, in-process). In the real WhistleB IVR integration it should be replaced by a Service Bus pattern:

1. Upload audio → publish `TranscriptionVoice` message to a Service Bus topic.
2. The existing `TranscriptionSpeechVoice` Azure Function picks it up and transcribes.
3. The function publishes `UpdateVoiceTranscription` back; a new `POST /api/audio/transcription-result` endpoint receives it and continues the PII flow.

See `INTEGRATION NOTE` comments in `AudioTranscriptionService.cs` and `AudioRedactionController.cs` for the exact payload structure needed.

### Document
The background processing (`ProcessUploadAsync`) is a fire-and-forget `Task.Run`. In production this should be a proper background worker or Azure Function triggered by a queue message, so it survives server restarts and can be scaled out.

### Video
The SecureRedact integration is based on the v3 API. The multipart field names (`blur_faces`, `blur_license_plates`, `redact_text`, `redact_audio`) and JSON response field names should be verified against the official SecureRedact docs before going live.

# FileRedaction

A POC (proof of concept) web app that automatically finds and removes personally identifiable information (PII) from documents, audio, and video files.

---

## What it does

Upload a file → the app detects names, phone numbers, email addresses, bank details, and other sensitive data → you pick what to redact → download the clean version.

**Three modes:**

| Mode | Input formats | What gets redacted |
|------|--------------|-------------------|
| **Document** | PDF, Word, Excel, PowerPoint, Images, TXT | Text is blacked out (█) or cells replaced |
| **Audio** | WAV, MP3, M4A, AAC, OGG, FLAC, WMA | PII segments replaced with a 1 kHz beep |
| **Video** | MP4, MOV, AVI, etc. | Faces, licence plates, text, audio (via SecureRedact) |

---

## How it works

### Documents
1. File is uploaded and saved temporarily on the server.
2. **Azure Document Intelligence** (`prebuilt-read`) extracts all the text with exact word positions (bounding boxes).
   - Exception: `.txt` files are read directly — no OCR needed.
3. **Azure AI Language Service** runs PII detection on the extracted text.
4. You review the detected entities in the browser and choose which ones to redact.
5. Hit **Confirm & Redact** — the app blacks out the selected text and you download the file.
   - PDFs and images: text fragments are covered with black rectangles.
   - Excel: cell values containing PII are replaced with `█` characters.
   - Word / PowerPoint: converted to PDF first via Aspose, then redacted as PDF.
   - TXT: PII spans are replaced with `█` characters using the exact character offsets from the detection step.

You can also **manually add words** — type or search for a word that wasn't auto-detected and it will be added to the redaction list.

### Audio
1. File is uploaded and decoded to 16 kHz WAV (via NAudio).
2. **Azure Speech Service** transcribes the audio with word-level timestamps.
3. The transcript is sent to Azure AI Language for PII detection.
4. Each PII entity is mapped back to its time range in the audio.
5. You pick which entities to beep out, then download the redacted WAV.
   - The beep is a 1 kHz sine wave with short fade-in/fade-out to avoid clicks.
   - You can also search for specific words and manually add them to the beep list.

### Video
Uses the **SecureRedact v3 API** (third-party service):
1. File is uploaded to SecureRedact.
2. SecureRedact automatically detects faces, licence plates, on-screen text, and spoken audio.
3. Once detection finishes, the app triggers redaction and then publishes the result.
4. You download the redacted video via a secure link.

---

## Tech stack

| Layer | Technology |
|-------|-----------|
| Backend API | ASP.NET Core 8 Web API (C#) |
| Frontend | React + TypeScript (Vite) |
| OCR / document extraction | Azure Document Intelligence |
| PII detection | Azure AI Language Service |
| Speech-to-text | Azure Cognitive Services Speech SDK |
| Document manipulation | Aspose.Words, Aspose.Cells, Aspose.Slides |
| PDF redaction | Aspose.PDF |
| Audio decode / beep | NAudio |
| Video redaction | SecureRedact API v3 |

---

## Configuration

All secrets go in `appsettings.Development.json` (gitignored — never commit this file).

```json
{
  "Azure": {
    "DocumentIntelligence": {
      "Endpoint": "<your DI endpoint>",
      "ApiKey": "<your DI key>"
    },
    "SpeechService": {
      "Key": "<your Speech key>",
      "Region": "<e.g. eastus>"
    }
  },
  "AzureLanguageService": {
    "Endpoint": "<your Language endpoint>",
    "ApiKey": "<your Language key>"
  },
  "SecureRedact": {
    "ClientId": "<your SecureRedact client id>",
    "ClientSecret": "<your SecureRedact client secret>"
  }
}
```

---

## Running locally

**Prerequisites:** .NET 8 SDK, Node 18+

```bash
# From the FileRedaction/ solution folder
dotnet run --project FileRedaction
```

The app starts on `https://localhost:5001` (or whatever port is configured). The React frontend is served as part of the same process via the Vite dev server proxy.

---

## Project structure

```
FileRedaction/
├── Controllers/
│   ├── RedactionController.cs        # Document upload, preview, redact endpoints
│   ├── AudioRedactionController.cs   # Audio upload, transcribe, redact endpoints
│   └── VideoRedactionController.cs   # Video upload, poll, redact endpoints
├── Services/
│   ├── DocumentIntelligenceService   # Azure DI wrapper
│   ├── PiiDetectionService           # Azure Language Service wrapper
│   ├── RedactionService              # PDF / image black-box redaction (Aspose.PDF)
│   ├── OfficeConversionService       # Office → PDF, Excel highlight / redact
│   ├── AudioTranscriptionService     # Speech SDK transcription
│   ├── AudioRedactionService         # WAV beep injection
│   └── SecureRedactService           # SecureRedact API client
├── Models/
│   ├── RedactionModels.cs            # Document session/entity models
│   └── AudioRedactionModels.cs       # Audio session/entity models
└── ClientApp/src/
    ├── components/
    │   ├── FileUpload.tsx            # Document drop zone
    │   ├── EntitySelector.tsx        # PII entity checklist + manual word search
    │   ├── DocumentPreview.tsx       # Highlighted preview + redact confirm
    │   ├── AudioRedaction.tsx        # Full audio flow (upload → review → beep)
    │   └── VideoRedaction.tsx        # Full video flow (upload → poll → download)
    └── App.tsx                       # Tab switcher (Document / Audio / Video)
```

---

## Notes for production integration

The audio flow currently calls Azure Speech SDK directly as a POC shortcut. In the real WhistleB IVR integration it should be replaced by a Service Bus pattern:

1. Upload audio → publish `TranscriptionVoice` message to the topic.
2. The existing `TranscriptionSpeechVoice` Azure Function picks it up and transcribes.
3. The function publishes `UpdateVoiceTranscription` back; a new `POST /api/audio/transcription-result` endpoint receives it and continues the PII flow.

See the `INTEGRATION NOTE` comments in `AudioTranscriptionService.cs` and `AudioRedactionController.cs` for the exact payload changes needed.

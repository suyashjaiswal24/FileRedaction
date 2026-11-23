using FileRedaction.Models;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace FileRedaction.Services;

public interface IRedactionService
{
    Task<string> CreateHighlightedPreviewAsync(string filePath, List<PiiEntityResult> selectedEntities);
    Task<string> AddHighlightsToExistingAsync(string existingHighlightedPath, List<PiiEntityResult> entitiesToAdd);
    Task<string> ApplyPermanentRedactionAsync(string filePath, List<PiiEntityResult> selectedEntities);
}

/// <summary>
/// PDF / image highlight preview and permanent redaction using only permissively-licensed libraries:
///  - PDFsharp (MIT)    : preview highlights, assembling the output PDF
///  - PDFtoImage (MIT)  : PDFium rendering of the pages that must be redacted
///  - SkiaSharp (MIT)   : painting the black boxes onto the rendered page
///  - Magick.NET (Apache-2.0): raster image highlight / redaction
///
/// Permanent PDF redaction works by re-rendering every page that contains a redaction region to
/// a bitmap, painting black boxes into the bitmap and replacing the page with that bitmap. The
/// text, vector content and embedded images of those pages are therefore gone from the file —
/// nothing under a black box can be selected, copied or extracted. Pages without redactions are
/// copied unchanged (text stays selectable).
/// </summary>
public class RedactionService : IRedactionService
{
    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif", ".webp"];

    /// <summary>Render resolution for redacted pages. 200 DPI keeps small print legible.</summary>
    private const int RedactRenderDpi = 200;
    private const int RedactJpegQuality = 90;
    private const double RedactPaddingPts = 1.0;

    private readonly ILogger<RedactionService> _logger;

    public RedactionService(ILogger<RedactionService> logger) => _logger = logger;

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task<string> CreateHighlightedPreviewAsync(string filePath, List<PiiEntityResult> selectedEntities)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
            return await Task.Run(() => HighlightImage(filePath, selectedEntities));

        return await Task.Run(() => HighlightPdf(filePath, selectedEntities));
    }

    public async Task<string> AddHighlightsToExistingAsync(string existingHighlightedPath, List<PiiEntityResult> entitiesToAdd)
    {
        var ext = Path.GetExtension(existingHighlightedPath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
            return await Task.Run(() => HighlightImage(existingHighlightedPath, entitiesToAdd));

        return await Task.Run(() => HighlightPdf(existingHighlightedPath, entitiesToAdd));
    }

    public async Task<string> ApplyPermanentRedactionAsync(string filePath, List<PiiEntityResult> selectedEntities)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
            return await Task.Run(() => RedactImage(filePath, selectedEntities, ext));

        return await Task.Run(() => RedactPdf(filePath, selectedEntities));
    }

    // ── PDF: highlight preview ───────────────────────────────────────────────

    private string HighlightPdf(string sourcePath, List<PiiEntityResult> entities)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.pdf");
        _logger.LogInformation("Creating highlighted PDF preview → {Path}", outPath);

        using var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
        var byPage = CollectPdfBoxes(entities, sourcePath);
        var brush = new XSolidBrush(XColor.FromArgb(120, 255, 235, 0));

        foreach (var (pageNumber, boxes) in byPage)
        {
            if (pageNumber < 1 || pageNumber > doc.PageCount) continue;
            var page = doc.Pages[pageNumber - 1];
            double pageHeight = page.Height.Point;

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            foreach (var r in boxes.Resolve(pageHeight))
                gfx.DrawRectangle(brush, r.X, r.Y, r.Width, r.Height);
        }

        doc.Save(outPath);
        return outPath;
    }

    // ── PDF: permanent redaction ─────────────────────────────────────────────

    private string RedactPdf(string filePath, List<PiiEntityResult> entities)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"redacted_{Guid.NewGuid():N}.pdf");
        _logger.LogInformation("Applying permanent PDF redaction → {Path}", outPath);

        var byPage = CollectPdfBoxes(entities, filePath);
        _logger.LogInformation("PDF redaction: {Pages} page(s) with boxes, {Boxes} box(es) total", byPage.Count, byPage.Values.Sum(b => b.Count));
        if (byPage.Count == 0)
            _logger.LogWarning("No redaction boxes were located in the PDF — output will be identical to the input");

        var pdfBytes = File.ReadAllBytes(filePath);
        var keepAlive = new List<IDisposable>();

        try
        {
            using var source = PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            output.Info.Title = source.Info.Title;

            for (int i = 0; i < source.PageCount; i++)
            {
                int pageNumber = i + 1;

                if (!byPage.TryGetValue(pageNumber, out var boxes))
                {
                    output.AddPage(source.Pages[i]);   // untouched page — copied as-is
                    continue;
                }

                var pageSize = Conversion.GetPageSize(new MemoryStream(pdfBytes), i, false, null);
                using var bitmap = Conversion.ToImage(
                    new MemoryStream(pdfBytes), i, false, null,
                    new RenderOptions { Dpi = RedactRenderDpi, WithAnnotations = true });

                float scale = bitmap.Width / (float)pageSize.Width;

                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
                {
                    foreach (var r in boxes.Resolve(pageSize.Height))
                    {
                        canvas.DrawRect(SKRect.Create(
                            (float)((r.X - RedactPaddingPts) * scale),
                            (float)((r.Y - RedactPaddingPts) * scale),
                            (float)((r.Width + 2 * RedactPaddingPts) * scale),
                            (float)((r.Height + 2 * RedactPaddingPts) * scale)), paint);
                    }
                    canvas.Flush();
                }

                var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, RedactJpegQuality);
                var imageStream = new MemoryStream(data.ToArray());
                data.Dispose();
                var xImage = XImage.FromStream(imageStream);
                keepAlive.Add(imageStream);
                keepAlive.Add(xImage);

                var page = output.AddPage();
                page.Width = XUnit.FromPoint(pageSize.Width);
                page.Height = XUnit.FromPoint(pageSize.Height);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, pageSize.Width, pageSize.Height);

                _logger.LogInformation("  Page {Page}: rasterised with {Count} redaction box(es)", pageNumber, boxes.Count);
            }

            output.Save(outPath);
        }
        finally
        {
            foreach (var d in keepAlive) d.Dispose();
        }

        return outPath;
    }

    // ── PDF helpers ──────────────────────────────────────────────────────────

    private readonly record struct Box(double X, double Y, double Width, double Height);

    /// <summary>Boxes on one page. Top-left-origin boxes come from Document Intelligence; face boxes are bottom-left-origin.</summary>
    private sealed class PageBoxes
    {
        public List<Box> TopLeft { get; } = [];
        public List<(double X1, double Y1, double X2, double Y2)> BottomLeft { get; } = [];
        public int Count => TopLeft.Count + BottomLeft.Count;

        public IEnumerable<Box> Resolve(double pageHeight)
        {
            foreach (var b in TopLeft) yield return b;
            foreach (var f in BottomLeft)
                yield return new Box(f.X1, pageHeight - f.Y2, f.X2 - f.X1, f.Y2 - f.Y1);
        }
    }

    private Dictionary<int, PageBoxes> CollectPdfBoxes(IEnumerable<PiiEntityResult> entities, string pdfPath)
    {
        var entityList = entities.ToList();
        var result = new Dictionary<int, PageBoxes>();

        PageBoxes For(int page)
        {
            if (!result.TryGetValue(page, out var pb)) result[page] = pb = new PageBoxes();
            return pb;
        }

        foreach (var entity in entityList)
        {
            foreach (var region in entity.BoundingRegions.Where(r => !r.IsPixelUnit && r.Polygon.Length >= 8))
            {
                var p = region.Polygon;
                double minX = Math.Min(Math.Min(p[0], p[2]), Math.Min(p[4], p[6])) * 72;
                double maxX = Math.Max(Math.Max(p[0], p[2]), Math.Max(p[4], p[6])) * 72;
                double minY = Math.Min(Math.Min(p[1], p[3]), Math.Min(p[5], p[7])) * 72;
                double maxY = Math.Max(Math.Max(p[1], p[3]), Math.Max(p[5], p[7])) * 72;
                For(region.PageNumber).TopLeft.Add(new Box(minX, minY, maxX - minX, maxY - minY));
            }

            foreach (var f in entity.PdfFaceBoxes)
                For(f.PageNumber).BottomLeft.Add((f.X1, f.Y1, f.X2, f.Y2));
        }

        // Text-layer search. Required for Word/PowerPoint (Document Intelligence returns no coordinates
        // for Office files, so the converted PDF is searched instead) and catches every occurrence in
        // digital PDFs. Scanned pages have no text layer — those rely on the DI regions above.
        try
        {
            foreach (var (page, box) in FindTextBoxes(pdfPath, entityList))
                For(page).TopLeft.Add(box);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text-layer search failed for {Path}", pdfPath);
        }

        return result;
    }

    /// <summary>
    /// Finds every occurrence of each entity's text in the PDF text layer (case-insensitive, whitespace-insensitive)
    /// and returns one box per text line, in top-left-origin points.
    /// </summary>
    private static List<(int Page, Box Box)> FindTextBoxes(string pdfPath, List<PiiEntityResult> entities)
    {
        var needles = entities
            .Where(e => e.Category != "Face" && !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => new string(e.Text.Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var found = new List<(int, Box)>();
        if (needles.Count == 0) return found;

        using var doc = PigDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0) continue;

            // One normalized string, plus a map from each char back to its letter
            var sb = new System.Text.StringBuilder();
            var map = new List<int>();
            for (int i = 0; i < letters.Count; i++)
                foreach (var ch in letters[i].Value)
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    sb.Append(ch);
                    map.Add(i);
                }
            var haystack = sb.ToString();

            foreach (var needle in needles)
            {
                int pos = 0;
                while ((pos = haystack.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int first = map[pos], last = map[pos + needle.Length - 1];
                    pos += needle.Length;

                    // Group the matched letters into lines (new line when the baseline jumps)
                    var line = new List<UglyToad.PdfPig.Content.Letter>();
                    void Flush()
                    {
                        if (line.Count == 0) return;
                        double x1 = line.Min(l => l.GlyphRectangle.Left);
                        double x2 = line.Max(l => l.GlyphRectangle.Right);
                        double yBottom = line.Min(l => l.GlyphRectangle.Bottom);
                        double yTop = line.Max(l => l.GlyphRectangle.Top);
                        found.Add((page.Number, new Box(x1, page.Height - yTop, x2 - x1, yTop - yBottom)));
                        line.Clear();
                    }

                    for (int i = first; i <= last; i++)
                    {
                        if (line.Count > 0)
                        {
                            var prev = line[^1].GlyphRectangle;
                            var cur = letters[i].GlyphRectangle;
                            if (Math.Abs(cur.Bottom - prev.Bottom) > Math.Max(prev.Height, cur.Height) * 0.6) Flush();
                        }
                        line.Add(letters[i]);
                    }
                    Flush();
                }
            }
        }
        return found;
    }

    // ── Image: highlight / redaction ─────────────────────────────────────────

    private string HighlightImage(string sourcePath, List<PiiEntityResult> entities)
    {
        // Always save as PNG (lossless — avoids JPEG artifacts around highlights)
        var outPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.png");
        _logger.LogInformation("Creating highlighted image preview → {Path}", outPath);

        using var image = new MagickImage(File.ReadAllBytes(sourcePath));
        var (dpiX, dpiY) = GetDpi(image);

        var drawables = new Drawables()
            .FillColor(MagickColor.FromRgba(255, 255, 0, 120))
            .StrokeColor(MagickColor.FromRgba(255, 165, 0, 200))
            .StrokeWidth(1.5);

        bool any = false;
        foreach (var entity in entities)
            foreach (var region in entity.BoundingRegions)
            {
                var rect = PolygonToPixelRect(region.Polygon, region.IsPixelUnit, dpiX, dpiY);
                if (rect is null) continue;
                drawables.Rectangle(rect.Value.X, rect.Value.Y, rect.Value.Right, rect.Value.Bottom);
                any = true;
            }

        if (any) drawables.Draw(image);

        image.Format = MagickFormat.Png;
        image.Write(outPath);
        return outPath;
    }

    private string RedactImage(string sourcePath, List<PiiEntityResult> entities, string ext)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"redacted_{Guid.NewGuid():N}{ext}");
        _logger.LogInformation("Applying permanent image redaction → {Path}", outPath);

        using var image = new MagickImage(File.ReadAllBytes(sourcePath));
        var (dpiX, dpiY) = GetDpi(image);

        var drawables = new Drawables()
            .FillColor(MagickColors.Black)
            .StrokeColor(MagickColors.Black)
            .StrokeWidth(1);

        foreach (var entity in entities)
            foreach (var region in entity.BoundingRegions)
            {
                var rect = PolygonToPixelRect(region.Polygon, region.IsPixelUnit, dpiX, dpiY);
                if (rect is null) continue;
                var r = rect.Value;
                drawables.Rectangle(r.X - 1, r.Y - 1, r.Right + 1, r.Bottom + 1);
                _logger.LogInformation("  Redact [{Cat}] '{Text}' at {Rect}", entity.Category, entity.Text, r);
            }

        drawables.Draw(image);

        // Drop EXIF / embedded thumbnails — they can still contain the un-redacted picture.
        image.Strip();
        if (ext is ".jpg" or ".jpeg") image.Quality = 95;
        image.Write(outPath);
        return outPath;
    }

    private static (double X, double Y) GetDpi(MagickImage image)
    {
        var d = image.Density;
        if (d.X <= 0 || d.Y <= 0) return (96, 96);
        return d.Units == DensityUnit.PixelsPerCentimeter
            ? (d.X * 2.54, d.Y * 2.54)
            : (d.X, d.Y);
    }

    /// <summary>
    /// Converts a DI bounding polygon ([x1,y1,…x4,y4]) to a pixel rectangle.
    /// Images: DI already returns pixels. Inch-based polygons are scaled by the image DPI.
    /// </summary>
    private static PixelRect? PolygonToPixelRect(double[] poly, bool isPixelUnit, double dpiX, double dpiY)
    {
        if (poly.Length < 8) return null;

        double minX = Math.Min(Math.Min(poly[0], poly[2]), Math.Min(poly[4], poly[6]));
        double maxX = Math.Max(Math.Max(poly[0], poly[2]), Math.Max(poly[4], poly[6]));
        double minY = Math.Min(Math.Min(poly[1], poly[3]), Math.Min(poly[5], poly[7]));
        double maxY = Math.Max(Math.Max(poly[1], poly[3]), Math.Max(poly[5], poly[7]));

        if (!isPixelUnit)
        {
            minX *= dpiX; maxX *= dpiX;
            minY *= dpiY; maxY *= dpiY;
        }

        return new PixelRect(minX, minY, maxX, maxY);
    }

    private readonly record struct PixelRect(double X, double Y, double Right, double Bottom);
}

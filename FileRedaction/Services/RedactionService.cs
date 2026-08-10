extern alias AsposeDrawing;

using System.Text.RegularExpressions;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Text;
using AD = AsposeDrawing::System.Drawing;
using FileRedaction.Models;

namespace FileRedaction.Services;

public interface IRedactionService
{
    Task<string> CreateHighlightedPreviewAsync(string filePath, List<PiiEntityResult> selectedEntities);
    Task<string> AddHighlightsToExistingAsync(string existingHighlightedPath, List<PiiEntityResult> entitiesToAdd);
    Task<string> ApplyPermanentRedactionAsync(string filePath, List<PiiEntityResult> selectedEntities);
}

public class RedactionService : IRedactionService
{
    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif", ".webp"];

    private readonly ILogger<RedactionService> _logger;

    public RedactionService(ILogger<RedactionService> logger) => _logger = logger;

    public async Task<string> CreateHighlightedPreviewAsync(string filePath, List<PiiEntityResult> selectedEntities)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
            return await CreateHighlightedImageAsync(filePath, selectedEntities, ext);

        // PDF path
        return await Task.Run(() =>
        {
            var previewPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.pdf");
            _logger.LogInformation("Creating highlighted PDF preview → {Path}", previewPath);

            using var doc = new Aspose.Pdf.Document(filePath);

            foreach (var entity in selectedEntities)
            {
                var fragments = FindTextFragments(doc, entity.Text);
                if (fragments.Count > 0)
                {
                    foreach (var fragment in fragments)
                    {
                        var highlight = new HighlightAnnotation(fragment.Page, fragment.Rectangle)
                        {
                            Color = Aspose.Pdf.Color.Yellow,
                            Opacity = 0.5,
                            Title = entity.Category,
                            Contents = $"{entity.Category}: {entity.Text}"
                        };
                        fragment.Page.Annotations.Add(highlight);
                    }
                }
                else
                {
                    // Text is inside an embedded image — TextFragmentAbsorber can't find it.
                    // Fall back to DI bounding regions (inch coordinates → PDF points).
                    foreach (var region in entity.BoundingRegions.Where(r => !r.IsPixelUnit))
                    {
                        var page = doc.Pages[region.PageNumber];
                        var rect = BoundingRegionToPdfRect(region, page);
                        if (rect is null) continue;
                        var sq = new SquareAnnotation(page, rect)
                        {
                            Color = Aspose.Pdf.Color.Yellow,
                            InteriorColor = Aspose.Pdf.Color.Yellow,
                            Opacity = 0.45
                        };
                        page.Annotations.Add(sq);
                    }
                }

                foreach (var box in entity.PdfFaceBoxes)
                {
                    var page = doc.Pages[box.PageNumber];
                    var sq = new SquareAnnotation(page, new Aspose.Pdf.Rectangle(box.X1, box.Y1, box.X2, box.Y2))
                    {
                        Color = Aspose.Pdf.Color.Yellow,
                        InteriorColor = Aspose.Pdf.Color.Yellow,
                        Opacity = 0.45
                    };
                    page.Annotations.Add(sq);
                }
            }

            doc.Save(previewPath);
            return previewPath;
        });
    }

    public async Task<string> AddHighlightsToExistingAsync(string existingHighlightedPath, List<PiiEntityResult> entitiesToAdd)
    {
        var ext = Path.GetExtension(existingHighlightedPath).ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
        {
            return await Task.Run(() =>
            {
                var outPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.png");
                using var ms = new MemoryStream(File.ReadAllBytes(existingHighlightedPath));
                using var raw0 = new AD.Bitmap(ms);
                using var bmp = ToArgb32(raw0);
                using var g = AD.Graphics.FromImage(bmp);

                var dpiX = bmp.HorizontalResolution;
                var dpiY = bmp.VerticalResolution;

                using var brush = new AD.SolidBrush(System.Drawing.Color.FromArgb(120, System.Drawing.Color.Yellow));
                using var pen = new AD.Pen(System.Drawing.Color.FromArgb(200, System.Drawing.Color.Orange), 1.5f);

                foreach (var entity in entitiesToAdd)
                    foreach (var region in entity.BoundingRegions)
                    {
                        var rect = PolygonToPixelRect(region.Polygon, region.IsPixelUnit, dpiX, dpiY);
                        if (rect == System.Drawing.RectangleF.Empty) continue;
                        g.FillRectangle(brush, rect);
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                    }

                using var outStream = File.OpenWrite(outPath);
                bmp.Save(outStream, AsposeDrawing::System.Drawing.Imaging.ImageFormat.Png);
                return outPath;
            });
        }

        // PDF: open existing highlighted PDF, add new annotations on top
        return await Task.Run(() =>
        {
            var outPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.pdf");
            using var doc = new Aspose.Pdf.Document(existingHighlightedPath);

            foreach (var entity in entitiesToAdd)
            {
                var fragments = FindTextFragments(doc, entity.Text);
                if (fragments.Count > 0)
                {
                    foreach (var fragment in fragments)
                    {
                        var highlight = new HighlightAnnotation(fragment.Page, fragment.Rectangle)
                        {
                            Color = Aspose.Pdf.Color.Yellow,
                            Opacity = 0.5,
                            Title = entity.Category,
                            Contents = $"{entity.Category}: {entity.Text}"
                        };
                        fragment.Page.Annotations.Add(highlight);
                    }
                }
                else
                {
                    foreach (var region in entity.BoundingRegions.Where(r => !r.IsPixelUnit))
                    {
                        var page = doc.Pages[region.PageNumber];
                        var rect = BoundingRegionToPdfRect(region, page);
                        if (rect is null) continue;
                        var sq = new SquareAnnotation(page, rect)
                        {
                            Color = Aspose.Pdf.Color.Yellow,
                            InteriorColor = Aspose.Pdf.Color.Yellow,
                            Opacity = 0.45
                        };
                        page.Annotations.Add(sq);
                    }
                }

                foreach (var box in entity.PdfFaceBoxes)
                {
                    var page = doc.Pages[box.PageNumber];
                    var sq = new SquareAnnotation(page, new Aspose.Pdf.Rectangle(box.X1, box.Y1, box.X2, box.Y2))
                    {
                        Color = Aspose.Pdf.Color.Yellow,
                        InteriorColor = Aspose.Pdf.Color.Yellow,
                        Opacity = 0.45
                    };
                    page.Annotations.Add(sq);
                }
            }

            doc.Save(outPath);
            return outPath;
        });
    }

    public async Task<string> ApplyPermanentRedactionAsync(string filePath, List<PiiEntityResult> selectedEntities)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
            return await ApplyImageRedactionAsync(filePath, selectedEntities, ext);

        // PDF path
        return await Task.Run(() =>
        {
            var redactedPath = Path.Combine(Path.GetTempPath(), $"redacted_{Guid.NewGuid():N}.pdf");
            _logger.LogInformation("Applying permanent PDF redaction → {Path}", redactedPath);

            using var doc = new Aspose.Pdf.Document(filePath);

            foreach (var entity in selectedEntities)
            {
                var fragments = FindTextFragments(doc, entity.Text);
                _logger.LogInformation("Redacting '{Text}' — {Count} occurrence(s) in text layer", entity.Text, fragments.Count);

                if (fragments.Count > 0)
                {
                    foreach (var fragment in fragments)
                    {
                        var rect = new Aspose.Pdf.Rectangle(
                            fragment.Rectangle.LLX - 1, fragment.Rectangle.LLY - 1,
                            fragment.Rectangle.URX + 1, fragment.Rectangle.URY + 1);

                        var redaction = new RedactionAnnotation(fragment.Page, rect)
                        {
                            FillColor = Aspose.Pdf.Color.Black,
                            BorderColor = Aspose.Pdf.Color.Black,
                            Color = Aspose.Pdf.Color.Black,
                            OverlayText = string.Empty
                        };
                        fragment.Page.Annotations.Add(redaction);
                        redaction.Redact();
                    }
                }
                else
                {
                    // Text is inside an embedded image — paint a black box over the DI bounding region.
                    foreach (var region in entity.BoundingRegions.Where(r => !r.IsPixelUnit))
                    {
                        var page = doc.Pages[region.PageNumber];
                        var rect = BoundingRegionToPdfRect(region, page);
                        if (rect is null) continue;
                        var expanded = new Aspose.Pdf.Rectangle(rect.LLX - 1, rect.LLY - 1, rect.URX + 1, rect.URY + 1);
                        var redaction = new RedactionAnnotation(page, expanded)
                        {
                            FillColor = Aspose.Pdf.Color.Black,
                            BorderColor = Aspose.Pdf.Color.Black,
                            Color = Aspose.Pdf.Color.Black,
                            OverlayText = string.Empty
                        };
                        page.Annotations.Add(redaction);
                        redaction.Redact();
                        _logger.LogInformation("  Embedded-image text redacted on page {Page}", region.PageNumber);
                    }
                }

                // Face boxes — no text to search, apply redaction annotation directly at stored coordinates
                foreach (var box in entity.PdfFaceBoxes)
                {
                    var page = doc.Pages[box.PageNumber];
                    var faceRedaction = new RedactionAnnotation(page, new Aspose.Pdf.Rectangle(box.X1, box.Y1, box.X2, box.Y2))
                    {
                        FillColor = Aspose.Pdf.Color.Black,
                        BorderColor = Aspose.Pdf.Color.Black,
                        Color = Aspose.Pdf.Color.Black,
                        OverlayText = string.Empty
                    };
                    page.Annotations.Add(faceRedaction);
                    faceRedaction.Redact();
                    _logger.LogInformation("  Face redacted on page {Page} at [{X1},{Y1},{X2},{Y2}]",
                        box.PageNumber, box.X1, box.Y1, box.X2, box.Y2);
                }
            }

            doc.Save(redactedPath);
            return redactedPath;
        });
    }

    // ── Image helpers ────────────────────────────────────────────────────────

    private Task<string> CreateHighlightedImageAsync(string filePath, List<PiiEntityResult> entities, string ext)
    {
        return Task.Run(() =>
        {
            // Always save preview as PNG (lossless, avoids JPEG re-compression artifacts on highlights)
            var outPath = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}.png");
            _logger.LogInformation("Creating highlighted image preview → {Path}", outPath);

            // Load via MemoryStream so the original file is not locked during processing
            using var ms = new MemoryStream(File.ReadAllBytes(filePath));
            using var raw1 = new AD.Bitmap(ms);
            using var bmp = ToArgb32(raw1);
            using var g = AD.Graphics.FromImage(bmp);

            var dpiX = bmp.HorizontalResolution;
            var dpiY = bmp.VerticalResolution;
            _logger.LogInformation("Image DPI: {X} x {Y}, size: {W}x{H}px", dpiX, dpiY, bmp.Width, bmp.Height);

            using var brush = new AD.SolidBrush(System.Drawing.Color.FromArgb(120, System.Drawing.Color.Yellow));
            using var pen = new AD.Pen(System.Drawing.Color.FromArgb(200, System.Drawing.Color.Orange), 1.5f);

            foreach (var entity in entities)
            {
                foreach (var region in entity.BoundingRegions)
                {
                    var rect = PolygonToPixelRect(region.Polygon, region.IsPixelUnit, dpiX, dpiY);
                    if (rect == System.Drawing.RectangleF.Empty) continue;
                    g.FillRectangle(brush, rect);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                    _logger.LogInformation("  Highlight [{Cat}] '{Text}' at {Rect}", entity.Category, entity.Text, rect);
                }
            }

            using var outStream = File.OpenWrite(outPath);
            bmp.Save(outStream, AsposeDrawing::System.Drawing.Imaging.ImageFormat.Png);
            return outPath;
        });
    }

    private Task<string> ApplyImageRedactionAsync(string filePath, List<PiiEntityResult> entities, string ext)
    {
        return Task.Run(() =>
        {
            var outPath = Path.Combine(Path.GetTempPath(), $"redacted_{Guid.NewGuid():N}{ext}");
            _logger.LogInformation("Applying permanent image redaction → {Path}", outPath);

            using var ms = new MemoryStream(File.ReadAllBytes(filePath));
            using var raw2 = new AD.Bitmap(ms);
            using var bmp = ToArgb32(raw2);
            using var g = AD.Graphics.FromImage(bmp);

            var dpiX = bmp.HorizontalResolution;
            var dpiY = bmp.VerticalResolution;

            using var brush = new AD.SolidBrush(System.Drawing.Color.Black);

            foreach (var entity in entities)
            {
                foreach (var region in entity.BoundingRegions)
                {
                    var rect = PolygonToPixelRect(region.Polygon, region.IsPixelUnit, dpiX, dpiY);
                    if (rect == System.Drawing.RectangleF.Empty) continue;
                    var expanded = System.Drawing.RectangleF.FromLTRB(rect.Left - 1, rect.Top - 1, rect.Right + 1, rect.Bottom + 1);
                    g.FillRectangle(brush, expanded);
                    _logger.LogInformation("  Redact [{Cat}] '{Text}' at {Rect}", entity.Category, entity.Text, expanded);
                }
            }

            var format = GetImageFormatForExt(ext);
            using var outStream = File.OpenWrite(outPath);
            bmp.Save(outStream, format);
            return outPath;
        });
    }

    /// <summary>
    /// Indexed-pixel-format bitmaps (1bpp/4bpp/8bpp — common for barcodes and scanned BW images)
    /// cannot be the target of a Graphics context. Convert to 32bpp ARGB first.
    /// DrawImage FROM an indexed source onto a 32bpp target is fine.
    /// </summary>
    private static AD.Bitmap ToArgb32(AD.Bitmap src)
    {
        if ((src.PixelFormat & AD.Imaging.PixelFormat.Indexed) == 0)
        {
            // Already a direct-color format — return a clone so the caller always owns a fresh bitmap
            return src.Clone(new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
                AD.Imaging.PixelFormat.Format32bppArgb);
        }
        var dst = new AD.Bitmap(src.Width, src.Height, AD.Imaging.PixelFormat.Format32bppArgb);
        dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
        using var g = AD.Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        return dst;
    }

    private static AsposeDrawing::System.Drawing.Imaging.ImageFormat GetImageFormatForExt(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => AsposeDrawing::System.Drawing.Imaging.ImageFormat.Jpeg,
        ".bmp"            => AsposeDrawing::System.Drawing.Imaging.ImageFormat.Bmp,
        ".gif"            => AsposeDrawing::System.Drawing.Imaging.ImageFormat.Gif,
        ".tiff" or ".tif" => AsposeDrawing::System.Drawing.Imaging.ImageFormat.Tiff,
        _                 => AsposeDrawing::System.Drawing.Imaging.ImageFormat.Png
    };

    /// <summary>
    /// Converts a DI bounding polygon ([x1,y1,…x4,y4] in inches) to a pixel RectangleF.
    /// Returns RectangleF.Empty if the polygon has fewer than 8 values.
    /// </summary>
    /// <summary>
    /// Converts a DI bounding polygon to a pixel RectangleF.
    /// Images: DI returns pixel coordinates directly — use as-is.
    /// PDFs: DI returns inch coordinates — multiply by image DPI (not used for PDFs in this path).
    /// </summary>
    private static System.Drawing.RectangleF PolygonToPixelRect(double[] poly, bool isPixelUnit, float dpiX, float dpiY)
    {
        if (poly.Length < 8) return System.Drawing.RectangleF.Empty;

        var xs = new[] { poly[0], poly[2], poly[4], poly[6] };
        var ys = new[] { poly[1], poly[3], poly[5], poly[7] };

        float x, y, w, h;
        if (isPixelUnit)
        {
            // Coordinates already in pixels
            x = (float)xs.Min();
            y = (float)ys.Min();
            w = (float)(xs.Max() - xs.Min());
            h = (float)(ys.Max() - ys.Min());
        }
        else
        {
            // Inches → pixels
            x = (float)(xs.Min() * dpiX);
            y = (float)(ys.Min() * dpiY);
            w = (float)((xs.Max() - xs.Min()) * dpiX);
            h = (float)((ys.Max() - ys.Min()) * dpiY);
        }

        return new System.Drawing.RectangleF(x, y, w, h);
    }

    // ── PDF helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a DI bounding polygon (inches, Y increases downward) to an Aspose.Pdf Rectangle
    /// (points = inches × 72, Y increases upward from page bottom).
    /// </summary>
    private static Aspose.Pdf.Rectangle? BoundingRegionToPdfRect(BoundingRegion region, Aspose.Pdf.Page page)
    {
        var poly = region.Polygon;
        if (poly.Length < 8) return null;

        var xs = new[] { poly[0], poly[2], poly[4], poly[6] };
        var ys = new[] { poly[1], poly[3], poly[5], poly[7] };

        double pageHeight = page.Rect.Height; // page height in PDF points
        double llx = xs.Min() * 72;
        double urx = xs.Max() * 72;
        double ury = pageHeight - ys.Min() * 72; // DI top edge (small Y) → PDF high Y
        double lly = pageHeight - ys.Max() * 72; // DI bottom edge (large Y) → PDF low Y

        return new Aspose.Pdf.Rectangle(llx, lly, urx, ury);
    }

    private static List<TextFragment> FindTextFragments(Aspose.Pdf.Document doc, string entityText)
    {
        var pattern = $"(?i){Regex.Escape(entityText)}";
        var options = new TextSearchOptions(isRegularExpressionUsed: true)
        {
            SearchForTextRelatedGraphics = false
        };
        var absorber = new TextFragmentAbsorber(pattern, options);
        doc.Pages.Accept(absorber);
        return absorber.TextFragments.Cast<TextFragment>().ToList();
    }
}

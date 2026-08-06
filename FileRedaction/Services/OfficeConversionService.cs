namespace FileRedaction.Services;

public interface IOfficeConversionService
{
    bool IsOfficeFormat(string extension);
    bool NeedsPdfConversion(string extension);
    bool IsExcelFormat(string extension);
    string ConvertToPdf(string filePath);
    string CreateHighlightedExcel(string filePath, List<string> textsToHighlight);
    string AddHighlightsToExistingExcel(string existingXlsxPath, List<string> textsToAdd);
    string ExportExcelToHtml(string xlsxPath);
    string RedactExcel(string filePath, List<string> textsToRedact);
}

public class OfficeConversionService : IOfficeConversionService
{
    private static readonly HashSet<string> WordExts =
        [".docx", ".doc", ".docm", ".odt", ".rtf"];
    private static readonly HashSet<string> CellsExts =
        [".xlsx", ".xls", ".ods"];
    private static readonly HashSet<string> SlidesExts =
        [".pptx", ".ppt", ".odp"];

    public bool IsOfficeFormat(string ext) =>
        WordExts.Contains(ext) || CellsExts.Contains(ext) || SlidesExts.Contains(ext);

    /// <summary>Only Word and Slides need PDF conversion. Excel stays as-is.</summary>
    public bool NeedsPdfConversion(string ext) =>
        WordExts.Contains(ext) || SlidesExts.Contains(ext);

    public bool IsExcelFormat(string ext) => CellsExts.Contains(ext);

    public string ConvertToPdf(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var outPath = Path.ChangeExtension(filePath, ".pdf");

        if (WordExts.Contains(ext))
        {
            var doc = new Aspose.Words.Document(filePath);
            doc.Save(outPath, Aspose.Words.SaveFormat.Pdf);
        }
        else if (CellsExts.Contains(ext))
        {
            throw new NotSupportedException("Excel files do not need PDF conversion — handle natively.");
        }
        else if (SlidesExts.Contains(ext))
        {
            using var pres = new Aspose.Slides.Presentation(filePath);
            pres.Save(outPath, Aspose.Slides.Export.SaveFormat.Pdf);
        }
        else
        {
            throw new NotSupportedException($"Cannot convert '{ext}' to PDF.");
        }

        return outPath;
    }

    public string CreateHighlightedExcel(string filePath, List<string> textsToHighlight)
    {
        var wb = new Aspose.Cells.Workbook(filePath);
        ApplyCellHighlights(wb, textsToHighlight);
        var outPath = Path.Combine(Path.GetTempPath(), $"highlighted_{Guid.NewGuid():N}.xlsx");
        wb.Save(outPath);
        return outPath;
    }

    public string AddHighlightsToExistingExcel(string existingXlsxPath, List<string> textsToAdd)
    {
        var wb = new Aspose.Cells.Workbook(existingXlsxPath);
        ApplyCellHighlights(wb, textsToAdd);
        var outPath = Path.Combine(Path.GetTempPath(), $"highlighted_{Guid.NewGuid():N}.xlsx");
        wb.Save(outPath);
        return outPath;
    }

    public string ExportExcelToHtml(string xlsxPath)
    {
        var wb = new Aspose.Cells.Workbook(xlsxPath);
        var outDir = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "preview.html");
        wb.Save(outPath, Aspose.Cells.SaveFormat.Html);
        return outPath;
    }

    private static void ApplyCellHighlights(Aspose.Cells.Workbook wb, List<string> texts)
    {
        var highlightStyle = wb.CreateStyle();
        highlightStyle.ForegroundColor = System.Drawing.Color.FromArgb(255, 215, 0);
        highlightStyle.Pattern = Aspose.Cells.BackgroundType.Solid;

        var styleFlag = new Aspose.Cells.StyleFlag { CellShading = true };
        var findOptions = new Aspose.Cells.FindOptions
        {
            CaseSensitive = false,
            LookInType = Aspose.Cells.LookInType.Values,
            LookAtType = Aspose.Cells.LookAtType.Contains
        };

        foreach (var sheet in wb.Worksheets)
        {
            foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                Aspose.Cells.Cell? cell = null;
                do
                {
                    cell = sheet.Cells.Find(text, cell, findOptions);
                    cell?.SetStyle(highlightStyle, styleFlag);
                } while (cell != null);
            }
        }
    }

    public string RedactExcel(string filePath, List<string> textsToRedact)
    {
        var wb = new Aspose.Cells.Workbook(filePath);
        var replaceOptions = new Aspose.Cells.ReplaceOptions
        {
            CaseSensitive = false,
            MatchEntireCellContents = false
        };

        foreach (var text in textsToRedact.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            wb.Replace(text, new string('█', text.Length), replaceOptions);
        }

        var ext = Path.GetExtension(filePath);
        var outPath = Path.Combine(
            Path.GetDirectoryName(filePath)!,
            Path.GetFileNameWithoutExtension(filePath) + "_redacted" + ext);
        wb.Save(outPath);
        return outPath;
    }
}

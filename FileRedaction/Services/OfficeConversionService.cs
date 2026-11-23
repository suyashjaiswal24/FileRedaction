using OfficeIMO;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Html;

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
        [".docx", ".doc", ".docm", ".odt", ".rtf", ".txt"];
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

        if (CellsExts.Contains(ext))
            throw new NotSupportedException("Excel files do not need PDF conversion — handle natively.");

        if (!WordExts.Contains(ext) && !SlidesExts.Contains(ext))
            throw new NotSupportedException($"Cannot convert '{ext}' to PDF.");

        OfficeConverter.Convert(filePath, outPath);

        return outPath;
    }

    public string CreateHighlightedExcel(string filePath, List<string> textsToHighlight)
    {
        using var wb = ExcelDocument.Load(filePath);
        ApplyCellHighlights(wb, textsToHighlight);
        var outPath = Path.Combine(Path.GetTempPath(), $"highlighted_{Guid.NewGuid():N}.xlsx");
        wb.Save(outPath);
        return outPath;
    }

    public string AddHighlightsToExistingExcel(string existingXlsxPath, List<string> textsToAdd)
    {
        using var wb = ExcelDocument.Load(existingXlsxPath);
        ApplyCellHighlights(wb, textsToAdd);
        var outPath = Path.Combine(Path.GetTempPath(), $"highlighted_{Guid.NewGuid():N}.xlsx");
        wb.Save(outPath);
        return outPath;
    }

    public string ExportExcelToHtml(string xlsxPath)
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"preview_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "preview.html");
        using var wb = ExcelDocument.Load(xlsxPath);
        // Visual-review profile keeps cell fills, so the yellow highlights show in the preview
        wb.SaveAsHtml(outPath, ExcelHtmlExportOptions.CreateVisualReviewProfile());
        return outPath;
    }

    private static void ApplyCellHighlights(ExcelDocument wb, List<string> texts)
    {
        var highlight = OfficeColor.Parse("FFD700");
        foreach (var sheet in wb.Sheets)
        {
            var usedRange = sheet.UsedRange;
            if (usedRange is null) continue;

            for (var row = usedRange.FirstRow; row <= usedRange.LastRow; row++)
            for (var column = usedRange.FirstColumn; column <= usedRange.LastColumn; column++)
            {
                var cell = sheet.Cell(row, column);
                var value = cell.GetFormattedText(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (texts.Any(text => !string.IsNullOrWhiteSpace(text)
                    && value.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    cell.SetFillColor(highlight);
            }
        }
    }

    public string RedactExcel(string filePath, List<string> textsToRedact)
    {
        using var wb = ExcelDocument.Load(filePath);
        foreach (var sheet in wb.Sheets)
        {
            var usedRange = sheet.UsedRange;
            if (usedRange is null) continue;

            for (var row = usedRange.FirstRow; row <= usedRange.LastRow; row++)
            for (var column = usedRange.FirstColumn; column <= usedRange.LastColumn; column++)
            {
                var cell = sheet.Cell(row, column);
                var original = cell.GetFormattedText(System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;
                if (original.Length == 0) continue;

                // Longest first so overlapping entities don't leave partial text behind
                var value = original;
                foreach (var text in textsToRedact.Where(t => !string.IsNullOrWhiteSpace(t)).OrderByDescending(t => t.Length))
                    if (value.Contains(text, StringComparison.OrdinalIgnoreCase))
                        value = ReplaceTextCaseInsensitive(value, text, new string('█', text.Length));

                // Only touch cells that actually contained PII (numbers/formulas elsewhere stay intact)
                if (!string.Equals(value, original, StringComparison.Ordinal))
                    cell.SetValue(value);
            }
        }

        var ext = Path.GetExtension(filePath);
        var outPath = Path.Combine(
            Path.GetDirectoryName(filePath)!,
            Path.GetFileNameWithoutExtension(filePath) + "_redacted" + ext);
        wb.Save(outPath);
        return outPath;
    }

    private static string ReplaceTextCaseInsensitive(string input, string find, string replacement)
    {
        var start = 0;
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var match = input.IndexOf(find, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                result.Append(input, start, input.Length - start);
                return result.ToString();
            }

            result.Append(input, start, match - start).Append(replacement);
            start = match + find.Length;
        }
    }
}

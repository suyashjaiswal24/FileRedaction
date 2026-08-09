using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MsgReader.Outlook;

namespace FileRedaction.Services;

public record EmailAttachmentInfo(string OriginalName, string TempFilePath, string Extension);

public class EmailParseResult
{
    public string BodyTempFilePath { get; set; } = string.Empty;
    public List<EmailAttachmentInfo> Attachments { get; set; } = new();
}

public interface IEmailParserService
{
    EmailParseResult Parse(string filePath);
}

public class EmailParserService : IEmailParserService
{
    private readonly ILogger<EmailParserService> _logger;
    public EmailParserService(ILogger<EmailParserService> logger) => _logger = logger;

    public EmailParseResult Parse(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".msg" ? ParseMsg(filePath) : ParseEml(filePath);
    }

    private EmailParseResult ParseEml(string filePath)
    {
        var message = MimeMessage.Load(filePath);

        var sb = new StringBuilder();
        sb.AppendLine($"From: {message.From}");
        sb.AppendLine($"To: {message.To}");
        if (message.Cc.Count > 0) sb.AppendLine($"CC: {message.Cc}");
        sb.AppendLine($"Subject: {message.Subject}");
        sb.AppendLine();

        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.HtmlBody))
            body = StripHtml(message.HtmlBody);
        sb.Append(body ?? "");

        var bodyPath = SaveBodyText(sb.ToString());
        var attachments = new List<EmailAttachmentInfo>();

        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var name = part.FileName ?? $"attachment_{attachments.Count + 1}";
            var attExt = Path.GetExtension(name).ToLowerInvariant();
            var tempPath = Path.Combine(Path.GetTempPath(), $"email_att_{Guid.NewGuid():N}{attExt}");
            using var fs = File.Create(tempPath);
            part.Content.DecodeTo(fs);
            attachments.Add(new EmailAttachmentInfo(name, tempPath, attExt));
            _logger.LogInformation("Extracted EML attachment: {Name}", name);
        }

        return new EmailParseResult { BodyTempFilePath = bodyPath, Attachments = attachments };
    }

    private EmailParseResult ParseMsg(string filePath)
    {
        using var msg = new Storage.Message(filePath);

        var sb = new StringBuilder();
        if (msg.Sender != null)
            sb.AppendLine($"From: {msg.Sender.DisplayName} <{msg.Sender.Email}>");

        var toList = msg.Recipients
            .Where(r => r.Type == RecipientType.To)
            .Select(r => string.IsNullOrEmpty(r.Email) ? r.DisplayName : $"{r.DisplayName} <{r.Email}>");
        sb.AppendLine($"To: {string.Join(", ", toList)}");

        var ccList = msg.Recipients
            .Where(r => r.Type == RecipientType.Cc)
            .Select(r => string.IsNullOrEmpty(r.Email) ? r.DisplayName : $"{r.DisplayName} <{r.Email}>");
        if (ccList.Any()) sb.AppendLine($"CC: {string.Join(", ", ccList)}");

        sb.AppendLine($"Subject: {msg.Subject}");
        sb.AppendLine();

        var body = msg.BodyText;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(msg.BodyHtml))
            body = StripHtml(msg.BodyHtml);
        sb.Append(body ?? "");

        var bodyPath = SaveBodyText(sb.ToString());
        var attachments = new List<EmailAttachmentInfo>();

        foreach (var item in msg.Attachments)
        {
            if (item is Storage.Attachment att && att.Data is { Length: > 0 })
            {
                var name = att.FileName ?? $"attachment_{attachments.Count + 1}";
                var attExt = Path.GetExtension(name).ToLowerInvariant();
                var tempPath = Path.Combine(Path.GetTempPath(), $"email_att_{Guid.NewGuid():N}{attExt}");
                File.WriteAllBytes(tempPath, att.Data);
                attachments.Add(new EmailAttachmentInfo(name, tempPath, attExt));
                _logger.LogInformation("Extracted MSG attachment: {Name}", name);
            }
        }

        return new EmailParseResult { BodyTempFilePath = bodyPath, Attachments = attachments };
    }

    private static string SaveBodyText(string text)
    {
        // Normalize to \n-only line endings before saving.
        // Reason: StringBuilder.AppendLine and MSGReader's RTF→text conversion both produce \r\n
        // on Windows. Azure Language Service counts \r\n as 1 character in its offset output,
        // but .NET string indexing counts it as 2. Every \r\n in the text would cause a 1-char
        // offset drift, making all highlights land in the wrong position. Normalising to \n
        // makes Azure offsets and our string positions identical.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Collapse any run of 2+ newlines to a single newline (no blank lines in the output).
        // EML TextBody preserves every original blank line; emails often have one blank line between
        // each paragraph, so \n{3,} never triggered. MSG RTF→text produces even more. Removing blank
        // lines entirely keeps the preview readable. Safe because highlighting uses text search, not
        // char offsets.
        text = Regex.Replace(text, @"\n{2,}", "\n");

        var path = Path.Combine(Path.GetTempPath(), $"email_body_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text, Encoding.UTF8);
        return path;
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</(script|style)>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|p|div|li|h[1-6]|tr|td|th)[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = html.Replace("\r\n", "\n").Replace('\r', '\n');
        // Trim every line and discard whitespace-only lines — HTML indentation leaves
        // leading spaces/tabs on each line after tag removal.
        return string.Join("\n", html.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}

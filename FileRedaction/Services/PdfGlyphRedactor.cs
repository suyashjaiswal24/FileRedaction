using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PigPage = UglyToad.PdfPig.Content.Page;

namespace FileRedaction.Services;

/// <summary>
/// Removes the glyphs that fall inside redaction boxes straight from a page's content stream, so the page
/// stays vector (small, other text still selectable) while the redacted text no longer exists in the file.
///
/// Deliberately conservative: it only handles pages it can prove it understands (simple single-byte fonts,
/// no images / form XObjects / inline images / ActualText, no rotation) and cross-checks its own glyph count
/// against PdfPig's. For any other page it returns false and the caller rasterises the page instead.
/// </summary>
internal static class PdfGlyphRedactor
{
    internal readonly record struct Rect(double X, double Y, double Width, double Height);

    private enum K { Str, Name, Num, Op, ArrOpen, ArrClose, Other }

    private sealed class Tok
    {
        public K Kind;
        public int Start, End;
        public byte[] Bytes = [];
        public string Text = "";
    }

    /// <summary>
    /// Builds the replacement content stream for <paramref name="srcPage"/>. Boxes are top-left-origin points.
    /// Returns false (with a reason) when the page must be handled by another strategy.
    /// </summary>
    public static bool TryBuildContent(PdfPage srcPage, PigPage pigPage, IReadOnlyList<Rect> boxes,
                                       double padding, out byte[] newContent, out string reason)
    {
        newContent = [];
        reason = "";
        try { return Build(srcPage, pigPage, boxes, padding, out newContent, out reason); }
        catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    private static bool Build(PdfPage srcPage, PigPage pigPage, IReadOnlyList<Rect> boxes,
                              double padding, out byte[] newContent, out string reason)
    {
        newContent = [];

        if (pigPage.Rotation.Value != 0 || srcPage.Rotate != 0) { reason = "rotated page"; return false; }

        var letters = pigPage.Letters;
        if (letters.Count == 0) { reason = "no text layer"; return false; }

        var codeSize = new Dictionary<string, int>();   // font resource name -> bytes per character code
        if (!ResourcesAreSupported(srcPage, codeSize, out reason)) return false;

        var data = ReadContent(srcPage);
        if (data is null || data.Length == 0) { reason = "empty or unreadable content"; return false; }
        if (Encoding.Latin1.GetString(data).Contains("/ActualText", StringComparison.Ordinal))
        { reason = "ActualText present"; return false; }

        // Which letters fall inside a redaction box (letter centre inside the padded box)
        double pageHeight = pigPage.Height;
        var remove = new HashSet<int>();
        for (int i = 0; i < letters.Count; i++)
        {
            var bb = letters[i].BoundingBox;
            double cx = (bb.Left + bb.Right) / 2;
            double cy = pageHeight - (bb.Top + bb.Bottom) / 2;
            foreach (var b in boxes)
            {
                if (cx >= b.X - padding && cx <= b.X + b.Width + padding &&
                    cy >= b.Y - padding && cy <= b.Y + b.Height + padding)
                { remove.Add(i); break; }
            }
        }

        var toks = Tokenize(data);
        var edits = new List<(int Start, int End, string Replacement)>();
        int glyph = 0;
        int size = 0;                       // bytes per code of the current font (0 = none selected yet)
        var sizeStack = new Stack<int>();   // q / Q save and restore the font

        for (int t = 0; t < toks.Count; t++)
        {
            var tok = toks[t];
            if (tok.Kind != K.Op) continue;

            switch (tok.Text)
            {
                case "q": sizeStack.Push(size); break;
                case "Q": if (sizeStack.Count > 0) size = sizeStack.Pop(); break;

                case "Tf":
                    if (t < 2 || toks[t - 2].Kind != K.Name || !codeSize.TryGetValue(toks[t - 2].Text, out size))
                    { reason = "unknown font in Tf"; return false; }
                    break;

                case "BI": reason = "inline image"; return false;
                case "\"": reason = "unsupported operator \""; return false;

                case "Tj":
                case "'":
                {
                    if (t < 1 || toks[t - 1].Kind != K.Str) { reason = "malformed Tj"; return false; }
                    var st = toks[t - 1];
                    var sb = new StringBuilder();
                    bool any = EmitString(st.Bytes, size, ref glyph, remove, letters, sb);
                    if (any)
                        edits.Add((st.Start, tok.End, (tok.Text == "'" ? "T* " : "") + "[" + sb + "] TJ"));
                    break;
                }

                case "TJ":
                {
                    int close = t - 1;
                    if (close < 0 || toks[close].Kind != K.ArrClose) { reason = "malformed TJ"; return false; }
                    int open = close;
                    while (open >= 0 && toks[open].Kind != K.ArrOpen) open--;
                    if (open < 0) { reason = "malformed TJ"; return false; }

                    var sb = new StringBuilder("[");
                    bool any = false;
                    for (int k = open + 1; k < close; k++)
                    {
                        var e = toks[k];
                        if (e.Kind == K.Str) any |= EmitString(e.Bytes, size, ref glyph, remove, letters, sb);
                        else if (e.Kind == K.Num) sb.Append(' ').Append(e.Text).Append(' ');
                        else { reason = "unexpected token in TJ"; return false; }
                    }
                    sb.Append(']');
                    if (any) edits.Add((toks[open].Start, toks[close].End, sb.ToString()));
                    break;
                }
            }
        }

        if (glyph != letters.Count)
        {
            reason = $"glyph count mismatch (stream {glyph} vs text layer {letters.Count})";
            return false;
        }

        using var ms = new MemoryStream(data.Length);
        int pos = 0;
        foreach (var (s, e, rep) in edits)
        {
            ms.Write(data, pos, s - pos);
            var repBytes = Encoding.ASCII.GetBytes(rep);
            ms.Write(repBytes, 0, repBytes.Length);
            pos = e;
        }
        ms.Write(data, pos, data.Length - pos);
        newContent = ms.ToArray();
        reason = "";
        return true;
    }

    // ── String handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Appends the string's kept runs as literals and each removed run as a TJ spacing number that
    /// preserves the removed glyphs' advance (so the text after them does not shift).
    /// Returns true when at least one glyph was removed.
    /// </summary>
    private static bool EmitString(byte[] bytes, int codeSize, ref int glyph, HashSet<int> remove,
                                   IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters, StringBuilder sb)
    {
        if (codeSize <= 0 || bytes.Length % codeSize != 0)
            throw new InvalidOperationException("text shown before a font was selected, or code length mismatch");

        int units = bytes.Length / codeSize;   // number of glyphs in this string
        bool any = false;
        int k = 0;
        while (k < units)
        {
            bool removed = remove.Contains(glyph + k);
            int run = k;
            double advance = 0;
            double pointSize = 0;
            while (run < units && remove.Contains(glyph + run) == removed)
            {
                if (removed && glyph + run < letters.Count)
                {
                    var l = letters[glyph + run];
                    double dx = l.EndBaseLine.X - l.StartBaseLine.X;
                    double dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
                    advance += Math.Sqrt(dx * dx + dy * dy);
                    if (l.PointSize > 0) pointSize = l.PointSize;
                }
                run++;
            }

            if (removed)
            {
                any = true;
                double n = pointSize > 0 ? -advance / pointSize * 1000.0 : 0;
                sb.Append(' ').Append(n.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                AppendLiteral(sb, bytes, k * codeSize, run * codeSize);
            }
            k = run;
        }

        glyph += units;
        return any;
    }

    private static void AppendLiteral(StringBuilder sb, byte[] b, int from, int to)
    {
        sb.Append('(');
        for (int i = from; i < to; i++)
        {
            byte c = b[i];
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append((char)c);
            else if (c < 32 || c > 126) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append((char)c);
        }
        sb.Append(')');
    }

    // ── PDF object checks ────────────────────────────────────────────────────

    private static PdfDictionary? AsDict(PdfItem? item) =>
        item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;

    private static bool ResourcesAreSupported(PdfPage page, Dictionary<string, int> codeSize, out string reason)
    {
        reason = "";
        var res = page.Elements.GetDictionary("/Resources");
        if (res is null) { reason = "no page resources"; return false; }

        var fonts = res.Elements.GetDictionary("/Font");
        if (fonts is not null)
        {
            foreach (var key in fonts.Elements.Keys)
            {
                var fd = AsDict(fonts.Elements[key]);
                var subtype = fd?.Elements.GetName("/Subtype");
                if (subtype is "/Type1" or "/TrueType" or "/MMType1")
                    codeSize[key] = 1;
                else if (subtype == "/Type0")
                {
                    // Only the identity CMap (fixed 2-byte codes, horizontal) is understood.
                    var enc = fd!.Elements.GetName("/Encoding");
                    if (enc != "/Identity-H") { reason = $"unsupported Type0 encoding {enc}"; return false; }
                    codeSize[key] = 2;
                }
                else { reason = $"unsupported font type {subtype}"; return false; }
            }
        }

        var xobjects = res.Elements.GetDictionary("/XObject");
        if (xobjects is not null && xobjects.Elements.Count > 0)
        { reason = "page has images or form XObjects"; return false; }

        return true;
    }

    private static byte[]? ReadContent(PdfPage page)
    {
        using var ms = new MemoryStream();
        foreach (var item in page.Contents.Elements)
        {
            var d = AsDict(item);
            var bytes = d?.Stream?.UnfilteredValue;
            if (bytes is null) return null;
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    // ── Tokenizer (just enough of the content-stream grammar) ────────────────

    private static bool IsWs(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelim(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static List<Tok> Tokenize(byte[] d)
    {
        var toks = new List<Tok>();
        int i = 0, n = d.Length;

        while (i < n)
        {
            byte c = d[i];
            if (IsWs(c)) { i++; continue; }
            if (c == '%') { while (i < n && d[i] != 10 && d[i] != 13) i++; continue; }

            int s = i;

            if (c == '(')
            {
                var buf = new List<byte>();
                int depth = 1;
                i++;
                while (i < n && depth > 0)
                {
                    byte b = d[i];
                    if (b == '\\')
                    {
                        i++;
                        if (i >= n) break;
                        byte e = d[i];
                        switch (e)
                        {
                            case (byte)'n': buf.Add(10); i++; break;
                            case (byte)'r': buf.Add(13); i++; break;
                            case (byte)'t': buf.Add(9); i++; break;
                            case (byte)'b': buf.Add(8); i++; break;
                            case (byte)'f': buf.Add(12); i++; break;
                            case 13: i++; if (i < n && d[i] == 10) i++; break;
                            case 10: i++; break;
                            default:
                                if (e >= '0' && e <= '7')
                                {
                                    int v = 0, cnt = 0;
                                    while (i < n && cnt < 3 && d[i] >= '0' && d[i] <= '7') { v = v * 8 + (d[i] - '0'); i++; cnt++; }
                                    buf.Add((byte)(v & 0xFF));
                                }
                                else { buf.Add(e); i++; }
                                break;
                        }
                    }
                    else if (b == '(') { depth++; buf.Add(b); i++; }
                    else if (b == ')') { depth--; if (depth > 0) buf.Add(b); i++; }
                    else { buf.Add(b); i++; }
                }
                toks.Add(new Tok { Kind = K.Str, Start = s, End = i, Bytes = buf.ToArray() });
                continue;
            }

            if (c == '<')
            {
                if (i + 1 < n && d[i + 1] == '<') { i += 2; toks.Add(new Tok { Kind = K.Other, Start = s, End = i, Text = "<<" }); continue; }
                i++;
                var hex = new List<int>();
                while (i < n && d[i] != '>')
                {
                    int v = HexVal(d[i]);
                    if (v >= 0) hex.Add(v);
                    i++;
                }
                if (i < n) i++;
                if (hex.Count % 2 == 1) hex.Add(0);
                var bytes = new byte[hex.Count / 2];
                for (int h = 0; h < bytes.Length; h++) bytes[h] = (byte)(hex[2 * h] * 16 + hex[2 * h + 1]);
                toks.Add(new Tok { Kind = K.Str, Start = s, End = i, Bytes = bytes });
                continue;
            }

            if (c == '>')
            {
                i += (i + 1 < n && d[i + 1] == '>') ? 2 : 1;
                toks.Add(new Tok { Kind = K.Other, Start = s, End = i, Text = ">>" });
                continue;
            }

            if (c == '[') { i++; toks.Add(new Tok { Kind = K.ArrOpen, Start = s, End = i }); continue; }
            if (c == ']') { i++; toks.Add(new Tok { Kind = K.ArrClose, Start = s, End = i }); continue; }
            if (c == '{' || c == '}' || c == ')') { i++; toks.Add(new Tok { Kind = K.Other, Start = s, End = i }); continue; }

            if (c == '/')
            {
                i++;
                while (i < n && !IsWs(d[i]) && !IsDelim(d[i])) i++;
                toks.Add(new Tok { Kind = K.Name, Start = s, End = i, Text = Encoding.Latin1.GetString(d, s, i - s) });
                continue;
            }

            while (i < n && !IsWs(d[i]) && !IsDelim(d[i])) i++;
            var word = Encoding.Latin1.GetString(d, s, i - s);
            var kind = double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? K.Num : K.Op;
            toks.Add(new Tok { Kind = kind, Start = s, End = i, Text = word });
        }

        return toks;
    }

    private static int HexVal(byte c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}

using System.Globalization;
using Meridian.Api.Features.Reports;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Meridian.Api.Features.Certificates;

/// <summary>What goes on one certificate. CompletedSa is already in South African time.</summary>
public sealed record CertificateData(
    string Name, string QuizTitle, string Category, DateTime CompletedSa, int ScorePercent, int PassMark,
    string Number, string SignatoryName, string SignatoryTitle);

/// <summary>
/// Meridian "Certificate of Completion": A4 landscape, Optimum branding (design concept B, classic frame).
/// Drawn with PDFsharp directly so every element sits at an exact position. Fonts: Trebuchet MS (Optimum's digital
/// font, see MeridianFontResolver.CertificateFamily) with Arial/DejaVu as fallback. Logos are embedded resources.
/// Long names shrink to fit one line; long quiz titles wrap to two lines and shrink if needed.
/// </summary>
public static class CertificatePdf
{
    private const double W = 842, H = 595;   // A4 landscape in points
    private static readonly XColor Green = XColor.FromArgb(0x2F, 0x5D, 0x64);
    private static readonly XColor Deep = XColor.FromArgb(0x15, 0x37, 0x41);
    private static readonly XColor Orange = XColor.FromArgb(0xE4, 0x64, 0x34);
    private static readonly XColor MutedOrange = XColor.FromArgb(0xF8, 0x99, 0x39);
    private static readonly XColor Grey = XColor.FromArgb(0xD7, 0xDD, 0xE0);
    private static readonly XColor Muted = XColor.FromArgb(0x5E, 0x6E, 0x73);

    private static readonly Lazy<byte[]> Logo = new(() => Resource("optimum-logo.png"));
    private static readonly Lazy<byte[]> Watermark = new(() => Resource("optimum-elephant-watermark.png"));

    private static byte[] Resource(string name)
    {
        var asm = typeof(CertificatePdf).Assembly;
        using var s = asm.GetManifestResourceStream("Meridian.Certificates." + name)
            ?? throw new InvalidOperationException($"Certificate image '{name}' is missing from the build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string NumberFor(Guid attemptId, DateTime completedUtc) =>
        $"MER-{completedUtc:yyyy}-{attemptId.ToString("N")[..8].ToUpperInvariant()}";

    public static byte[] Build(CertificateData d)
    {
        var doc = new PdfDocument();
        doc.Info.Title = $"Certificate of Completion - {d.QuizTitle} - {d.Name}";
        doc.Info.Author = "Optimum Financial Services Group";
        doc.Info.Subject = $"Meridian certificate {d.Number}";
        doc.Info.Creator = "Meridian";
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(W);
        page.Height = XUnit.FromPoint(H);
        using (var g = XGraphics.FromPdfPage(page)) Draw(g, d);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    // Coordinates: points from the top-left corner. Text is placed on its baseline.
    private static void Draw(XGraphics g, CertificateData d)
    {
        const double cx = W / 2;
        g.DrawRectangle(XBrushes.White, 0, 0, W, H);

        // Frame: prime green outer line, thin orange inner line, deep green + orange corner wedges.
        g.DrawRectangle(new XPen(Green, 2.2), 22, 22, W - 44, H - 44);
        g.DrawRectangle(new XPen(Orange, 0.7), 30, 30, W - 60, H - 60);
        Triangle(g, Deep, (22, 22), (92, 22), (22, 92));
        Triangle(g, Orange, (22, 22), (68, 22), (22, 68));
        Triangle(g, Deep, (W - 22, H - 22), (W - 92, H - 22), (W - 22, H - 92));
        Triangle(g, Orange, (W - 22, H - 22), (W - 68, H - 22), (W - 22, H - 68));

        // Faint elephant behind the text, and the Optimum logo at the top.
        using (var mark = XImage.FromStream(new MemoryStream(Watermark.Value)))
        {
            const double ew = 330; var eh = ew * mark.PixelHeight / mark.PixelWidth;
            g.DrawImage(mark, cx - ew / 2, H / 2 - eh / 2 + 20, ew, eh);
        }
        using (var logo = XImage.FromStream(new MemoryStream(Logo.Value)))
        {
            const double lw = 170; var lh = lw * logo.PixelHeight / logo.PixelWidth;
            g.DrawImage(logo, cx - lw / 2, 62, lw, lh);
        }

        var family = MeridianFontResolver.CertificateFamily;
        var y = 158.0;
        Spaced(g, "CERTIFICATE OF COMPLETION", Font(family, 24, bold: true), Green, 4.5, cx, y);
        y += 16;
        g.DrawRectangle(new XSolidBrush(Orange), cx - 28, y - 2.5, 56, 2.5);

        y += 30;
        g.DrawString("This is to certify that", Font(family, 12, italic: true), new XSolidBrush(Muted), cx, y, XStringFormats.BaseLineCenter);

        var nameFont = Fit(g, d.Name, family, 34, 18, W - 200);
        y += 44;
        g.DrawString(d.Name, nameFont, new XSolidBrush(Deep), cx, y, XStringFormats.BaseLineCenter);
        y += 12;
        var nameWidth = Math.Min(g.MeasureString(d.Name, nameFont).Width + 40, W - 200);
        g.DrawLine(new XPen(MutedOrange, 1), cx - nameWidth / 2, y, cx + nameWidth / 2, y);

        y += 26;
        g.DrawString("has successfully completed", Font(family, 12), new XSolidBrush(Muted), cx, y, XStringFormats.BaseLineCenter);

        // Quiz title: up to two lines, shrinking from 19pt if it still does not fit.
        var size = 19.0;
        var lines = Wrap(g, d.QuizTitle, Font(family, size, bold: true), W - 240);
        while (lines.Count > 2 && size > 12) { size -= 1; lines = Wrap(g, d.QuizTitle, Font(family, size, bold: true), W - 240); }
        if (lines.Count > 2) lines = [lines[0], string.Join(' ', lines.Skip(1))];
        var titleFont = Font(family, size, bold: true);
        y += 30;
        foreach (var line in lines)
        {
            g.DrawString(line, titleFont, new XSolidBrush(Green), cx, y, XStringFormats.BaseLineCenter);
            y += size * 1.25;
        }
        y -= size * 1.25;
        if (!string.IsNullOrWhiteSpace(d.Category))
            Spaced(g, d.Category.ToUpperInvariant(), Font(family, 8), Muted, 1.8, cx, y + 18);

        // Details row: date, score, certificate number.
        const double detailsY = H - 150, colW = 190;
        var details = new[]
        {
            ("DATE COMPLETED", d.CompletedSa.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
            ("SCORE", $"{d.ScorePercent}%  (pass mark {d.PassMark}%)"),
            ("CERTIFICATE NO.", d.Number)
        };
        for (var i = 0; i < details.Length; i++)
        {
            var x = cx + (i - 1) * colW;
            Spaced(g, details[i].Item1, Font(family, 7, bold: true), Muted, 1.6, x, detailsY - 16);
            g.DrawString(details[i].Item2, Font(family, 11.5, bold: true), new XSolidBrush(Deep), x, detailsY, XStringFormats.BaseLineCenter);
            if (i > 0) g.DrawLine(new XPen(Grey, 0.8), x - colW / 2, detailsY + 6, x - colW / 2, detailsY - 26);
        }

        // Signature (left) and slogan (right).
        const double signY = H - 78, signX = 210;
        g.DrawLine(new XPen(Deep, 0.8), signX - 95, signY - 20, signX + 95, signY - 20);
        g.DrawString(d.SignatoryName, Font(family, 10, bold: true), new XSolidBrush(Deep), signX, signY - 6, XStringFormats.BaseLineCenter);
        g.DrawString(d.SignatoryTitle, Font(family, 8.5), new XSolidBrush(Muted), signX, signY + 6, XStringFormats.BaseLineCenter);
        const double sloganX = W - 210;
        g.DrawString("Where possibility runs deep", Font(family, 9.5, italic: true), new XSolidBrush(Muted), sloganX, signY - 12, XStringFormats.BaseLineCenter);
        g.DrawString("Issued through Meridian · meridian.primebridge.co.za", Font(family, 7.5), new XSolidBrush(Muted), sloganX, signY + 2, XStringFormats.BaseLineCenter);
    }

    private static XFont Font(string family, double size, bool bold = false, bool italic = false) =>
        new(family, size, bold && italic ? XFontStyleEx.BoldItalic : bold ? XFontStyleEx.Bold : italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);

    private static XFont Fit(XGraphics g, string text, string family, double size, double min, double maxWidth)
    {
        var font = Font(family, size, bold: true);
        while (size > min && g.MeasureString(text, font).Width > maxWidth) { size -= 1; font = Font(family, size, bold: true); }
        return font;
    }

    private static List<string> Wrap(XGraphics g, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth) { lines.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    /// <summary>Letter-spaced text centred on cx (PDFsharp has no tracking, so it is drawn one character at a time).</summary>
    private static void Spaced(XGraphics g, string text, XFont font, XColor color, double tracking, double cx, double baseline)
    {
        var widths = text.Select(ch => g.MeasureString(ch.ToString(), font).Width).ToList();
        var x = cx - (widths.Sum() + tracking * (text.Length - 1)) / 2;
        var brush = new XSolidBrush(color);
        for (var i = 0; i < text.Length; i++)
        {
            g.DrawString(text[i].ToString(), font, brush, x, baseline, XStringFormats.BaseLineLeft);
            x += widths[i] + tracking;
        }
    }

    private static void Triangle(XGraphics g, XColor color, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        g.DrawPolygon(new XSolidBrush(color), [new XPoint(a.X, a.Y), new XPoint(b.X, b.Y), new XPoint(c.X, c.Y)], XFillMode.Winding);
}

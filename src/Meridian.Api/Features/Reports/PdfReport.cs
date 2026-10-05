using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Meridian.Api.Features.Reports;

public sealed record StaffRow(string Name, string Department, string JobTitle, int Attempts, int Passed,
    decimal AverageScore, decimal CpdPoints, long Xp, int SurveysCompleted, DateTime? LastActivitySa);
public sealed record QuizRow(string Title, string Category, int Attempts, int Participants, decimal PassRate, decimal AverageScore, decimal CpdAwarded);
public sealed record AttemptRow(string Quiz, string Category, DateTime CompletedSa, decimal Score, bool Passed, decimal Cpd, long Xp);
public sealed record SurveyQuestionSummary(string Question, List<string> Lines);
public sealed record SurveySummary(string Title, int Responses, string Note, List<SurveyQuestionSummary> Questions);

public sealed class ReportData
{
    public required string ScopeLabel { get; init; }
    public required string PeriodLabel { get; init; }
    public required string GeneratedBy { get; init; }
    public required DateTime GeneratedAtSa { get; init; }
    public required bool Personal { get; init; }
    public required bool IncludeQuizzes { get; init; }
    public required bool IncludeSurveys { get; init; }
    public List<(string Label, string Value)> Totals { get; init; } = [];
    public List<StaffRow> Staff { get; init; } = [];
    public List<QuizRow> Quizzes { get; init; } = [];
    public List<AttemptRow> Attempts { get; init; } = [];
    public List<SurveySummary> Surveys { get; init; } = [];
    public List<string> NotRegistered { get; init; } = [];
}

public static class PdfReportBuilder
{
    private static readonly Color Green = new(0x2F, 0x5D, 0x64);
    private static readonly Color DeepGreen = new(0x15, 0x37, 0x41);
    private static readonly Color Orange = new(0xE4, 0x64, 0x34);
    private static readonly Color Stripe = new(0xF2, 0xF5, 0xF5);
    private static readonly Color Muted = new(0x6B, 0x7A, 0x80);

    public static byte[] Build(ReportData d)
    {
        var doc = new Document();
        doc.Info.Title = $"Meridian CPD report - {d.ScopeLabel}";
        doc.Info.Author = "Meridian · PrimeBridge";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = MeridianFontResolver.Family;
        normal.Font.Size = 9;
        var h1 = doc.Styles.AddStyle("MeridianH1", StyleNames.Normal);
        h1.Font.Size = 20; h1.Font.Bold = true; h1.Font.Color = DeepGreen;
        var h2 = doc.Styles.AddStyle("MeridianH2", StyleNames.Normal);
        h2.Font.Size = 13; h2.Font.Bold = true; h2.Font.Color = Green;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(14); h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        h2.ParagraphFormat.KeepWithNext = true;

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Portrait;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7; footer.Format.Font.Color = Muted; footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText($"Meridian · PrimeBridge · {d.ScopeLabel} · {d.PeriodLabel} · Page ");
        footer.AddPageField(); footer.AddText(" of "); footer.AddNumPagesField();

        // Title block
        var brand = section.AddParagraph("MERIDIAN · PRIMEBRIDGE");
        brand.Format.Font.Size = 7; brand.Format.Font.Color = Orange; brand.Format.Font.Bold = true;
        section.AddParagraph(d.Personal ? "Personal CPD report" : "CPD report").Style = "MeridianH1";
        var sub = section.AddParagraph();
        sub.Format.SpaceAfter = Unit.FromPoint(10); sub.Format.Font.Color = Muted;
        sub.AddFormattedText(d.ScopeLabel, TextFormat.Bold);
        sub.AddText($"   ·   {d.PeriodLabel}   ·   Generated {d.GeneratedAtSa:dd MMM yyyy HH:mm} (SAST) by {d.GeneratedBy}");

        if (d.Totals.Count > 0)
        {
            var totals = section.AddTable();
            totals.Borders.Visible = false;
            var width = 17.8 / d.Totals.Count;
            foreach (var _ in d.Totals) totals.AddColumn(Unit.FromCentimeter(width));
            var labels = totals.AddRow(); var values = totals.AddRow();
            labels.Shading.Color = Stripe; values.Shading.Color = Stripe;
            for (var i = 0; i < d.Totals.Count; i++)
            {
                var l = labels.Cells[i].AddParagraph(d.Totals[i].Label.ToUpperInvariant());
                l.Format.Font.Size = 6.5; l.Format.Font.Color = Muted;
                var v = values.Cells[i].AddParagraph(d.Totals[i].Value);
                v.Format.Font.Size = 14; v.Format.Font.Bold = true; v.Format.Font.Color = DeepGreen;
                labels.Cells[i].Format.LeftIndent = Unit.FromPoint(4); values.Cells[i].Format.LeftIndent = Unit.FromPoint(4);
            }
            values.BottomPadding = Unit.FromPoint(6); labels.TopPadding = Unit.FromPoint(6);
        }

        if (d.IncludeQuizzes)
        {
            if (d.Personal)
            {
                Heading(section, "Quiz results");
                if (d.Attempts.Count == 0) Empty(section, "No quizzes were completed in this period.");
                else Table(section, ["Quiz", "Category", "Completed", "Score", "Result", "CPD", "XP"], [5.2, 3.6, 2.8, 1.5, 1.6, 1.4, 1.7],
                    d.Attempts.Select(a => new[] { a.Quiz, a.Category, a.CompletedSa.ToString("dd MMM yyyy"), $"{a.Score:0}%",
                        a.Passed ? "Passed" : "Not passed", $"{a.Cpd:0.##}", $"{a.Xp}" }), rightFrom: 3);
            }
            else
            {
                Heading(section, "Staff summary");
                if (d.Staff.Count == 0) Empty(section, "No registered staff in this scope.");
                else Table(section, ["Name", "Department", "Job title", "Attempts", "Passed", "Avg", "CPD", "XP", "Surveys"],
                    [3.4, 3.4, 3.2, 1.4, 1.2, 1.1, 1.2, 1.3, 1.6],
                    d.Staff.Select(s => new[] { s.Name, ShortDept(s.Department), s.JobTitle, $"{s.Attempts}", $"{s.Passed}",
                        s.Attempts == 0 ? "–" : $"{s.AverageScore:0}%", $"{s.CpdPoints:0.##}", $"{s.Xp}", $"{s.SurveysCompleted}" }), rightFrom: 3);

                Heading(section, "Quiz summary");
                if (d.Quizzes.Count == 0) Empty(section, "No quizzes were completed in this period.");
                else Table(section, ["Quiz", "Category", "Attempts", "People", "Pass rate", "Avg score", "CPD awarded"],
                    [5.0, 3.6, 1.6, 1.5, 1.9, 2.0, 2.2],
                    d.Quizzes.Select(q => new[] { q.Title, q.Category, $"{q.Attempts}", $"{q.Participants}", $"{q.PassRate:0}%",
                        $"{q.AverageScore:0}%", $"{q.CpdAwarded:0.##}" }), rightFrom: 2);
            }
        }

        if (d.IncludeSurveys)
        {
            Heading(section, d.Personal ? "Survey responses" : "Survey results");
            if (d.Surveys.Count == 0) Empty(section, "No surveys in this period.");
            foreach (var s in d.Surveys)
            {
                var title = section.AddParagraph();
                title.Format.SpaceBefore = Unit.FromPoint(8); title.Format.KeepWithNext = true;
                title.AddFormattedText(s.Title, TextFormat.Bold);
                var meta = title.AddFormattedText($"   {s.Note}"); meta.Font.Color = Muted;
                foreach (var q in s.Questions)
                {
                    var qp = section.AddParagraph(q.Question);
                    qp.Format.LeftIndent = Unit.FromCentimeter(0.4); qp.Format.SpaceBefore = Unit.FromPoint(4);
                    qp.Format.Font.Bold = true; qp.Format.Font.Size = 8.5; qp.Format.KeepWithNext = true;
                    foreach (var line in q.Lines)
                    {
                        var lp = section.AddParagraph(line);
                        lp.Format.LeftIndent = Unit.FromCentimeter(0.8); lp.Format.Font.Size = 8.5;
                    }
                }
            }
        }

        if (!d.Personal && d.NotRegistered.Count > 0)
        {
            Heading(section, $"Not yet registered on Meridian ({d.NotRegistered.Count})");
            var p = section.AddParagraph(string.Join(" · ", d.NotRegistered));
            p.Format.Font.Size = 8; p.Format.Font.Color = Muted;
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    private static string ShortDept(string department) =>
        department.Replace(" (Pty) Ltd", "", StringComparison.OrdinalIgnoreCase).Replace("Optimum ", "", StringComparison.OrdinalIgnoreCase);

    private static void Heading(Section section, string text) => section.AddParagraph(text).Style = "MeridianH2";

    private static void Empty(Section section, string text)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Color = Muted; p.Format.Font.Italic = true;
    }

    private static void Table(Section section, string[] headers, double[] widthsCm, IEnumerable<string[]> rows, int rightFrom)
    {
        var table = section.AddTable();
        table.Borders.Color = new Color(0xDD, 0xE3, 0xE5);
        table.Borders.Width = 0.5;
        table.Format.Font.Size = 8;
        table.TopPadding = Unit.FromPoint(2);
        table.BottomPadding = Unit.FromPoint(2);
        for (var i = 0; i < headers.Length; i++)
        {
            var column = table.AddColumn(Unit.FromCentimeter(widthsCm[i]));
            column.Format.Alignment = i >= rightFrom ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Green;
        head.Format.Font.Bold = true;
        head.Format.Font.Color = Colors.White;
        for (var i = 0; i < headers.Length; i++) head.Cells[i].AddParagraph(headers[i]);
        var n = 0;
        foreach (var values in rows)
        {
            var row = table.AddRow();
            if (n++ % 2 == 1) row.Shading.Color = Stripe;
            for (var i = 0; i < headers.Length && i < values.Length; i++) row.Cells[i].AddParagraph(values[i] ?? "");
        }
    }
}

/// <summary>
/// PDFsharp 6 needs a font resolver. Uses Arial on Windows, otherwise DejaVu Sans / Liberation Sans from /usr/share/fonts.
/// Every family name maps to this one font so the PDF always renders.
/// </summary>
public sealed class MeridianFontResolver : IFontResolver
{
    public const string Family = "Meridian Sans";
    private readonly Dictionary<string, string> _files = new();

    public MeridianFontResolver()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string? Find(params string[] names)
        {
            foreach (var name in names)
            {
                if (!string.IsNullOrEmpty(windows) && File.Exists(Path.Combine(windows, name))) return Path.Combine(windows, name);
                foreach (var root in new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts/Supplemental" })
                {
                    if (!Directory.Exists(root)) continue;
                    var hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is not null) return hit;
                }
            }
            return null;
        }
        // Missing fonts must not stop the API starting; GetFont reports it when a PDF is generated.
        var regular = Find("arial.ttf", "Arial.ttf", "DejaVuSans.ttf", "LiberationSans-Regular.ttf") ?? "";
        _files["R"] = regular;
        _files["B"] = Find("arialbd.ttf", "Arial Bold.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf") ?? regular;
        _files["I"] = Find("ariali.ttf", "Arial Italic.ttf", "DejaVuSans-Oblique.ttf", "LiberationSans-Italic.ttf") ?? regular;
        _files["BI"] = Find("arialbi.ttf", "Arial Bold Italic.ttf", "DejaVuSans-BoldOblique.ttf", "LiberationSans-BoldItalic.ttf") ?? _files["B"];
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold && italic ? "BI" : bold ? "B" : italic ? "I" : "R");

    public byte[]? GetFont(string faceName)
    {
        var path = _files.TryGetValue(faceName, out var p) && p.Length > 0 ? p : _files["R"];
        if (path.Length == 0)
            throw new InvalidOperationException("No usable font found for PDF reports. Install Arial, DejaVu Sans or Liberation Sans on this server.");
        return File.ReadAllBytes(path);
    }
}

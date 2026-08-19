using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace Meridian.Api.Features.Reports;

[ApiController]
[Route("api/reports")]
//[Authorize] // comment this out ONLY for local testing, put it back before this touches main
public class ReportController : ControllerBase
{
    private readonly string _connectionString;
    public ReportController(IConfiguration configuration)
    {
        _connectionString =
        configuration.GetConnectionString("MeridianDb")
        ?? throw new InvalidOperationException(
            "Missing 'MeridianDb' connection string.");
    }
    // TEMPORARY: managerId is a query param until the Azure AD identity ->
    // users.id mapping middleware exists (still an open item on the RBAC
    // work). Once that's built, this must come from the authenticated
    // user's claims, not from whatever the caller passes in — otherwise
    // any line manager could pull any other manager's team data.

    [HttpGet("export")]
    public async Task<IActionResult> ExportToExcel (
        [FromQuery] long managerId,
        [FromQuery] ReportCadence cadence = ReportCadence.Quarterly,
        [FromQuery] DateTime? asOf = null)
    {
        var referenceDate = asOf ?? DateTime.UtcNow;
        var (periodStart, periodEnd, priorStart, priorEnd) = ReportPeriodCalculator.Calculate(cadence, referenceDate);

        const string sql = @"
            SELECT
              u.id AS AdvisorId, u.display_name AS AdvisorName, u.Department AS Department,
              COALESCE(cp.points, 0) AS CpdPointsThisPeriod,
              COALESCE(pp.points, 0) AS CpdPointsPriorPeriod,
              COALESCE(cp.points, 0) - COALESCE(pp.points, 0) AS PointsGrowth,
              COALESCE(qa.quizzes_completed, 0) AS QuizzesCompletedThisPeriod,
              COALESCE(qa.quizzes_passed, 0) AS QuizzesPassedThisPeriod
            FROM users u
            LEFT JOIN (SELECT user_id, SUM(points) AS points FROM cpd_ledger_entries WHERE created_at >= @PeriodStart AND created_at < @PeriodEnd GROUP BY user_id) cp ON cp.user_id = u.id
            LEFT JOIN (SELECT user_id, SUM(points) AS points FROM cpd_ledger_entries WHERE created_at >= @PriorPeriodStart AND created_at < @PriorPeriodEnd GROUP BY user_id) pp ON pp.user_id = u.id
            LEFT JOIN (SELECT user_id, COUNT(*) AS quizzes_completed, SUM(CASE WHEN passed = 1 THEN 1 ELSE 0 END) AS quizzes_passed FROM quiz_attempts WHERE completed_at IS NOT NULL AND completed_at >= @PeriodStart AND completed_at < @PeriodEnd GROUP BY user_id) qa ON qa.user_id = u.id
            WHERE u.manager_id = @ManagerId AND u.is_active = 1
            ORDER BY u.display_name;";

        await using var connection = new MySqlConnection(_connectionString);
        var rows = (await connection.QueryAsync<AdvisorReportRow>(sql, new
        {
            ManagerId = managerId, PeriodStart = periodStart, PeriodEnd = periodEnd,
            PriorPeriodStart = priorStart, PriorPeriodEnd = priorEnd
        })).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("CPD Report");

        sheet.Cell(1, 1).Value = "Meridian CPD Report";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).Value = $"Period: {periodStart:yyyy-MM-dd} to {periodEnd.AddDays(-1):yyyy-MM-dd} ({cadence})";

        string[] headers = { "Advisor", "Department", "CPD Points (This Period)", "CPD Points (Prior Perdiod)", "Growth", "Quizzes Completed", "Quizzes Passed" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(4, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2F5D64");
            cell.Style.Font.FontColor = XLColor.White;
        }

        int row = 5;
        foreach (var r in rows)
        {
            sheet.Cell(row, 1).Value = r.AdvisorName;
            sheet.Cell(row, 2).Value = r.Department;
            sheet.Cell(row, 3).Value = r.CpdPointsThisPeriod;
            sheet.Cell(row, 4).Value = r.CpdPointsPriorPeriod;
            sheet.Cell(row, 5).Value = r.PointsGrowth;
            sheet.Cell(row, 6).Value = r.QuizzesCompletedThisPeriod;
            sheet.Cell(row, 7).Value = r.QuizzesPassedThisPeriod;
            if (r.PointsGrowth < 0)
            {
                sheet.Cell(row, 5).Style.Font.FontColor = XLColor.FromHtml("#E46434");
                sheet.Cell(row, 5).Style.Font.Bold = true;
            }
            row++;
        }

        sheet.Cell(row, 1).Value = "Team Total";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        for (int col = 3; col <= 7; col++)
        {
            var colLetter = (char) ('A' + col - 1);
            sheet.Cell(row, col).FormulaA1 = $"=SUM({colLetter}5:{colLetter}{row - 1})";
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var fileName = $"Meridian-CPD-Report-{cadence}-{referenceDate:yyyy-MM-dd}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
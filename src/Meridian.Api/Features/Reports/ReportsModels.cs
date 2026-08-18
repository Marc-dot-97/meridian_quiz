using DocumentFormat.OpenXml.Presentation;

namespace Meridian.Api.Features.Reports;

public enum ReportCadence { Monthly, Quarterly, BiAnnually, Annually }

public record AdvisorReportRow
{
    public long AdvisorId { get; init; }
    public string AdvisorName { get; init; } = "";
    public string Department { get; init; } = "";
    public decimal CpdPointsThisPeriod { get; init; }
    public decimal CpdPointsPriorPeriod { get; init; }
    public decimal PointsGrowth { get; init; }
    public int QuizzesCompletedThisPeriod { get; init; }
    public int QuizzesPassedThisPeriod { get; init; }
}

public static class ReportPeriodCalculator
{
    public static (DateTime PeriodStart, DateTime PeriodEnd, DateTime PriorStart, DateTime PriorEnd) Calculate(ReportCadence cadence, DateTime referenceDate)
    {
        return cadence switch
        {
            ReportCadence.Monthly => Build(new DateTime(referenceDate.Year, referenceDate.Month, 1), m => m.AddMonths(1), m => m.AddMonths(-1)),
            ReportCadence.Quarterly => Build(new DateTime(referenceDate.Year, ((referenceDate.Month - 1) / 3) * 3 + 1, 1), m => m.AddMonths(3), m => m.AddMonths(-3)),
            ReportCadence.BiAnnually => Build(new DateTime(referenceDate.Year, referenceDate.Month <= 6 ? 1 : 7, 1), m => m.AddMonths(6), m => m.AddMonths(-6)),
            ReportCadence.Annually => Build(new DateTime(referenceDate.Year, 1, 1), m => m.AddYears(1), m => m.AddYears(-1)),
            _ => throw new ArgumentOutOfRangeException(nameof(cadence))
        };
    }

    private static (DateTime, DateTime, DateTime, DateTime) Build(DateTime start, Func<DateTime, DateTime> addOne, Func<DateTime, DateTime> subOne)
    {
        var end = addOne(start);
        var priorStart = subOne(start);
        return (start, end, priorStart, start);
    }
}
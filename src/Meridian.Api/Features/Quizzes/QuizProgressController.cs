using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Meridian.Api.Features.Quizzes;
[ApiController, Authorize]
public sealed class QuizProgressController(MeridianDbContext db) : ControllerBase
{
    [HttpGet("api/progress/me")]
    public async Task<ActionResult<UserProgressDto>> Progress()
    {
        var id = ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var rows = await db.QuizAttempts.AsNoTracking().Where(a => a.UserId == id && a.CompletedAt != null).OrderBy(a => a.CompletedAt).ToListAsync();
        int streak = 0, longest = 0;
        foreach (var a in rows) { streak = a.Passed ? streak + 1 : 0; longest = Math.Max(longest, streak); }
        var xp = rows.Sum(a => (int)a.PointsEarned);
        return Ok(new UserProgressDto(xp, Math.Max(1, xp / 100 + 1), rows.Count, rows.Count(a => a.Passed), streak, longest));
    }
    [HttpGet("api/leaderboard")]
    public async Task<IActionResult> Leaderboard(int take = 10, int? year = null, int? month = null)
    {
        if (year.HasValue != month.HasValue || (year.HasValue && (year < 2 || year > 9998 || month < 1 || month > 12)))
            return BadRequest(new { message = "Provide a valid year and month together." });
        var query = db.QuizAttempts.AsNoTracking().Where(a => a.CompletedAt != null && a.User.IsActive == true);
        if (year.HasValue) {
            // Reporting months use South African time (UTC+2).
            var start = new DateTime(year.Value, month!.Value, 1).AddHours(-2);
            var end = new DateTime(year.Value, month.Value, 1).AddMonths(1).AddHours(-2);
            query = query.Where(a => a.CompletedAt >= start && a.CompletedAt < end);
        }
        var rows = await query.GroupBy(a => new { a.UserId, a.User.DisplayName })
            .Select(g => new { g.Key.UserId, g.Key.DisplayName, Xp = g.Sum(a => (long)a.PointsEarned), Score = g.Max(a => a.ScorePercent) })
            .OrderByDescending(a => a.Xp).ThenBy(a => a.DisplayName).Take(year.HasValue ? 10000 : Math.Clamp(take, 1, 100)).ToListAsync();
        if (year.HasValue) return Ok(rows.Select((a, i) => new MonthlyLeaderboardEntryDto(i + 1, a.UserId,
            a.DisplayName, Math.Max(1, (int)a.Xp / 100 + 1), a.Score, (int)a.Xp)).ToList());
        return Ok(rows.Select((a, i) => new LeaderboardEntryDto(i + 1, a.DisplayName, (int)a.Xp,
            Math.Max(1, (int)a.Xp / 100 + 1), (int)a.Score)).ToList());
    }
}

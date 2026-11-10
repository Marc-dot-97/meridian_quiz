using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Features.Assignments;
using Meridian.Api.Features.Reports;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Meridian.Api.Features.Videos;

/// <summary>Where video files live. Default App_Data/quiz-videos (the deploy script never overwrites App_Data).</summary>
public sealed class QuizVideoFiles(string folder)
{
    public string Folder { get; } = folder;
    public string PathOf(string fileName) => System.IO.Path.Combine(Folder, System.IO.Path.GetFileName(fileName));
    public string UploadPath(Guid uploadId) => System.IO.Path.Combine(Folder, "_uploads", uploadId.ToString("N") + ".part");
}

/// <summary>
/// Rules around a quiz's module video:
///  - the quiz can only be started once the video has been watched to the end (once is enough, for good);
///  - when the video has an "unlock after" quiz (Academy 1 before 2, 2 before 3...), that quiz must be passed first;
///  - while an attempt on the quiz is open the video isn't served, so it can't be watched during the quiz.
/// </summary>
public sealed class QuizVideoGate(QuizVideoStore store, MeridianDbContext db)
{
    /// <summary>Attempts without a time limit count as "open" for this long; after that they're treated as abandoned.</summary>
    public static readonly TimeSpan OpenAttemptWindow = TimeSpan.FromHours(3);

    /// <summary>Why this person can't start the quiz yet, or null when they can.</summary>
    public async Task<string?> StartBlockAsync(ulong userId, ulong quizId, CancellationToken ct = default)
    {
        var video = await store.GetVideoAsync(quizId, ct);
        if (video is null) return null;
        if (await LockedByAsync(userId, video, ct) is { } title) return $"Pass \"{title}\" first to unlock this quiz.";
        var view = await store.GetViewAsync(userId, quizId, ct);
        return view?.CompletedAt is null ? "Watch the module video to the end before starting this quiz." : null;
    }

    /// <summary>Title of the quiz that still has to be passed before this video opens, or null when it's open.</summary>
    public async Task<string?> LockedByAsync(ulong userId, QuizVideo video, CancellationToken ct = default)
    {
        if (video.UnlockAfterQuizId is not ulong before) return null;
        var id = (uint)before;
        if (await db.QuizAttempts.AnyAsync(a => a.UserId == userId && a.QuizId == id && a.Passed && a.CompletedAt != null, ct)) return null;
        return await db.Quizzes.Where(q => q.Id == id).Select(q => q.Title).SingleOrDefaultAsync(ct) ?? "the previous quiz";
    }

    /// <summary>
    /// True while the person's latest attempt on this quiz is unfinished (within its time limit, or 3 hours without one).
    /// Attempts can't be resumed, so an older unfinished attempt is always abandoned and never counts.
    /// </summary>
    public async Task<bool> InQuizAsync(ulong userId, ulong quizId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var id = (uint)quizId;
        var latest = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.QuizId == id)
            .OrderByDescending(a => a.StartedAt)
            .Select(a => new { a.StartedAt, a.CompletedAt, a.SnapshotTimeLimitMinutes }).FirstOrDefaultAsync(ct);
        if (latest is null || latest.CompletedAt is not null) return false;
        var started = DateTime.SpecifyKind(latest.StartedAt, DateTimeKind.Utc);
        var window = latest.SnapshotTimeLimitMinutes is ushort limit ? TimeSpan.FromMinutes(limit) : OpenAttemptWindow;
        return started + window > now;
    }
}

/// <summary>
/// "Watch first" module video of a quiz.
///  GET    api/quizzes/{id}/video           status for the signed-in person (+ whether they may manage the video)
///  GET    api/quizzes/{id}/video/file      the MP4 (range requests, so the browser can stream and resume); not while
///                                          the quiz is in progress, not before the "unlock after" quiz is passed,
///                                          and only to the page's video player (not opened in a tab to save)
///  POST   api/quizzes/{id}/video/progress  furthest point watched; the server limits jumps to real elapsed time,
///                                          so the end can't be reached faster than the video actually plays
///  POST   api/quizzes/{id}/video/upload    chunked upload (8 MB pieces, stays under IIS/Kestrel body limits)
///  PUT    api/quizzes/{id}/video/unlock-after  which quiz must be passed before this video opens (managers)
///  DELETE api/quizzes/{id}/video           remove the video (the quiz no longer needs a watch)
/// Managing = SuperAdmin, HR and line managers (the people who may create quizzes).
/// </summary>
[ApiController, Authorize, Route("api/quizzes/{quizId:long}/video")]
public sealed class QuizVideosController(MeridianDbContext db, QuizVideoStore store, QuizVideoFiles files,
    AssignmentService assignments, AssignmentStore assignmentStore, ReportAccessService access, QuizVideoGate gate,
    ILogger<QuizVideosController> log) : ControllerBase
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    private const long MaxVideoBytes = 1024L * 1024 * 1024;   // 1 GB
    private ulong UserId => ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<QuizVideoDto>> Get(ulong quizId, CancellationToken ct)
    {
        if (!await CanSeeQuizAsync(quizId, ct)) return NotFound(new { message = "Quiz not found." });
        return await StatusAsync(quizId, ct);
    }

    [HttpGet("file")]
    public async Task<IActionResult> GetFile(ulong quizId, CancellationToken ct)
    {
        if (!await CanSeeQuizAsync(quizId, ct)) return NotFound();
        var video = await store.GetVideoAsync(quizId, ct);
        var path = video is null ? null : files.PathOf(video.FileName);
        if (path is null || !System.IO.File.Exists(path)) return NotFound();
        // Only for the page's <video> player: opening the address directly in a tab (to save it) is refused.
        var dest = Request.Headers["Sec-Fetch-Dest"].ToString();
        if (dest is "document" or "iframe" or "embed" or "object") return StatusCode(403);
        if (await gate.InQuizAsync(UserId, quizId, ct)) return StatusCode(403);
        if (await gate.LockedByAsync(UserId, video!, ct) is not null && !await CanManageAsync(ct)) return StatusCode(403);
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }

    [HttpPost("progress")]
    public async Task<ActionResult<VideoProgressResponse>> Progress(ulong quizId, VideoProgressRequest request, CancellationToken ct)
    {
        if (!await CanSeeQuizAsync(quizId, ct)) return NotFound(new { message = "Quiz not found." });
        var video = await store.GetVideoAsync(quizId, ct);
        if (video is null) return NotFound(new { message = "This quiz has no video." });

        var now = DateTime.UtcNow;
        var view = await store.GetViewAsync(UserId, quizId, ct);
        if (view?.CompletedAt is not null) return new VideoProgressResponse(view.MaxPosition, true);
        if (await gate.LockedByAsync(UserId, video, ct) is { } title) return BadRequest(new { message = $"Pass \"{title}\" first." });
        if (await gate.InQuizAsync(UserId, quizId, ct)) return BadRequest(new { message = "Finish the quiz first." });

        // Never further than the real time that has passed since the last report (plus a little slack).
        var max = view?.MaxPosition ?? 0m;
        var elapsed = view is null ? 15m : (decimal)Math.Min((now - view.LastUpdate).TotalSeconds, 24 * 3600);
        var reported = Math.Max(0m, request.Position);
        var newMax = Math.Max(max, Math.Min(reported, max + elapsed * 1.1m + 5m));

        var duration = video.DurationSeconds ?? (request.Duration > 10 ? request.Duration : 0m);
        if (video.DurationSeconds is null && request.Ended && request.Duration > 10)
            await store.SetDurationIfMissingAsync(quizId, decimal.Round(request.Duration, 2), ct);
        var completed = duration > 0 && newMax >= duration - 3m && (request.Ended || newMax >= duration - 1.5m);

        await store.SaveViewAsync(new QuizVideoView(UserId, quizId, decimal.Round(newMax, 2), completed ? now : null, now), ct);
        if (completed) log.LogInformation("User {User} finished the video of quiz {Quiz}", UserId, quizId);
        return new VideoProgressResponse(decimal.Round(newMax, 2), completed);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(ChunkBytes + 64 * 1024)]
    public async Task<ActionResult<QuizVideoDto>> Upload(ulong quizId, [FromQuery] Guid uploadId, [FromQuery] int index,
        [FromQuery] int total, [FromQuery] string? name, CancellationToken ct)
    {
        if (!await CanManageAsync(ct)) return StatusCode(403, new { message = "Only line managers, HR and administrators can add quiz videos." });
        if (!await db.Quizzes.AnyAsync(q => q.Id == quizId && q.IsActive == true, ct)) return NotFound(new { message = "Quiz not found." });
        if (uploadId == Guid.Empty || total < 1 || total > MaxVideoBytes / ChunkBytes + 1 || index < 0 || index >= total)
            return BadRequest(new { message = "Invalid upload piece." });

        Directory.CreateDirectory(System.IO.Path.Combine(files.Folder, "_uploads"));
        var part = files.UploadPath(uploadId);
        if (index == 0 && System.IO.File.Exists(part)) System.IO.File.Delete(part);
        if (index > 0 && !System.IO.File.Exists(part)) return BadRequest(new { message = "Upload expired. Start again." });
        await using (var output = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            if (index > 0 && output.Length != (long)index * ChunkBytes)
                return BadRequest(new { message = "Upload pieces arrived out of order. Start again." });
            await Request.Body.CopyToAsync(output, ct);
            if (output.Length > MaxVideoBytes) { output.Close(); System.IO.File.Delete(part); return BadRequest(new { message = "Videos must be 1 GB or smaller." }); }
        }
        if (index < total - 1) return Accepted();

        // Last piece: check it really is an MP4 ("ftyp" box at byte 4), then swap it in.
        var header = new byte[12];
        await using (var check = System.IO.File.OpenRead(part)) { _ = await check.ReadAsync(header, ct); }
        if (System.Text.Encoding.ASCII.GetString(header, 4, 4) != "ftyp")
        {
            System.IO.File.Delete(part);
            return BadRequest(new { message = "That file is not an MP4 video. Export the video as MP4 (H.264) and try again." });
        }
        var fileName = $"quiz-{quizId}-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
        System.IO.File.Move(part, files.PathOf(fileName));
        var old = await store.GetVideoAsync(quizId, ct);
        var size = new FileInfo(files.PathOf(fileName)).Length;
        var original = string.IsNullOrWhiteSpace(name) ? fileName : System.IO.Path.GetFileName(name.Trim());
        await store.SaveVideoAsync(new QuizVideo(quizId, fileName, original.Length > 255 ? original[..255] : original, size, null, UserId, DateTime.UtcNow), ct);
        if (old is not null && old.FileName != fileName) TryDelete(files.PathOf(old.FileName));
        log.LogInformation("User {User} attached video {File} ({Size} bytes) to quiz {Quiz}", UserId, fileName, size, quizId);
        return await StatusAsync(quizId, ct);
    }

    [HttpPut("unlock-after")]
    public async Task<ActionResult<QuizVideoDto>> UnlockAfter(ulong quizId, VideoUnlockRequest request, CancellationToken ct)
    {
        if (!await CanManageAsync(ct)) return StatusCode(403, new { message = "Only line managers, HR and administrators can change quiz videos." });
        if (await store.GetVideoAsync(quizId, ct) is null) return NotFound(new { message = "Add the video first." });
        if (request.UnlockAfterQuizId is ulong before)
        {
            if (before == quizId) return BadRequest(new { message = "A quiz can't unlock itself." });
            if (!await db.Quizzes.AnyAsync(q => q.Id == before, ct)) return BadRequest(new { message = "That quiz doesn't exist." });
            // No loops (1 → 2 → 1): walk back along the chain from the chosen quiz.
            var seen = new HashSet<ulong> { quizId };
            for (ulong? at = before; at is ulong current; at = (await store.GetVideoAsync(current, ct))?.UnlockAfterQuizId)
                if (!seen.Add(current)) return BadRequest(new { message = "That order would go round in a circle." });
        }
        await store.SetUnlockAfterAsync(quizId, request.UnlockAfterQuizId, ct);
        log.LogInformation("User {User} set quiz {Quiz} video to unlock after quiz {Before}", UserId, quizId, request.UnlockAfterQuizId);
        return await StatusAsync(quizId, ct);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(ulong quizId, CancellationToken ct)
    {
        if (!await CanManageAsync(ct)) return StatusCode(403, new { message = "Only line managers, HR and administrators can remove quiz videos." });
        var video = await store.GetVideoAsync(quizId, ct);
        if (video is null) return NoContent();
        await store.DeleteVideoAsync(quizId, ct);
        TryDelete(files.PathOf(video.FileName));
        log.LogInformation("User {User} removed the video of quiz {Quiz}", UserId, quizId);
        return NoContent();
    }

    private async Task<QuizVideoDto> StatusAsync(ulong quizId, CancellationToken ct)
    {
        var video = await store.GetVideoAsync(quizId, ct);
        var canManage = await CanManageAsync(ct);
        if (video is null) return new QuizVideoDto(false, false, 0, null, canManage, null, null, null, ChunkBytes);
        var view = await store.GetViewAsync(UserId, quizId, ct);
        var lockedBy = await gate.LockedByAsync(UserId, video, ct);
        string? unlockTitle = null;
        if (video.UnlockAfterQuizId is ulong before)
        {
            var id = (uint)before;
            unlockTitle = await db.Quizzes.AsNoTracking().Where(q => q.Id == id).Select(q => q.Title).SingleOrDefaultAsync(ct);
        }
        return new QuizVideoDto(true, view?.CompletedAt is not null, view?.MaxPosition ?? 0, video.DurationSeconds, canManage,
            video.OriginalName, video.SizeBytes, video.UploadedAt, ChunkBytes,
            video.UnlockAfterQuizId, unlockTitle, lockedBy is not null, await gate.InQuizAsync(UserId, quizId, ct));
    }

    private async Task<bool> CanManageAsync(CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == UserId, ct);
        return me is not null && await access.CanCreateQuizzesAsync(me, ct);
    }

    /// <summary>Same visibility as GET api/quizzes/{id}: active, and restricted quizzes only for their departments.</summary>
    private async Task<bool> CanSeeQuizAsync(ulong quizId, CancellationToken ct)
    {
        if (!await db.Quizzes.AnyAsync(q => q.Id == quizId && q.IsActive == true, ct)) return false;
        var a = await assignmentStore.GetAsync(AssignmentKinds.Quiz, quizId.ToString(), ct);
        if (!a.AssignedOnly) return true;
        var viewer = await assignments.ViewerAsync(User, ct);
        return viewer is not null && a.IsVisibleTo(viewer.Departments, viewer.SeesEverything);
    }

    private void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException ex) { log.LogWarning(ex, "Could not delete old video file {Path}", path); }
    }
}

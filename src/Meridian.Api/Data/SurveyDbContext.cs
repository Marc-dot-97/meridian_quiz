using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Data;

public sealed class SurveyRecord
{
    public DateTime? DeleteAfter { get; set; }
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Who created the survey (null for surveys created before this was recorded). Staff see and manage their own surveys.</summary>
    public ulong? CreatedByUserId { get; set; }
}
public sealed class SurveySetting
{
    public byte Id { get; set; }
    public Guid? ActiveSurveyId { get; set; }
}
public sealed class SurveyResponseRecord
{
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public Guid AttemptId { get; set; }
    public ulong UserId { get; set; }
    public string AnswersJson { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
}

/// <summary>
/// WHO took part (one row per user per survey) plus that user's PRIVATE copy of their own answers,
/// read only by their personal report. Manager/HR reports never read AnswersJson from here.
/// </summary>
public sealed class SurveyCompletionRecord
{
    /// <summary>When this completion was copied into survey_anonymous_answers (null = not yet).</summary>
    public DateTime? AnonymisedAt { get; set; }
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public ulong UserId { get; set; }
    public string AnswersJson { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
}

/// <summary>
/// WHAT was answered, with no user ID and no exact time (day only), under a random ID,
/// so a row cannot be linked back to a person. Departments ("A|B") allow department-scoped results.
/// Manager/HR survey results are built ONLY from this table.
/// </summary>
public sealed class SurveyAnonymousAnswer
{
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public string Departments { get; set; } = "";
    public string AnswersJson { get; set; } = "";
    public DateTime SubmittedOn { get; set; }
}

public partial class MeridianDbContext
{
    public DbSet<SurveyAnonymousAnswer> SurveyAnonymousAnswers => Set<SurveyAnonymousAnswer>();
    public DbSet<SurveyCompletionRecord> SurveyCompletions => Set<SurveyCompletionRecord>();
    public DbSet<SurveyRecord> Surveys => Set<SurveyRecord>();
    public DbSet<SurveySetting> SurveySettings => Set<SurveySetting>();
    public DbSet<SurveyResponseRecord> SurveyResponses => Set<SurveyResponseRecord>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SurveyCompletionRecord>(entity =>
        {
            entity.ToTable("survey_completions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasColumnType("char(36)");
            entity.Property(x => x.SurveyId).HasColumnName("survey_id").HasColumnType("char(36)");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.AnswersJson).HasColumnName("answers_json").HasColumnType("longtext");
            entity.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("datetime(6)");
            entity.Property(x => x.AnonymisedAt).HasColumnName("anonymised_at").HasColumnType("datetime(6)");
            entity.HasIndex(x => new { x.SurveyId, x.UserId }).IsUnique().HasDatabaseName("uq_survey_completion_user");
            entity.HasOne<SurveyRecord>().WithMany().HasForeignKey(x => x.SurveyId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SurveyAnonymousAnswer>(entity =>
        {
            entity.ToTable("survey_anonymous_answers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasColumnType("char(36)").ValueGeneratedNever();
            entity.Property(x => x.SurveyId).HasColumnName("survey_id").HasColumnType("char(36)");
            entity.Property(x => x.Departments).HasColumnName("departments").HasMaxLength(1000);
            entity.Property(x => x.AnswersJson).HasColumnName("answers_json").HasColumnType("longtext");
            entity.Property(x => x.SubmittedOn).HasColumnName("submitted_on").HasColumnType("date");
            entity.HasIndex(x => x.SurveyId).HasDatabaseName("ix_survey_anonymous_answers_survey");
        });
        modelBuilder.Entity<SurveyRecord>(entity =>
        {
            entity.ToTable("surveys");
            entity.Property(x => x.DeleteAfter).HasColumnName("delete_after").HasColumnType("datetime(6)");
            entity.HasIndex(x => x.DeleteAfter).HasDatabaseName("ix_surveys_delete_after");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasColumnType("char(36)");
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(x => x.DefinitionJson).HasColumnName("definition_json").HasColumnType("longtext");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
            entity.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        });
        modelBuilder.Entity<SurveySetting>(entity =>
        {
            entity.ToTable("survey_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.ActiveSurveyId).HasColumnName("active_survey_id").HasColumnType("char(36)");
            entity.HasOne<SurveyRecord>().WithMany().HasForeignKey(x => x.ActiveSurveyId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SurveyResponseRecord>(entity =>
        {
            entity.ToTable("survey_responses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").HasColumnType("char(36)");
            entity.Property(x => x.SurveyId).HasColumnName("survey_id").HasColumnType("char(36)");
            entity.Property(x => x.AttemptId).HasColumnName("attempt_id").HasColumnType("char(36)");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.AnswersJson).HasColumnName("answers_json").HasColumnType("longtext");
            entity.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("datetime(6)");
            entity.HasIndex(x => new { x.AttemptId, x.UserId }).IsUnique().HasDatabaseName("uq_survey_response_attempt_user");
            entity.HasOne<SurveyRecord>().WithMany().HasForeignKey(x => x.SurveyId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

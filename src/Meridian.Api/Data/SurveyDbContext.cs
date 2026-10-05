using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Data;

public sealed class SurveyRecord
{
    public DateTime? DeleteAfter { get; set; }
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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

public sealed class SurveyCompletionRecord
{
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public ulong UserId { get; set; }
    public string AnswersJson { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
}

public partial class MeridianDbContext
{
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
            entity.HasIndex(x => new { x.SurveyId, x.UserId }).IsUnique().HasDatabaseName("uq_survey_completion_user");
            entity.HasOne<SurveyRecord>().WithMany().HasForeignKey(x => x.SurveyId).OnDelete(DeleteBehavior.Restrict);
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

using System;
using System.Collections.Generic;
using Meridian.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Data;

public partial class MeridianDbContext : DbContext
{
    public MeridianDbContext(DbContextOptions<MeridianDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AnswerOption> AnswerOptions { get; set; }

    public virtual DbSet<CpdLedgerEntry> CpdLedgerEntries { get; set; }

    public virtual DbSet<None> Nones { get; set; }

    public virtual DbSet<Question> Questions { get; set; }

    public virtual DbSet<Quiz> Quizzes { get; set; }

    public virtual DbSet<QuizAttempt> QuizAttempts { get; set; }

    public virtual DbSet<QuizCategory> QuizCategories { get; set; }

    public virtual DbSet<QuizQuestion> QuizQuestions { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserProgress> UserProgresses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnswerOption>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("answer_options");

            entity.HasIndex(e => new { e.QuestionId, e.DisplayOrder }, "uq_answer_display_order").IsUnique();

            entity.HasIndex(e => new { e.Id, e.QuestionId }, "uq_answer_option_question").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsCorrect).HasColumnName("is_correct");
            entity.Property(e => e.OptionText)
                .HasColumnType("text")
                .HasColumnName("option_text");
            entity.Property(e => e.QuestionId).HasColumnName("question_id");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Question).WithMany(p => p.AnswerOptions)
                .HasForeignKey(d => d.QuestionId)
                .HasConstraintName("fk_answer_options_question");
        });

        modelBuilder.Entity<CpdLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("cpd_ledger_entries");

            entity.HasIndex(e => e.QuizId, "fk_cpd_ledger_quiz");

            entity.HasIndex(e => new { e.UserId, e.CreatedAt }, "ix_cpd_ledger_user_date");

            entity.HasIndex(e => e.AttemptId, "uq_cpd_ledger_attempt").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AttemptId).HasColumnName("attempt_id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasMaxLength(255)
                .HasColumnName("description");
            entity.Property(e => e.Points)
                .HasPrecision(8)
                .HasColumnName("points");
            entity.Property(e => e.QuizId).HasColumnName("quiz_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.Attempt).WithOne(p => p.CpdLedgerEntry)
                .HasForeignKey<CpdLedgerEntry>(d => d.AttemptId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cpd_ledger_attempt");

            entity.HasOne(d => d.Quiz).WithMany(p => p.CpdLedgerEntries)
                .HasForeignKey(d => d.QuizId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cpd_ledger_quiz");

            entity.HasOne(d => d.User).WithMany(p => p.CpdLedgerEntries)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cpd_ledger_user");
        });

        modelBuilder.Entity<None>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("none");

            entity.HasIndex(e => e.Code, "uq_achievements_code").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(100)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasColumnType("text")
                .HasColumnName("description");
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValueSql("'1'")
                .HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(150)
                .HasColumnName("name");
            entity.Property(e => e.XpReward).HasColumnName("xp_reward");
        });

        modelBuilder.Entity<Question>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("questions");

            entity.HasIndex(e => e.ApprovedByUserId, "fk_questions_approved_by");

            entity.HasIndex(e => e.CategoryId, "fk_questions_category");

            entity.HasIndex(e => e.CreatedByUserId, "fk_questions_created_by");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ApprovedAt)
                .HasMaxLength(6)
                .HasColumnName("approved_at");
            entity.Property(e => e.ApprovedByUserId).HasColumnName("approved_by_user_id");
            entity.Property(e => e.CategoryId).HasColumnName("category_id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(e => e.Difficulty)
                .HasDefaultValueSql("'1'")
                .HasColumnName("difficulty");
            entity.Property(e => e.GenerationMetadata)
                .HasColumnType("json")
                .HasColumnName("generation_metadata");
            entity.Property(e => e.QuestionText)
                .HasColumnType("text")
                .HasColumnName("question_text");
            entity.Property(e => e.SourceType)
                .HasDefaultValueSql("'1'")
                .HasColumnName("source_type");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'1'")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.ApprovedByUser).WithMany(p => p.QuestionApprovedByUsers)
                .HasForeignKey(d => d.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_questions_approved_by");

            entity.HasOne(d => d.Category).WithMany(p => p.Questions)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_questions_category");

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.QuestionCreatedByUsers)
                .HasForeignKey(d => d.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_questions_created_by");
        });

        modelBuilder.Entity<Quiz>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("quizzes");

            entity.HasIndex(e => e.CreatedByUserId, "fk_quizzes_created_by");

            entity.HasIndex(e => new { e.CategoryId, e.Title }, "uq_quizzes_category_title").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CategoryId).HasColumnName("category_id");
            entity.Property(e => e.CpdPoints)
                .HasPrecision(8)
                .HasColumnName("cpd_points");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(e => e.Description)
                .HasColumnType("text")
                .HasColumnName("description");
            entity.Property(e => e.Instructions)
                .HasColumnType("text")
                .HasColumnName("instructions");
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValueSql("'1'")
                .HasColumnName("is_active");
            entity.Property(e => e.PassMarkPercent)
                .HasDefaultValueSql("'70'")
                .HasColumnName("pass_mark_percent");
            entity.Property(e => e.QuestionsPerAttempt)
                .HasDefaultValueSql("'10'")
                .HasColumnName("questions_per_attempt");
            entity.Property(e => e.TimeLimitMinutes).HasColumnName("time_limit_minutes");
            entity.Property(e => e.Title)
                .HasMaxLength(200)
                .HasColumnName("title");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Category).WithMany(p => p.Quizzes)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_quizzes_category");

            entity.HasOne(d => d.CreatedByUser).WithMany(p => p.Quizzes)
                .HasForeignKey(d => d.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_quizzes_created_by");
        });

        modelBuilder.Entity<QuizAttempt>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("quiz_attempts");

            entity.HasIndex(e => e.QuizId, "fk_quiz_attempts_quiz");

            entity.HasIndex(e => e.CompletedAt, "ix_attempt_completed");

            entity.HasIndex(e => e.ScorePercent, "ix_attempt_score");

            entity.HasIndex(e => new { e.UserId, e.QuizId }, "ix_attempt_user_quiz");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CompletedAt)
                .HasMaxLength(6)
                .HasColumnName("completed_at");
            entity.Property(e => e.CorrectAnswers).HasColumnName("correct_answers");
            entity.Property(e => e.CpdPointsEarned)
                .HasPrecision(8)
                .HasColumnName("cpd_points_earned");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.Passed).HasColumnName("passed");
            entity.Property(e => e.PointsEarned).HasColumnName("points_earned");
            entity.Property(e => e.QuizId).HasColumnName("quiz_id");
            entity.Property(e => e.ScorePercent)
                .HasPrecision(5)
                .HasColumnName("score_percent");
            entity.Property(e => e.StartedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'1'")
                .HasColumnName("status");
            entity.Property(e => e.TotalQuestions).HasColumnName("total_questions");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.Quiz).WithMany(p => p.QuizAttempts)
                .HasForeignKey(d => d.QuizId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_quiz_attempts_quiz");

            entity.HasOne(d => d.User).WithMany(p => p.QuizAttempts)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_quiz_attempts_user");
        });

        modelBuilder.Entity<QuizCategory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("quiz_categories");

            entity.HasIndex(e => e.Name, "uq_quiz_categories_name").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasColumnType("text")
                .HasColumnName("description");
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValueSql("'1'")
                .HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(150)
                .HasColumnName("name");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<QuizQuestion>(entity =>
        {
            entity.HasKey(e => new { e.QuizId, e.QuestionId }).HasName("PRIMARY");

            entity.ToTable("quiz_questions");

            entity.HasIndex(e => e.QuestionId, "fk_quiz_questions_question");

            entity.Property(e => e.QuizId).HasColumnName("quiz_id");
            entity.Property(e => e.QuestionId).HasColumnName("question_id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValueSql("'1'")
                .HasColumnName("is_active");
            entity.Property(e => e.QuestionWeight)
                .HasPrecision(5)
                .HasDefaultValueSql("'1.00'")
                .HasColumnName("question_weight");

            entity.HasOne(d => d.Question).WithMany(p => p.QuizQuestions)
                .HasForeignKey(d => d.QuestionId)
                .HasConstraintName("fk_quiz_questions_question");

            entity.HasOne(d => d.Quiz).WithMany(p => p.QuizQuestions)
                .HasForeignKey(d => d.QuizId)
                .HasConstraintName("fk_quiz_questions_quiz");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("users");

            entity.HasIndex(e => new { e.UserName, e.Administrators }, "uq_users_auth").IsUnique();

            entity.HasIndex(e => e.Email, "uq_users_email").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasMaxLength(6)
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("created_at");
            entity.Property(e => e.Department).HasMaxLength(100);
            entity.Property(e => e.DisplayName)
                .HasMaxLength(150)
                .HasColumnName("display_name");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.FirstName)
                .HasMaxLength(100)
                .HasColumnName("First_Name");
            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValueSql("'1'")
                .HasColumnName("is_active");
            entity.Property(e => e.LastName)
                .HasMaxLength(100)
                .HasColumnName("Last_Name");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserName)
                .HasMaxLength(50)
                .HasDefaultValueSql("'local'");
            entity.Property(e => e.UserRole)
                .HasMaxLength(30)
                .HasDefaultValueSql("'Advisor'")
                .HasColumnName("user_role");
        });

        modelBuilder.Entity<UserProgress>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("PRIMARY");

            entity.ToTable("user_progress");

            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.LastActivityAt)
                .HasMaxLength(6)
                .HasColumnName("last_activity_at");
            entity.Property(e => e.QuizzesCompleted).HasColumnName("quizzes_completed");
            entity.Property(e => e.QuizzesPassed).HasColumnName("quizzes_passed");
            entity.Property(e => e.TotalPoints).HasColumnName("total_Points");
            entity.Property(e => e.UpdatedAt)
                .HasMaxLength(6)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("'CURRENT_TIMESTAMP(6)'")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.User).WithOne(p => p.UserProgress)
                .HasForeignKey<UserProgress>(d => d.UserId)
                .HasConstraintName("fk_user_progress_user");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);

   
}

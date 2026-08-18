using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class Question
{
    public uint Id { get; set; }

    public uint CategoryId { get; set; }

    public string QuestionText { get; set; } = null!;

    public byte Difficulty { get; set; }

    public byte Status { get; set; }

    public byte SourceType { get; set; }

    public string? GenerationMetadata { get; set; }

    public ulong? CreatedByUserId { get; set; }

    public ulong? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<AnswerOption> AnswerOptions { get; set; } = new List<AnswerOption>();

    public virtual User? ApprovedByUser { get; set; }

    public virtual QuizCategory Category { get; set; } = null!;

    public virtual User? CreatedByUser { get; set; }

    public virtual ICollection<QuizQuestion> QuizQuestions { get; set; } = new List<QuizQuestion>();
}

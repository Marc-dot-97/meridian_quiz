using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class QuizQuestion
{
    public uint QuizId { get; set; }

    public uint QuestionId { get; set; }

    public decimal QuestionWeight { get; set; }

    public bool? IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Question Question { get; set; } = null!;

    public virtual Quiz Quiz { get; set; } = null!;
}

using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class AnswerOption
{
    public uint Id { get; set; }

    public uint QuestionId { get; set; }

    public string OptionText { get; set; } = null!;

    public bool IsCorrect { get; set; }

    public ushort DisplayOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Question Question { get; set; } = null!;
}

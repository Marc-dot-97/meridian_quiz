using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class UserProgress
{
    public ulong UserId { get; set; }

    public uint TotalPoints { get; set; }

    public uint QuizzesCompleted { get; set; }

    public uint QuizzesPassed { get; set; }

    public DateTime? LastActivityAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}

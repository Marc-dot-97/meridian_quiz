using System;
using System.Collections.Generic;
using System.Text;

namespace Meridian.Shared.DTOs;

public sealed record MonthlyLeaderboardEntryDto(
    int Rank,
    ulong UserId,
    string DisplayName,
    int Level,
    decimal BestScorePercent,
    int MonthlyXp);


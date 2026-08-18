using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class None
{
    public uint Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public uint XpReward { get; set; }

    public bool? IsActive { get; set; }

    public DateTime CreatedAt { get; set; }
}

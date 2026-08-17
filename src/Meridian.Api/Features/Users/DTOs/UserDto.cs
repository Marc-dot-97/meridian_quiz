namespace Meridian.Api.Features.Users.Dtos;

public sealed class UserDto
{
    public ulong Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string UserRole { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string Department { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string? LastName { get; set; }

    public DateTime CreatedAt { get; set; }
}
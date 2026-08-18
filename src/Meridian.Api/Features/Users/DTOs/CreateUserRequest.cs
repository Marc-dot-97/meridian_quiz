namespace Meridian.Api.Features.Users.DTOs;

public sealed class CreateUserRequest
{

    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;

    public required string LastName { get; set; }

    public string Department { get; set; } = string.Empty;

    public string UserRole { get; set; } = string.Empty;

}

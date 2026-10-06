using System.ComponentModel.DataAnnotations;
namespace Meridian.Shared.DTOs;
public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 8)] public string Password { get; set; } = "";
}
public sealed class RegisterAccountRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 8)] public string Password { get; set; } = "";
    [Required, StringLength(70)] public string FirstName { get; set; } = "";
    [Required, StringLength(70)] public string LastName { get; set; } = "";
    [Required, StringLength(100)] public string Department { get; set; } = "";
    [Required, StringLength(100)] public string JobTitle { get; set; } = "";
    [StringLength(150)] public string LineManager { get; set; } = "";
}
public sealed record AccountDto(ulong Id, string Email, string DisplayName, string Role);

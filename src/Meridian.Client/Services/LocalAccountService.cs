namespace Meridian.Client.Services;

public sealed record LocalRegistration(
    string FirstName,
    string LastName,
    string Email,
    string Department,
    string LineManager,
    string Password);

public sealed class LocalAccountService(LocalAuthenticationStateProvider auth)
{
    private readonly Dictionary<string, (string Password, string DisplayName)> _accounts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["local@meridian.test"] = ("admin123", "Local Developer")
        };

    public bool TryLogin(string email, string password, out string? error)
    {
        if (_accounts.TryGetValue(email.Trim(), out var account) && account.Password == password)
        {
            auth.SignIn(email.Trim(), account.DisplayName);
            error = null;
            return true;
        }

        error = "Email or password is incorrect.";
        return false;
    }

    public bool TryRegister(LocalRegistration registration, out string? error)
    {
        var email = registration.Email.Trim();
        if (_accounts.ContainsKey(email))
        {
            error = "An account with this email already exists.";
            return false;
        }

        var displayName = $"{registration.FirstName} {registration.LastName}".Trim();
        _accounts[email] = (registration.Password, displayName);
        auth.SignIn(email, displayName);
        error = null;
        return true;
    }
}

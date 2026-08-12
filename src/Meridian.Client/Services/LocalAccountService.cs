namespace Meridian.Client.Services;

public sealed class LocalAccountService
{
    private readonly LocalAuthenticationStateProvider _authenticationStateProvider;
    private readonly Dictionary<string, LocalAccount> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalAccountService(LocalAuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;

        // Local-development-only demo account. This is not used in production.
        _accounts["local@meridian.test"] = new LocalAccount(
            FirstName: "Local",
            Surname: "Developer",
            Email: "local@meridian.test",
            Department: "Development",
            LineManager: "Marc",
            Password: "Meridian123!");
    }

    public Task<LocalAccountResult> LoginAsync(string email, string password)
    {
        var key = email.Trim();

        if (!_accounts.TryGetValue(key, out var account) ||
            !string.Equals(account.Password, password, StringComparison.Ordinal))
        {
            return Task.FromResult(LocalAccountResult.Fail("The email address or password is incorrect."));
        }

        _authenticationStateProvider.SignIn(account.DisplayName, account.Email);
        return Task.FromResult(LocalAccountResult.Ok());
    }

    public Task<LocalAccountResult> RegisterAsync(
        string firstName,
        string surname,
        string email,
        string department,
        string? lineManager,
        string password)
    {
        var key = email.Trim();

        if (_accounts.ContainsKey(key))
        {
            return Task.FromResult(LocalAccountResult.Fail("An account with this email address already exists."));
        }

        var account = new LocalAccount(
            FirstName: firstName.Trim(),
            Surname: surname.Trim(),
            Email: key,
            Department: department.Trim(),
            LineManager: lineManager?.Trim(),
            Password: password);

        _accounts[key] = account;
        _authenticationStateProvider.SignIn(account.DisplayName, account.Email);

        return Task.FromResult(LocalAccountResult.Ok());
    }

    public Task SignOutAsync()
    {
        _authenticationStateProvider.SignOut();
        return Task.CompletedTask;
    }

    private sealed record LocalAccount(
        string FirstName,
        string Surname,
        string Email,
        string Department,
        string? LineManager,
        string Password)
    {
        public string DisplayName => $"{FirstName} {Surname}".Trim();
    }
}

public sealed record LocalAccountResult(bool Success, string? ErrorMessage)
{
    public static LocalAccountResult Ok() => new(true, null);
    public static LocalAccountResult Fail(string message) => new(false, message);
}

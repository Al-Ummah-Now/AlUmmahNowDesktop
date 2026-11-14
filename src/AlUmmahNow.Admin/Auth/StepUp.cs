namespace AlUmmahNow.Admin.Auth;

/// <summary>
/// Step-up for high-risk actions (wiki AUN-ADMIN-NATIVE, Auth): the sign-in page opens again with
/// <c>prompt=login</c> and <c>max_age=900</c>, so the server requires a password (or passkey) from
/// the last 15 minutes. Core builds the authorize URL with PKCE and state; the app adds max_age.
/// </summary>
public static class StepUp
{
    public const int MaxAgeSeconds = 900;

    public static Uri WithMaxAge(Uri authorize, int maxAge = MaxAgeSeconds)
    {
        var builder = new UriBuilder(authorize);
        var parts = (builder.Query.StartsWith("?", StringComparison.Ordinal) ? builder.Query.Substring(1) : builder.Query)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("max_age=", StringComparison.Ordinal) && !p.StartsWith("prompt=", StringComparison.Ordinal))
            .ToList();
        parts.Add("prompt=login");
        parts.Add("max_age=" + maxAge);
        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }
}

/// <summary>The redirect the Admin app registers (all platforms).</summary>
public static class AdminRedirect
{
    public const string Scheme = "alummahnow-admin";
    public const string Uri = "alummahnow-admin://auth/callback";

    /// <summary>A protocol activation is ours only if it is exactly the callback (query aside).</summary>
    public static bool IsCallback(string? value) =>
        value != null && System.Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Scheme && u.Host == "auth" && u.AbsolutePath == "/callback";
}

/// <summary>Signed-in Admin account on the Core AuthManager (one account; the app is for staff).</summary>
public interface IAdminAuth
{
    bool IsSignedIn { get; }

    string? UserId { get; }

    string? DisplayName { get; }

    string? Email { get; }

    Task<string?> AccessTokenAsync(CancellationToken ct = default);

    /// <summary>Opens the system browser and waits for <c>alummahnow-admin://auth/callback</c>.</summary>
    Task SignInAsync(CancellationToken ct = default);

    /// <summary>prompt=login&amp;max_age=900. False when cancelled.</summary>
    Task<bool> StepUpAsync(CancellationToken ct = default);

    Task SignOutAsync();

    /// <summary>The callback the OS handed the app (protocol activation).</summary>
    bool HandleRedirect(string uri);

    /// <summary>True where the platform passkey API exists.</summary>
    bool PasskeysAvailable { get; }

    /// <summary>Sign-in with the Windows passkey sheet (no browser). Throws <c>PasskeyException</c>.</summary>
    Task SignInWithPasskeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a passkey to the signed-in account; on <c>step_up_required</c> runs <see cref="StepUpAsync"/>
    /// (after <paramref name="confirmSignInAgain"/>) and retries once. Throws <c>PasskeyException</c>.
    /// </summary>
    Task AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken ct = default);
}

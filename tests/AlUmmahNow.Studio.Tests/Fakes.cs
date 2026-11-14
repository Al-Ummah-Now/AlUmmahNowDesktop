using System.Text.Json;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Answers API calls from a table of operation id → JSON body (or exception).</summary>
public sealed class FakeTransport : IApiTransport
{
    public readonly List<ApiRequest> Sent = new();
    public readonly Dictionary<string, Func<ApiRequest, string>> Responses = new();
    public Func<string, string?, IReadOnlyList<Suggestion>>? Suggestions;
    public int SuggestCalls;

    public Task<string> SendAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(request);
        return Responses.TryGetValue(request.OperationId, out var f) ? Task.FromResult(f(request)) : Task.FromResult("{}");
    }

    public Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, int limit, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref SuggestCalls);
        return Task.FromResult(Suggestions?.Invoke(text, channelId) ?? Array.Empty<Suggestion>());
    }

    public IReadOnlyList<ApiRequest> Calls(string operationId)
    {
        lock (Sent) return Sent.Where(r => r.OperationId == operationId).ToList();
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);
}

public sealed class FakeAccounts : IStudioAccounts
{
    public readonly List<StudioAccount> Saved = new();
    public string? ActiveId;
    public Exception? SignInError;
    public StudioAccount? NextSignIn;
    public readonly List<string> SignedOut = new();
    public string? LastChannel;

    public event EventHandler? Changed;

    public IReadOnlyList<StudioAccount> Accounts => Saved.ToList();
    public StudioAccount? Active => Saved.FirstOrDefault(a => a.UserId == ActiveId);

    public void Reload() { }

    public Task<StudioAccount> SignInAsync(SignInMode mode, CancellationToken cancellationToken = default)
    {
        if (SignInError != null) throw SignInError;
        var a = NextSignIn ?? new StudioAccount("u1", "a@example.com", "Amina", null, null, false);
        Saved.RemoveAll(x => x.UserId == a.UserId);
        Saved.Add(a);
        ActiveId = a.UserId;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(a);
    }

    public bool PasskeysAvailable { get; set; } = true;
    public Exception? PasskeyError;
    public int PasskeySignIns;
    public int PasskeysAdded;

    public Task<StudioAccount> SignInWithPasskeyAsync(CancellationToken cancellationToken = default)
    {
        PasskeySignIns++;
        if (PasskeyError != null) throw PasskeyError;
        return SignInAsync(SignInMode.Default, cancellationToken);
    }

    public Task<SavedPasskey> AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken cancellationToken = default)
    {
        if (PasskeyError != null) throw PasskeyError;
        PasskeysAdded++;
        return Task.FromResult(new SavedPasskey("pk1", "Windows Hello", null));
    }

    public void Switch(string userId)
    {
        ActiveId = userId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SignOutAsync(string userId)
    {
        SignedOut.Add(userId);
        Saved.RemoveAll(a => a.UserId == userId);
        if (ActiveId == userId) ActiveId = Saved.FirstOrDefault()?.UserId;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void SetActiveChannel(string? channelId) => LastChannel = channelId;
}

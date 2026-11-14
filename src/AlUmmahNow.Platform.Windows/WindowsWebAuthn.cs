using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.Platform.Windows.WebAuthn;

namespace AlUmmahNow.Platform.Windows;

/// <summary>
/// <see cref="IWebAuthn"/> on webauthn.dll: the Windows security sheet over the app window, with
/// Windows Hello, security keys, a phone over QR (hybrid) and, on Windows 11, passkey manager
/// plugins such as 1Password. No browser. The RP id comes from the server's options and
/// clientDataJSON claims <c>https://&lt;rpId&gt;</c>, so the website's passkeys work here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWebAuthn : IWebAuthn
{
    private static readonly Logger Logger = Log.For("passkey");

    private readonly Func<IntPtr> _window;
    private readonly Lazy<uint> _api;

    /// <param name="window">The top-level window the sheet is modal to (read on each ceremony).</param>
    public WindowsWebAuthn(Func<IntPtr> window)
    {
        _window = window;
        _api = new Lazy<uint>(ReadApiVersion);
    }

    /// <summary>webauthn.dll's API version; 0 where it is missing.</summary>
    public uint ApiVersion => _api.Value;

    public bool IsAvailable => ApiVersion >= 1;

    public Task<PasskeyAssertion> GetAssertionAsync(PasskeyRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PasskeyException(PasskeyError.Unavailable, "webauthn.dll is not available");
        var hwnd = _window();
        return RunAsync(cancel =>
        {
            using var arena = new NativeArena();
            var call = WebAuthnMarshal.BuildAssertion(arena, request, ApiVersion, cancel);
            var hr = WebAuthnApi.WebAuthNAuthenticatorGetAssertion(hwnd, call.RpId, call.ClientData, call.Options, out var result);
            try
            {
                Check(hr, signIn: true, result);
                return WebAuthnMarshal.ReadAssertion(result, call.ClientDataJson);
            }
            finally
            {
                if (result != IntPtr.Zero) WebAuthnApi.WebAuthNFreeAssertion(result);
            }
        }, cancellationToken);
    }

    public Task<PasskeyAttestation> MakeCredentialAsync(PasskeyCreation creation, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PasskeyException(PasskeyError.Unavailable, "webauthn.dll is not available");
        var hwnd = _window();
        return RunAsync(cancel =>
        {
            using var arena = new NativeArena();
            var call = WebAuthnMarshal.BuildMakeCredential(arena, creation, ApiVersion, cancel);
            var hr = WebAuthnApi.WebAuthNAuthenticatorMakeCredential(hwnd, call.Rp, call.User, call.Parameters, call.ClientData, call.Options, out var result);
            try
            {
                Check(hr, signIn: false, result);
                return WebAuthnMarshal.ReadAttestation(result, call.ClientDataJson);
            }
            finally
            {
                if (result != IntPtr.Zero) WebAuthnApi.WebAuthNFreeCredentialAttestation(result);
            }
        }, cancellationToken);
    }

    // The calls block until the sheet closes: run them off the UI thread, and map the token to
    // WebAuthNCancelCurrentOperation so closing the window or leaving the page closes the sheet.
    private static async Task<T> RunAsync<T>(Func<IntPtr, T> ceremony, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cancelId = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            var haveId = WebAuthnApi.WebAuthNGetCancellationId(out var guid) == WebAuthnConst.S_OK;
            Marshal.StructureToPtr(guid, cancelId, false);
            using var registration = haveId
                ? cancellationToken.Register(() =>
                {
                    var g = guid;
                    WebAuthnApi.WebAuthNCancelCurrentOperation(ref g);
                })
                : default;
            var result = await Task.Factory.StartNew(() => ceremony(haveId ? cancelId : IntPtr.Zero), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            return result;
        }
        catch (PasskeyException ex) when (ex.Error == PasskeyError.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            Marshal.FreeHGlobal(cancelId);
        }
    }

    private static void Check(int hr, bool signIn, IntPtr result)
    {
        if (hr == WebAuthnConst.S_OK && result != IntPtr.Zero) return;
        var error = WebAuthnMarshal.MapError(hr, signIn);
        var name = ErrorName(hr);
        if (error == PasskeyError.Cancelled) Logger.Info("passkey sheet closed: " + name);
        else Logger.Warn("passkey ceremony failed: " + name + " 0x" + hr.ToString("X8"));
        throw new PasskeyException(error, "webauthn.dll returned " + name + " (0x" + hr.ToString("X8") + ")");
    }

    private static string ErrorName(int hr)
    {
        try
        {
            return Marshal.PtrToStringUni(WebAuthnApi.WebAuthNGetErrorName(hr)) ?? "UnknownError";
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            return "UnknownError";
        }
    }

    private static uint ReadApiVersion()
    {
        try
        {
            var v = WebAuthnApi.WebAuthNGetApiVersionNumber();
            Logger.Info("webauthn.dll API version " + v);
            return v;
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
        {
            Logger.Info("no webauthn.dll: " + ex.GetType().Name);
            return 0;
        }
    }
}

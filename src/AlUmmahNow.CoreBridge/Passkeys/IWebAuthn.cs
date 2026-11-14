using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>
    /// The platform passkey API (Windows: webauthn.dll). Shows the system's own security sheet
    /// (Windows Hello, security keys, a phone over QR, passkey manager plugins) over the app window.
    /// Implementations build clientDataJSON with <see cref="WebAuthnJson.ClientData"/>.
    /// Failures are <see cref="PasskeyException"/>; <see cref="PasskeyError.Cancelled"/> when the
    /// person closed the sheet.
    /// </summary>
    public interface IWebAuthn
    {
        /// <summary>False where the platform has no passkey API: the app hides its passkey buttons.</summary>
        bool IsAvailable { get; }

        Task<PasskeyAssertion> GetAssertionAsync(PasskeyRequest request, CancellationToken cancellationToken = default);

        Task<PasskeyAttestation> MakeCredentialAsync(PasskeyCreation creation, CancellationToken cancellationToken = default);
    }
}

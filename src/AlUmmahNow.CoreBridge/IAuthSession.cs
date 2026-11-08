using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    public sealed class AuthAccount
    {
        public AuthAccount(string subject, string? email)
        {
            Subject = subject;
            Email = email;
        }

        public string Subject { get; }
        public string? Email { get; }
    }

    /// <summary>Thrown when an action needs sign-in, or a stored session is no longer valid.</summary>
    public sealed class AuthRequiredException : Exception
    {
        public AuthRequiredException(string message) : base(message) { }
    }

    /// <summary>Thrown when an interactive sign-in does not complete.</summary>
    public sealed class AuthFailedException : Exception
    {
        public AuthFailedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>Sign-in and tokens. Backed by aun-auth once AlUmmahNowCore exposes it.</summary>
    public interface IAuthSession
    {
        event EventHandler? AccountChanged;

        AuthAccount? Account { get; }

        /// <summary>Restores a saved session, if any, without network access.</summary>
        void Restore();

        /// <summary>Runs an interactive browser sign-in.</summary>
        Task SignInAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns a valid access token, refreshing when needed.</summary>
        /// <exception cref="AuthRequiredException">No session, or the session was rejected.</exception>
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

        void SignOut();
    }

    /// <summary>OS-owned secret storage. Same shape as the core's SecureStorage adapter.</summary>
    public interface ISecureStore
    {
        byte[]? Get(string key);
        void Set(string key, byte[] value);
        void Delete(string key);
        string[] List(string prefix);
    }

    public interface IBrowserLauncher
    {
        void Open(Uri uri);
    }
}

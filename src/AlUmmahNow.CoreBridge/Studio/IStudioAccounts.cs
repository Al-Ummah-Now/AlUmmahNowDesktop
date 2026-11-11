using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>A signed-in account on this device (no secrets).</summary>
    public sealed class StudioAccount
    {
        public StudioAccount(string userId, string? email, string? displayName, string? avatarUrl, string? activeChannelId, bool needsReauth)
        {
            UserId = userId;
            Email = email;
            DisplayName = displayName;
            AvatarUrl = avatarUrl;
            ActiveChannelId = activeChannelId;
            NeedsReauth = needsReauth;
        }

        public string UserId { get; }
        public string? Email { get; }
        public string? DisplayName { get; }
        public string? AvatarUrl { get; }

        /// <summary>The channel this account last worked in.</summary>
        public string? ActiveChannelId { get; }

        /// <summary>The refresh token was rejected: sign in again.</summary>
        public bool NeedsReauth { get; }

        /// <summary>What to show for the account: display name, else email, else the id.</summary>
        public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : !string.IsNullOrWhiteSpace(Email) ? Email! : UserId;
    }

    public enum SignInMode
    {
        /// <summary>Silent when the browser is already signed in, the form otherwise.</summary>
        Default,

        /// <summary>Always the sign-in form, to add another account.</summary>
        AddAccount,
    }

    /// <summary>
    /// Multi-account sign-in for the Studio: system browser, PKCE and a loopback redirect. Backed by
    /// aun-auth's AuthManager. <see cref="Changed"/> may fire on any thread.
    /// </summary>
    public interface IStudioAccounts
    {
        event EventHandler? Changed;

        IReadOnlyList<StudioAccount> Accounts { get; }
        StudioAccount? Active { get; }

        /// <summary>Re-reads the saved accounts (no network).</summary>
        void Reload();

        Task<StudioAccount> SignInAsync(SignInMode mode, CancellationToken cancellationToken = default);

        void Switch(string userId);

        /// <summary>Removes the account from this device and revokes its refresh token on the server.</summary>
        Task SignOutAsync(string userId);

        /// <summary>Remembers the active account's channel.</summary>
        void SetActiveChannel(string? channelId);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.Demo
{
    /// <summary>
    /// The live scenarios of CI (<c>AUN_AUTOMATION</c>): <c>live</c> is the list, <c>live:ID</c> a page, <c>behind:ID</c> a
    /// page that is paused, <c>remind-set:ID</c>, <c>remind-failed:ID</c> and <c>remind-signin:ID</c> one whose reminder is
    /// asked for, and what must come of it.
    /// </summary>
    public static class LiveAutomation
    {
        public static Route? RouteOf(string automation) =>
            automation == "live" ? new Route.LiveList()
            : IdOf(automation) is { } id ? new Route.Live(id)
            : (Route?)null;

        /// <summary>What a viewer does on the page, once it is there.</summary>
        public static async Task ActAsync(string automation, Func<LiveWatchViewModel?> page, AuthViewModel auth)
        {
            await Task.Delay(3_000);
            if (automation.StartsWith("behind:", StringComparison.Ordinal)) page()?.Watch?.TogglePlay();
            if (automation.StartsWith("remind-", StringComparison.Ordinal) && page() is { } live) await live.ToggleReminderAsync(auth);
        }

        /// <summary>Whether the page is what the scenario asks for; null when it is none of these.</summary>
        public static bool? Passed(string automation, LiveListViewModel list, LiveWatchViewModel? page, AuthViewModel auth, double furthest)
        {
            if (automation == "live") return list.State == LoadState.Content && list.All.Count > 0;
            if (IdOf(automation) == null) return null;
            if (page == null || page.State != LoadState.Content) return false;
            if (automation.StartsWith("behind:", StringComparison.Ordinal)) return page.IsBehind;
            if (automation.StartsWith("remind-set:", StringComparison.Ordinal)) return auth.IsSignedIn && page.ReminderSet && !page.ReminderFailed;
            if (automation.StartsWith("remind-failed:", StringComparison.Ordinal)) return auth.IsSignedIn && page.ReminderFailed && !page.ReminderSet;
            if (automation.StartsWith("remind-signin:", StringComparison.Ordinal)) return auth.IsBusy && !page.ReminderSet;
            return page.Watch == null || furthest > 2;
        }

        private static string? IdOf(string automation)
        {
            foreach (var kind in new[] { "live:", "behind:", "remind-set:", "remind-failed:", "remind-signin:" })
            {
                if (automation.StartsWith(kind, StringComparison.Ordinal)) return automation.Substring(kind.Length);
            }

            return null;
        }
    }

    /// <summary>
    /// Sign-in for review frames (<c>AUN_DEMO_AUTH</c>): <c>in</c> signs in at once, <c>waiting</c> is the browser that is
    /// still open. No network, no browser, never in a normal launch.
    /// </summary>
    public sealed class DemoAuthSession : IAuthSession
    {
        private readonly bool _waits;

        public DemoAuthSession(bool waits) => _waits = waits;

        public event EventHandler? AccountChanged;

        public AuthAccount? Account { get; private set; }

        public void Restore()
        {
        }

        public async Task SignInAsync(CancellationToken cancellationToken = default)
        {
            if (_waits) await Task.Delay(Timeout.Infinite, cancellationToken);
            Account = new AuthAccount("demo", "reader@example.com");
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("demo");

        public void SignOut()
        {
            Account = null;
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

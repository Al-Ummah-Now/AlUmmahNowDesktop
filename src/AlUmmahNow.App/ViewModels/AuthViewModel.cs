using System;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.ViewModels
{
    public sealed partial class AuthViewModel : ObservableObject
    {
        private readonly IAuthSession _session;
        private SynchronizationContext? _ui;

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _failed;

        public AuthViewModel(IAuthSession session)
        {
            _session = session;
            _session.AccountChanged += (s, e) => RaiseOnUi();
        }

        public bool IsSignedIn => _session.Account != null;
        public string AccountName => _session.Account?.Email ?? "";

        public void Restore()
        {
            _ui = SynchronizationContext.Current;
            _session.Restore();
        }

        public async Task SignInAsync(CancellationToken cancellationToken = default)
        {
            if (IsBusy) return;
            _ui = SynchronizationContext.Current ?? _ui;
            IsBusy = true;
            Failed = false;
            try
            {
                await _session.SignInAsync(cancellationToken);
            }
            catch (AuthFailedException)
            {
                Failed = true;
            }
            catch (CatalogException)
            {
                Failed = true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void SignOut() => _session.SignOut();

        // Token refresh runs on worker threads, and views must only change on the UI thread.
        private void RaiseOnUi()
        {
            var ui = _ui;
            if (ui == null || ReferenceEquals(ui, SynchronizationContext.Current)) RaiseAccount();
            else ui.Post(_ => RaiseAccount(), null);
        }

        private void RaiseAccount()
        {
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(AccountName));
        }
    }
}

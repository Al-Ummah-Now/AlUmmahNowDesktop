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

        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _failed;

        public AuthViewModel(IAuthSession session)
        {
            _session = session;
            _session.AccountChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(AccountName));
            };
        }

        public bool IsSignedIn => _session.Account != null;
        public string AccountName => _session.Account?.Email ?? "";

        public void Restore() => _session.Restore();

        public async Task SignInAsync(CancellationToken cancellationToken = default)
        {
            if (IsBusy) return;
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
    }
}

using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Institution;

/// <summary>
/// Institution mode (E19 19.9): every channel the account owns or manages, the brand kit and the shared asset
/// library. Team members acting as a channel they manage see the owner's kit and assets read-only.
/// </summary>
public sealed partial class InstitutionViewModel : ObservableObject
{
    private static readonly Logger Logger = Log.For("institution");

    private readonly StudioShellViewModel _shell;
    private readonly IInstitutionApi _api;
    private int _generation;

    [ObservableProperty] private BrandKitState? _state;
    [ObservableProperty] private BrandKit _kit = new();
    [ObservableProperty] private StudioAssetLibrary? _library;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _noticeKey;

    public InstitutionViewModel(StudioShellViewModel shell, IInstitutionApi api)
    {
        _shell = shell;
        _api = api;
        _shell.ChannelChanged += (_, _) => _ = LoadAsync();
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StudioShellViewModel.Channels)) OnPropertyChanged(nameof(Channels));
        };
    }

    public Func<string, object[], Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);

    /// <summary>All channels: owned first, then the ones the account manages, each by name.</summary>
    public IReadOnlyList<StudioChannel> Channels => _shell.Channels
        .OrderBy(c => c.Role == ChannelRole.Owner ? 0 : 1)
        .ThenByDescending(c => c.IsMain)
        .ThenBy(c => c.Name, StringComparer.CurrentCulture)
        .ToList();

    public StudioChannel? Active => _shell.ActiveChannel;

    public void Switch(StudioChannel channel)
    {
        _shell.SelectChannel(channel);
        OnPropertyChanged(nameof(Active));
    }

    public bool CanEdit => State?.CanEdit == true;

    public bool IsDirty => State != null && !Kit.Equals(State.Kit);

    /// <summary>Acting as another owner's channel: the kit and assets are theirs.</summary>
    private string? ChannelHeader => _shell.ActiveChannel is { Role: not ChannelRole.Owner } c ? c.Id : null;

    partial void OnStateChanged(BrandKitState? value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnKitChanged(BrandKit value) => OnPropertyChanged(nameof(IsDirty));

    public async Task LoadAsync()
    {
        OnPropertyChanged(nameof(Active));
        if (_shell.ActiveAccount == null) return;
        var gen = Interlocked.Increment(ref _generation);
        IsLoading = true;
        ErrorKey = null;
        try
        {
            var kit = _api.BrandKitAsync(ChannelHeader);
            var library = _api.AssetsAsync(ChannelHeader);
            await Task.WhenAll(kit, library);
            if (gen != _generation) return;
            State = kit.Result;
            Kit = kit.Result.Kit.Clone();
            Library = library.Result;
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (Exception ex) when (ex is StudioApiException or RateLimitedException)
        {
            Logger.Warn("brand kit did not load", ex);
            if (gen == _generation) ErrorKey = "studio.brand.failed";
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }

    /// <summary>Changes one field of the edited kit (the page passes a changed copy).</summary>
    public void Edit(Action<BrandKit> change)
    {
        if (!CanEdit) return;
        var next = Kit.Clone();
        change(next);
        Kit = next;
    }

    public void Revert()
    {
        if (State != null) Kit = State.Kit.Clone();
    }

    public async Task SaveAsync()
    {
        if (!CanEdit || !IsDirty) return;
        if (Kit.Invalid() is { } field)
        {
            ErrorKey = "studio.brand.invalid." + field.Replace(".", "_", StringComparison.Ordinal);
            return;
        }

        IsSaving = true;
        ErrorKey = null;
        try
        {
            var saved = await _api.SaveBrandKitAsync(Kit);
            State = new BrandKitState(saved, true, true);
            Kit = saved.Clone();
            NoticeKey = "studio.brand.saved";
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("brand kit not saved", ex);
            ErrorKey = ex.Status == 403 ? "studio.brand.readOnly" : ex.Status == 400 ? "studio.brand.rejected" : "studio.brand.saveFailed";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Uploads a file to the shared library. Checks the limits before reading the bytes.</summary>
    public async Task AddAssetAsync(string path)
    {
        if (!CanEdit || Library is not { } lib) return;
        ErrorKey = null;
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            ErrorKey = "studio.assets.unreadable";
            return;
        }

        if (lib.Refusal(size) is { } refusal)
        {
            ErrorKey = "studio.assets." + refusal;
            return;
        }

        IsSaving = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var asset = await _api.AddAssetAsync(Path.GetFileNameWithoutExtension(path), bytes);
            Library = await _api.AssetsAsync(ChannelHeader);
            NoticeKey = "studio.assets.added";
            Logger.Info($"asset added: {asset.Kind}");
        }
        catch (IOException)
        {
            ErrorKey = "studio.assets.unreadable";
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("asset not added", ex);
            ErrorKey = ex.Code switch
            {
                "library_full" => "studio.assets.library_full",
                "too_large" => "studio.assets.too_large",
                "empty" => "studio.assets.empty",
                "unsupported_type" => "studio.assets.unsupported",
                _ => ex.Status == 403 ? "studio.brand.readOnly" : "studio.assets.failed",
            };
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task DeleteAssetAsync(StudioAsset asset)
    {
        if (!CanEdit) return;
        var used = new[] { Kit.Logo, Kit.Intro, Kit.Outro, State?.Kit.Logo, State?.Kit.Intro, State?.Kit.Outro }.Contains(asset.Id);
        if (!await Confirm(used ? "studio.assets.deleteUsed" : "studio.assets.deleteConfirm", new object[] { asset.Name })) return;
        try
        {
            await _api.DeleteAssetAsync(asset.Id);
            Library = await _api.AssetsAsync(ChannelHeader);
            if (used)
            {
                // The server drops the reference from the saved kit; reload so the editor shows it gone.
                var state = await _api.BrandKitAsync(ChannelHeader);
                State = state;
                Kit = state.Kit.Clone();
            }
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("asset not deleted", ex);
            ErrorKey = ex.Status == 403 ? "studio.brand.readOnly" : "studio.assets.deleteFailed";
        }
    }

    public string? AssetName(string? id) => id == null ? null : Library?.Assets.FirstOrDefault(a => a.Id == id)?.Name;
}

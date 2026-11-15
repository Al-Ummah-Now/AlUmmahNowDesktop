using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Institution;

namespace AlUmmahNow.Studio.Timeline;

/// <summary>
/// Keeps the editor on the active channel's saved brand kit (institution mode, 19.9): when the kit loads or is saved,
/// new titles take its colours and every title is drawn with its template and logo.
/// </summary>
public static class EditorBrand
{
    public static void Attach(InstitutionViewModel institution, EditorViewModel editor)
    {
        var generation = 0;
        institution.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstitutionViewModel.State)) _ = ApplyAsync();
        };
        _ = ApplyAsync();

        async Task ApplyAsync()
        {
            var gen = ++generation;
            if (institution.State is not { } state) return;
            editor.Brand = state.Kit.Clone();
            var logo = await institution.LogoAsync().ConfigureAwait(true);
            if (gen == generation) editor.BrandLogo = logo;
        }
    }
}

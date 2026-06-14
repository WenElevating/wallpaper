using System.Globalization;
using System.Threading;

namespace WallpaperApp.Localization;

// Centralizes applying a UI culture: thread UI/format cultures, the
// strongly-typed Strings accessor, and the XAML-facing TranslationSource. Call
// ApplyCulture at startup (before any window is shown) and whenever the user
// changes the language.
public static class LocalizationService
{
    public const string Chinese = "zh-CN";
    public const string English = "en";

    /// <summary>
    /// Resolves a saved language code to the canonical code in use. An empty
    /// value means "follow the OS UI language".
    /// </summary>
    public static string EffectiveCode(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            return code == Chinese ? Chinese : English;

        return CultureInfo.InstalledUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    public static void ApplyCulture(string? code)
    {
        var effective = EffectiveCode(code);
        var uiCulture = effective == Chinese
            ? CultureInfo.GetCultureInfo(Chinese)
            : CultureInfo.InvariantCulture; // -> neutral (English) resource fallback
        var formatCulture = effective == Chinese
            ? CultureInfo.GetCultureInfo(Chinese)
            : CultureInfo.GetCultureInfo("en-US");

        Thread.CurrentThread.CurrentUICulture = uiCulture;
        Thread.CurrentThread.CurrentCulture = formatCulture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = formatCulture;

        Strings.Culture = uiCulture;

        // Keep XAML bindings in sync. ResourceManager is idempotently assigned.
        TranslationSource.Instance.ResourceManager ??= Strings.ResourceManager;
        TranslationSource.Instance.CurrentCulture = uiCulture;
    }
}

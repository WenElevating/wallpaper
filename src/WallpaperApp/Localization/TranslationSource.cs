using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;

namespace WallpaperApp.Localization;

// INotifyPropertyChanged singleton that exposes localized strings to XAML via an
// indexer. {loc:Loc Key} binds to Instance[Key]; when the culture changes we
// raise PropertyChanged with Binding.IndexerName, which refreshes every indexer
// binding in one shot — so the whole UI updates live without a restart.
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static readonly TranslationSource Instance = new();

    private ResourceManager? _manager;
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private TranslationSource() { }

    public ResourceManager? ResourceManager
    {
        get => _manager;
        set
        {
            _manager = value;
            OnChanged();
        }
    }

    public CultureInfo CurrentCulture
    {
        get => _culture;
        set
        {
            _culture = value;
            OnChanged();
        }
    }

    private void OnChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

    public string this[string key] => _manager?.GetString(key, _culture) ?? key;
}

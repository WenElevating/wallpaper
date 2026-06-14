using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace WallpaperApp.Localization;

// XAML markup extension: {loc:Loc Key} expands to a one-way Binding against
// TranslationSource.Instance[Key]. Because it is a binding (not a static value),
// the text refreshes live when the culture changes. Usage:
//   <TextBlock Text="{loc:Loc LibraryLabel}"/>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Lazy-init the shared ResourceManager so the extension works even if the
        // app hasn't set it explicitly yet.
        TranslationSource.Instance.ResourceManager ??= Strings.ResourceManager;

        var binding = new Binding
        {
            Source = TranslationSource.Instance,
            Path = new PropertyPath("[" + Key + "]"),
            Mode = BindingMode.OneWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

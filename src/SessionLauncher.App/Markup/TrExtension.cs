using System;
using System.Windows.Markup;
using SessionLauncher.App.Services;

namespace SessionLauncher.App.Markup
{
    /// <summary>
    /// XAML shorthand for a localised string: <c>Text="{markup:Tr app.title}"</c>.
    /// </summary>
    /// <remarks>
    /// A markup extension is evaluated once, when the XAML tree is built, so switching
    /// language cannot re-run it. The window handles that by rebuilding itself (see
    /// <c>MainWindow.OnLanguageToggle</c>), which is why this is safe here and would
    /// not be in a long-lived view model.
    /// </remarks>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension()
        {
        }

        public TrExtension(string key) => Key = key;

        /// <summary>A key from <see cref="Loc"/>.</summary>
        public string? Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
            => Key is null ? string.Empty : Loc.T(Key);
    }
}

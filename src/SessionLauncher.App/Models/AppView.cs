// AppView.cs
//
// Which list the window is showing. Its own file, and public, because it is
// persisted by name in AppSettings: a private enum nested in MainWindow would still
// serialise, but the setting would then depend on a type the settings layer cannot
// see, and the self-test could not round-trip it.

namespace SessionLauncher.App.Models;

/// <summary>The list the launcher is currently showing.</summary>
public enum AppView
{
    /// <summary>Top-level conversations from the catalog. The default.</summary>
    Sessions,

    /// <summary>Projects discovered from opencode's own logs.</summary>
    Projects,
}

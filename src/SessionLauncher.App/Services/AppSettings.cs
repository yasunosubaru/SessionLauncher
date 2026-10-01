using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services
{
    /// <summary>
    /// UI preferences persisted next to the executable as JSON.
    /// </summary>
    /// <remarks>
    /// Load and Save are both total: a GUI app that refuses to start because its
    /// settings file got truncated is worse than one that quietly uses defaults.
    /// </remarks>
    public sealed class AppSettings
    {
        private const string FileName = "sessionlauncher.settings.json";

        public const double MinFontScale = 0.6;
        public const double MaxFontScale = 2.0;
        public const double DefaultFontScale = 1.0;

        public double FontScale { get; set; } = DefaultFontScale;

        public AppLang Language { get; set; } = AppLang.ZhHans;

        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }

        /// <summary>Which list the window shows. Persisted because translating the UI
        /// rebuilds the window, and a field-only choice would reset the view.</summary>
        public AppView View { get; set; } = AppView.Sessions;

        /// <summary>Ordering of the project list.</summary>
        public ProjectSortMode ProjectSort { get; set; } = ProjectSortMode.LastUsed;

        /// <summary>Whether projects whose directory has been deleted are hidden.</summary>
        public bool HideMissingProjects { get; set; } = true;

        /// <summary>
        /// Test seam: when set, <see cref="DefaultPath"/> returns this instead of the
        /// exe-relative location.
        /// </summary>
        public static string? PathOverride { get; set; }

        public static string DefaultPath =>
            PathOverride ?? Path.Combine(AppContext.BaseDirectory, FileName);

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Write enums as names, not ordinals. The file is meant to be hand-editable,
            // and "language": "En" is the difference between a usable settings file and one
            // that silently resets every time someone types an edit into it. Integer values
            // stay accepted so older files still load.
            Converters = { new JsonStringEnumConverter() },
        };

        public static AppSettings Load()
        {
            var path = DefaultPath;
            try
            {
                if (!File.Exists(path)) return new AppSettings();

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is null) return new AppSettings();

                loaded.Sanitise();
                return loaded;
            }
            catch
            {
                // Corrupt, truncated, unreadable, or written by a newer version.
                return new AppSettings();
            }
        }

        /// <summary>Clamp every field into a range the UI can actually honour.</summary>
        public void Sanitise()
        {
            if (double.IsNaN(FontScale) || double.IsInfinity(FontScale) || FontScale <= 0)
                FontScale = DefaultFontScale;
            FontScale = Math.Clamp(FontScale, MinFontScale, MaxFontScale);

            // Repeated 0.1 steps accumulate binary float drift, which then shows up as
            // 1.3000000000000003 in a file people are invited to hand-edit.
            FontScale = Math.Round(FontScale, 2);

            if (!Enum.IsDefined(typeof(AppLang), Language))
                Language = AppLang.ZhHans;

            if (WindowWidth is <= 0) WindowWidth = null;
            if (WindowHeight is <= 0) WindowHeight = null;

            // A monitor can disappear between sessions; an off-screen restore would
            // make the app look like it failed to launch.
            if (WindowLeft is < -32000 or > 32000) WindowLeft = null;
            if (WindowTop is < -32000 or > 32000) WindowTop = null;
        }

        public void Save()
        {
            Sanitise();
            var path = DefaultPath;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Atomic: write beside the target, then swap, so an interrupted save
                // cannot leave a half-written file that Load() would reject.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // Losing a preference is not worth interrupting the user over.
            }
        }

        public AppSettings Clone() => new()
        {
            FontScale = FontScale,
            Language = Language,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
        };
    }
}

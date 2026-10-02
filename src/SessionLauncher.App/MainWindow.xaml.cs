using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SessionLauncher.App.Models;
using SessionLauncher.App.Services;

// .NET 10 introduced System.Range, which would otherwise be ambiguous with the
// highlight-range struct the search engine returns.
using Range = SessionLauncher.App.Services.Range;

namespace SessionLauncher.App
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Guard rail for batch opens. Spawning dozens of TUI windows helps nobody and is
        /// awkward to undo, so refuse and let the user narrow the selection.
        /// </summary>
        private const int MaxBatchOpen = 10;

        /// <summary>Base font size at 100%. Everything else scales relative to this.</summary>
        private const double BaseFontSize = 13.0;

        private const double FontStep = 0.1;

        private readonly LauncherService _launcher = new();
        private readonly List<SessionInfo> _all = new();
        private readonly string _catalogPath;
        private readonly AppSettings _settings;
        private double _fontScale;

        /// <summary>
        /// Session id to the original catalog record.
        /// </summary>
        /// <remarks>
        /// The search engine works on <see cref="SearchDoc"/>, which carries no timestamp.
        /// Rows are rebuilt from search hits on every keystroke, so the original record has
        /// to be looked up rather than reconstructed, or every row would lose its date.
        /// </remarks>
        private readonly Dictionary<string, SessionInfo> _byId = new(StringComparer.Ordinal);

        /// <summary>
        /// The authoritative selection: session ids the user ticked.
        /// </summary>
        /// <remarks>
        /// It lives here rather than on the row objects because filtering rebuilds the item
        /// collection, which would otherwise drop every tick. Rows mirror it through
        /// <see cref="SessionRow.IsChosen"/> for the checkbox and row highlight.
        /// </remarks>
        private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);

        /// <summary>Which of the two lists is showing. Persisted, not a field only.</summary>
        private AppView _view = AppView.Sessions;

        // (AppView itself lives in Models so AppSettings can persist it by name.)

        /// <summary>All projects found by the last log scan, unfiltered and unsorted.</summary>
        private List<ProjectRow> _projects = new();

    /// <summary>The in-flight "waiting for OpenChamber to quit" poll, if any.</summary>
    private ProjectRegistrationWait? _registrationWait;

    /// <summary>Set by the cancel button, so a cancel is not reported as a timeout.</summary>
    private bool _registrationWaitCancelled;

    /// <summary>Set when the window closes, so a late continuation cannot write.</summary>
    private bool _closing;

        /// <summary>Project list filter text.</summary>
        private string _projectFilter = string.Empty;

        /// <summary>How the project list is ordered.</summary>
        private ProjectSortMode _projectSort = ProjectSortMode.LastUsed;

        /// <summary>Whether vanished projects are hidden from the list.</summary>
        private bool _hideMissing = true;

        /// <summary>
        /// Set while the sort combo is being populated, so restoring a persisted choice
        /// does not immediately write it back out as if the user had picked it.
        /// </summary>
        private bool _suppressSortSave;

        /// <summary>
        /// True only while <c>InitializeComponent</c> is running. Checked/Unchecked/
        /// SelectionChanged fire during the load, before later elements exist, so every
        /// such handler has to bail out when this is set.
        /// </summary>
        private bool _initializing;

        public MainWindow()
        {
            // Language and font scale must be applied BEFORE InitializeComponent().
            // The XAML markup extensions run while the tree is being built and bake their
            // result into the text, so setting them afterwards leaves every static label in
            // the previous language and only the imperatively-assigned text switches.
            _settings = AppSettings.Load();
            _fontScale = _settings.FontScale;
            _view = _settings.View == AppView.Projects ? AppView.Projects : AppView.Sessions;
            _projectSort = _settings.ProjectSort;
            _hideMissing = _settings.HideMissingProjects;
            Loc.SetLanguage(_settings.Language);

            // A CheckBox declared IsChecked="True" in XAML raises Checked DURING
            // InitializeComponent, i.e. before the elements that come after it in the
            // tree have been constructed. The handler then dereferences a null
            // ProjectList and the window dies at startup. Fence off the whole load and
            // apply the real state once every named element exists.
            _initializing = true;
            try
            {
                InitializeComponent();
            }
            finally { _initializing = false; }

            _catalogPath = SessionCatalog.ResolveCatalogPath(AppContext.BaseDirectory)
                           ?? SessionCatalog.ProbeCandidatePaths(AppContext.BaseDirectory)[0];

            ApplyFontScale();
            ApplyCatalogPath();

            // The sort box is populated after InitializeComponent so its items can be
            // localised, and PopulateProjectSortBox restores the persisted choice
            // itself (by reference, which SelectedItem requires).
            _suppressSortSave = true;
            PopulateProjectSortBox();
            _suppressSortSave = false;
            HideMissingCheck.IsChecked = _hideMissing;
            ApplyView();

            SourceInitialized += (_, _) => ApplyDarkTitleBar();
        }

        // ---- lifecycle -------------------------------------------------------

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            RestorePlacement();

            // Read the catalog whenever the session list will be shown, including on
            // startup in the project view, so switching over is instant and the
            // session count is already correct. The project list is only scanned when
            // it is actually on screen: that is 50 MB of logs.
            if (_view == AppView.Sessions) { LoadCatalog(); Reload(); }
            else
            {
                LoadCatalog();
                UpdateTotalChip();
                ReloadProjects();
            }
        }

        /// <summary>
        /// Ask DWM for the dark title bar and the acrylic backdrop.
        /// </summary>
        /// <remarks>
        /// Two attribute families are needed. Immersive dark mode (20, legacy 19) makes
        /// the title bar match the app instead of staying light. The backdrop attribute
        /// (38, DWMWA_SYSTEMBACKDROP_TYPE) is what actually produces the glass; it
        /// exists only on Windows 11 22H2 and later, so every call is best-effort and
        /// the app is fully usable without it.
        /// <para>
        /// Acrylic (3) is chosen over Mica (2) deliberately: Mica is tuned for apps with
        /// a full title bar and reads almost flat at this window size, while
        /// TransientWindow/acrylic gives the frosted, slightly luminous look.
        /// <para>
        /// The attribute does nothing on its own. DWM draws the backdrop, but WPF then
        /// paints over it unless the window background itself carries an alpha channel,
        /// which is why every brush in the theme is translucent.
        /// </remarks>
        private void ApplyDarkTitleBar()
        {
            const int useImmersiveDarkMode = 20;
            const int useImmersiveDarkModeLegacy = 19;
            const int systemBackdropType = 38;

            // DWMSBT values: 2 = Mica, 3 = TransientWindow (acrylic).
            const int backdropAcrylic = 3;
            const int backdropMica = 2;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            foreach (var attribute in new[] { useImmersiveDarkMode, useImmersiveDarkModeLegacy })
            {
                var enabled = 1;
                if (DwmSetWindowAttribute(handle, attribute, ref enabled, sizeof(int)) != 0)
                    enabled = 0;
            }

            // Try acrylic, then Mica. Both fail harmlessly on pre-22H2 builds.
            foreach (var backdrop in new[] { backdropAcrylic, backdropMica })
            {
                var value = backdrop;
                if (DwmSetWindowAttribute(handle, systemBackdropType, ref value, sizeof(int)) == 0)
                    return;
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        // ---- font scaling ----------------------------------------------------

        private void ApplyFontScale()
        {
            _fontScale = Math.Clamp(_fontScale, AppSettings.MinFontScale, AppSettings.MaxFontScale);
            _settings.FontScale = _fontScale;
            _settings.Save();

            // Setting FontSize on the window and letting everything inherit keeps text
            // vector-crisp at any scale, which a LayoutTransform would not.
            FontSize = Math.Round(BaseFontSize * _fontScale, 2);

            // GridViewColumn widths are fixed doubles, so they do not follow FontSize.
            // Without this the date column clips to "2026-10-01 1" at 140%.
            ApplyColumnWidths();

            FontPercentLabel.Text =
                Loc.Format(Loc.FontPercent, (int)Math.Round(_fontScale * 100));
        }

        /// <summary>Column widths at 100%, scaled by the current font scale.</summary>
        /// <remarks>
        /// The project columns are included because they used not to be, and at 140%
        /// their fixed widths clipped the date columns — including the creation time
        /// column this change added, which would have shipped already broken at the
        /// font sizes the app offers.
        /// </remarks>
        private void ApplyColumnWidths()
        {
            void Set(GridViewColumn column, double at100)
            {
                if (column is not null) column.Width = Math.Round(at100 * _fontScale);
            }

            Set(ColPick, 40);
            Set(ColCreated, 132);
            Set(ColMsgs, 60);
            Set(ColTitle, 380);
            Set(ColFolder, 220);

            Set(PColMark, 46);
            Set(PColName, 330);
            Set(PColSessions, 92);
            Set(PColCreated, 150);
            Set(PColLast, 150);
            Set(PColPath, 560);
        }

        private void OnFontBiggerClick(object sender, RoutedEventArgs e)
        {
            _fontScale += FontStep;
            ApplyFontScale();
        }

        private void OnFontSmallerClick(object sender, RoutedEventArgs e)
        {
            _fontScale -= FontStep;
            ApplyFontScale();
        }

        private void OnFontResetClick(object sender, RoutedEventArgs e)
        {
            _fontScale = AppSettings.DefaultFontScale;
            ApplyFontScale();
        }

        // ---- language --------------------------------------------------------

        /// <summary>
        /// Switch language by rebuilding the window.
        /// </summary>
        /// <remarks>
        /// Localised text is baked in by a markup extension at parse time, so there is no
        /// cheap way to re-evaluate it in place. Rebuilding also gives every control the
        /// new column widths and hint placement for free, which matters when the German
        /// of a label is twice the length of the Chinese. Window geometry carries over.
        /// </remarks>
        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            var left = Left;
            var top = Top;
            var width = Width;
            var height = Height;
            var wasMaximised = WindowState == WindowState.Maximized;

            _settings.WindowLeft = left;
            _settings.WindowTop = top;
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;
            _settings.Language = Loc.Other;
            _settings.Save();

            Loc.SetLanguage(_settings.Language);

            // Deliberately no Owner: in WPF closing an owner closes every window it owns,
            // so the replacement would be torn down along with this one.
            var replacement = new MainWindow();
            replacement.Left = left;
            replacement.Top = top;
            replacement.Width = width;
            replacement.Height = height;
            if (wasMaximised) replacement.WindowState = WindowState.Maximized;
            replacement.Show();

            Close();
        }

        // ---- window placement -------------------------------------------------

        private void RestorePlacement()
        {
            if (_settings.WindowWidth is > 0) Width = _settings.WindowWidth.Value;
            if (_settings.WindowHeight is > 0) Height = _settings.WindowHeight.Value;

            if (_settings.WindowLeft is null || _settings.WindowTop is null) return;

            // Only restore a position that still lands on a connected screen; otherwise
            // the app would appear to have failed to launch.
            var left = _settings.WindowLeft.Value;
            var top = _settings.WindowTop.Value;
            if (SystemParameters.VirtualScreenLeft - Width < left &&
                left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                SystemParameters.VirtualScreenTop - Height < top &&
                top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                Left = left;
                Top = top;
            }
        }

        // ---- data -------------------------------------------------------------

        private void ApplyCatalogPath()
        {
            SourceText.Text = _catalogPath;

            // Not the counter: this runs from the constructor, before any data is
            // loaded, and setting the chip here stamped "0 conversations" over the
            // project count. UpdateTotalChip owns the counter instead.
        }

        /// <summary>Re-read the catalog into memory. Does not touch the views.</summary>
        private bool LoadCatalog()
        {
            _all.Clear();
            _byId.Clear();
            _chosen.Clear();

            try
            {
                var sessions = new SessionCatalog(_catalogPath).Load();
                _all.AddRange(sessions);
                foreach (var s in sessions) _byId[s.Id] = s;
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = Loc.Format(Loc.CatalogError, ex.Message);
                return false;
            }
        }

        private void Reload()
        {
            StatusText.Text = string.Empty;

            // Reload means "re-read whatever this view shows". Rescanning 50 MB of logs
            // on a session-list refresh would be absurd, so the two are split.
            if (_view == AppView.Projects) { ReloadProjects(); return; }

            if (!LoadCatalog())
            {
                TotalChip.Text = "—";
                return;
            }

            ApplyFilter();
            UpdateSelectionUi();
        }

        /// <summary>
        /// Run the search engine over the catalog and rebuild the visible rows.
        /// </summary>
        private void ApplyFilter()
        {
            var query = SearchBox.Text ?? string.Empty;
            var docs = _all.Select(ToSearchDoc).ToList();

            var results = SessionSearch.Search(docs, query, int.MaxValue);

            var rows = new List<SessionRow>(results.Count);
            foreach (var result in results)
            {
                var id = result.Hit.Doc.Id;
                if (!_byId.TryGetValue(id, out var info)) continue;

                rows.Add(SessionRow.From(
                    info, result.TitleRanges, result.DirectoryRanges, result.IsFuzzy));
            }

            foreach (var row in rows)
                row.PropertyChanged += OnRowPropertyChanged;

            // Restoring ticks here is safe without a reentrancy guard: assigning a value
            // equal to the row's current one raises no PropertyChanged, and the rows that
            // do change are already in _chosen, so the handler's add is a no-op.
            foreach (var row in rows)
                row.IsChosen = _chosen.Contains(row.Id);

            List.ItemsSource = rows;

            UpdateSelectionUi();
            UpdateSearchUi(results.Count);
        }

        private void UpdateSearchUi(int matched)
        {
            var query = (SearchBox.Text ?? string.Empty).Trim();
            SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearch.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            // Only touch the chip on the session view; the project view has its own
            // counts and this would overwrite them with a conversation count.
            if (_view == AppView.Sessions)
                TotalChip.Text = query.Length == 0
                    ? Loc.Format(Loc.Conversations, _all.Count)
                    : Loc.Format(Loc.StatusSearchCount, matched, _all.Count);
        }

        private static SearchDoc ToSearchDoc(SessionInfo s)
            => new(s.Id, s.DisplayTitle, s.Directory, s.Agent ?? string.Empty, s.Messages);

        // ---- selection --------------------------------------------------------

        /// <summary>Everything ticked, including rows the filter is currently hiding.</summary>
        private List<SessionInfo> SelectedSessions()
            => _all.Where(s => _chosen.Contains(s.Id)).ToList();

        private void UpdateSelectionUi()
        {
            // The project view has its own selection model: one highlighted row, not a
            // set of ticked conversations. Everything below describes the ticked
            // session list and would be wrong here.
            if (_view == AppView.Projects) { UpdateProjectSelectionUi(); return; }

            var picked = SelectedSessions();
            var n = picked.Count;

            SelectionText.Text = n switch
            {
                0 => Loc.T(Loc.NothingSelected),
                1 => Loc.Format(Loc.SelectedOne, picked[0].DisplayTitle),
                _ => Loc.Format(Loc.SelectedMany, n),
            };

            SelectionDetail.Text = n == 0
                ? Loc.T(Loc.TickHint)
                : Loc.Format(Loc.SelectedStats,
                             picked.Sum(p => p.Messages),
                             picked.Select(p => p.DirectoryLeaf).Distinct().Count())
                  + ShownSuffix();

            var has = n > 0;
            OpenChamberButton.IsEnabled = has;
            OpenOpencodeButton.IsEnabled = has;
            CopyButton.IsEnabled = has;
            ExplorerButton.IsEnabled = has;
            TerminalButton.IsEnabled = has;
            EditorButton.IsEnabled = has;
            ProjectButton.IsEnabled = has;
            OcProjectButton.IsEnabled = has;
            ClearSelectionButton.IsEnabled = has;
            SelectAllButton.IsEnabled = List.Items.Count > 0;
        }

        /// <summary>Summary line and button states for the project view.</summary>
        private void UpdateProjectSelectionUi()
        {
            var project = SelectedProject();
            var has = project is not null;

            SelectionText.Text = has
                ? project!.Name
                : Loc.T(Loc.NothingSelected);

            SelectionDetail.Text = has
                ? Loc.Format(Loc.ProjOpened, project!.Path)
                : Loc.T(Loc.TickHint);

            // Selecting a different project while a registration wait is running must NOT
            // re-enable the open-set button. It did, and that reopened the door the
            // wait had just closed: click a row, get told to quit OpenChamber, click
            // another row, press the button again.
            var waiting = _registrationWait is { IsWaiting: true };

            ProjectOcButton.IsEnabled = has && !waiting;
            ProjectExplorerButton.IsEnabled = has;
            ProjectTerminalButton.IsEnabled = has;
            ProjectEditorButton.IsEnabled = has;
            ProjectOpencodeButton.IsEnabled = has;
        }

        private string ShownSuffix()
        {
            var shown = List.Items.Count;
            return shown == _all.Count
                ? string.Empty
                : "  ·  " + Loc.Format(Loc.ShownSuffix, shown, _all.Count);
        }

        /// <summary>
        /// Sync the authoritative selection when a row's checkbox flips.
        /// </summary>
        /// <remarks>
        /// Driven by <see cref="INotifyPropertyChanged"/> rather than the checkbox's Click
        /// event: accessibility tools and UI automation set <c>IsChecked</c> directly and
        /// never raise Click, so a Click-only handler silently misses those.
        /// </remarks>
        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SessionRow.IsChosen)) return;
            if (sender is not SessionRow row) return;

            if (row.IsChosen) _chosen.Add(row.Id);
            else _chosen.Remove(row.Id);

            UpdateSelectionUi();
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            // "All" means the whole catalog, not just the visible rows: selecting from
            // inside a filter should not silently cap a batch at the filter's size.
            foreach (var s in _all) _chosen.Add(s.Id);
            foreach (var row in List.Items.Cast<SessionRow>()) row.IsChosen = true;

            StatusText.Text = Loc.Format(Loc.StatusSelectAll, _chosen.Count, List.Items.Count);
            UpdateSelectionUi();
        }

        private void OnClearSelectionClick(object sender, RoutedEventArgs e)
        {
            _chosen.Clear();
            foreach (var row in List.Items.Cast<SessionRow>()) row.IsChosen = false;

            StatusText.Text = Loc.T(Loc.StatusCleared);
            UpdateSelectionUi();
        }

        // ---- events -----------------------------------------------------------

        private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OnSelectAllClick(this, new RoutedEventArgs());
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = string.Empty;
                else OnClearSelectionClick(this, new RoutedEventArgs());
                return;
            }

            if (e.Key != Key.Enter) return;

            e.Handled = true;
            OpenInOpenChamber();
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not FrameworkElement fe ||
                fe.DataContext is not SessionRow clicked)
                return;

            // Double-click means "this one": collapse a multi-tick first.
            if (_chosen.Count > 1) SelectOnly(clicked);

            OpenInOpenChamber();
        }

        private void OnReloadClick(object sender, RoutedEventArgs e) => Reload();

        // ---- actions ----------------------------------------------------------

        private void OnOpenChamberClick(object sender, RoutedEventArgs e) => OpenInOpenChamber();

        /// <summary>
        /// OpenChamber shows one conversation at a time, so a multi-tick needs a winner.
        /// Most recently updated is the least surprising pick.
        /// </summary>
        private void OpenInOpenChamber()
        {
            Guard(() =>
            {
                var picked = SelectedSessions();
                if (picked.Count == 0) throw new InvalidOperationException(Loc.T(Loc.StatusPickFirst));

                var target = picked.OrderByDescending(s => s.Updated).First();
                _launcher.OpenInOpenChamber(target);

                StatusText.Text = picked.Count == 1
                    ? Loc.Format(Loc.StatusOpenedOne, target.DisplayTitle)
                    : Loc.Format(Loc.StatusOpenedMany, target.DisplayTitle, picked.Count);
            });
        }

        private void OnOpenOpencodeClick(object sender, RoutedEventArgs e)
        {
            Guard(() =>
            {
                var picked = SelectedSessions();
                if (picked.Count == 0) throw new InvalidOperationException(Loc.T(Loc.StatusPickFirst));

                if (picked.Count > MaxBatchOpen)
                    throw new InvalidOperationException(
                        Loc.Format(Loc.StatusBatchCap, picked.Count, MaxBatchOpen));

                // Staggered: each CLI process opens the same database, and starting a dozen
                // at once makes them race.
                for (var i = 0; i < picked.Count; i++)
                {
                    _launcher.OpenInOpencode(picked[i]);
                    if (i < picked.Count - 1) System.Threading.Thread.Sleep(400);
                }

                StatusText.Text = Loc.Format(Loc.StatusResumed, picked.Count);
            });
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            Guard(() =>
            {
                var picked = SelectedSessions();
                if (picked.Count == 0) throw new InvalidOperationException(Loc.T(Loc.StatusPickFirst));

                _launcher.CopyText(string.Join(Environment.NewLine, picked.Select(s => s.Id)));
                StatusText.Text = Loc.Format(Loc.StatusCopied, picked.Count);
            });
        }

        private void OnOpenExplorerClick(object sender, RoutedEventArgs e)
            => OpenEachIn("资源管理器", Loc.T(Loc.OpenExplorer), _launcher.OpenDirectoryPath);

        private void OnOpenTerminalClick(object sender, RoutedEventArgs e)
            => OpenEachIn("终端", Loc.T(Loc.OpenTerminal), _launcher.OpenTerminal);

        private void OnOpenEditorClick(object sender, RoutedEventArgs e)
            => OpenEachIn("编辑器", Loc.T(Loc.OpenEditor), _launcher.OpenEditor);

        /// <summary>
        /// Open every distinct project folder among the ticks, then report by target name.
        /// </summary>
        /// <remarks>
        /// The target has to be named in the status line: reusing one "opened N folders"
        /// string for all three made a terminal launch announce itself as Explorer.
        /// </remarks>
        private void OpenEachIn(string targetZh, string targetEn, Action<string> open)
            => Guard(() =>
            {
                var dirs = DistinctProjectPaths();
                var (opened, missing) = OpenEach(dirs, open);
                var target = Loc.Language == AppLang.ZhHans ? targetZh : targetEn;

                StatusText.Text = missing == 0
                    ? Loc.Format(Loc.StatusOpenedIn, target, opened)
                    : Loc.Format(Loc.StatusFoldersMissing, opened, missing);
            });

        /// <summary>
        /// Start an opencode TUI rooted at the project, so its session list is that
        /// project's rather than the global one.
        /// </summary>
        private void OnOpenProjectClick(object sender, RoutedEventArgs e) => Guard(() =>
        {
            var dirs = DistinctProjectPaths();

            // One TUI only: opening one window per project would be unusable.
            var target = dirs[0];
            _launcher.OpenProjectInOpencode(target);

            StatusText.Text = dirs.Count == 1
                ? Loc.Format(Loc.StatusProjectOpened, target)
                : Loc.Format(Loc.StatusProjectFirstOfMany, target, dirs.Count);
        });

        // ---- helpers ----------------------------------------------------------

        private List<string> DistinctProjectPaths()
        {
            var dirs = SelectedSessions()
                .Select(s => s.ProjectPath)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dirs.Count == 0) throw new InvalidOperationException(Loc.T(Loc.StatusNoFolder));
            return dirs;
        }

        private static (int Opened, int Missing) OpenEach(
            List<string> paths, Action<string> open)
        {
            var opened = 0;
            var missing = 0;

            for (var i = 0; i < paths.Count; i++)
            {
                try
                {
                    open(paths[i]);
                    opened++;
                }
                catch (DirectoryNotFoundException)
                {
                    missing++;
                    continue;
                }

                if (i < paths.Count - 1) System.Threading.Thread.Sleep(250);
            }

            return (opened, missing);
        }

        /// <summary>Reduce the ticks to a single row.</summary>
        private void SelectOnly(SessionRow keep)
        {
            _chosen.Clear();
            _chosen.Add(keep.Id);

            foreach (var row in List.Items.Cast<SessionRow>())
                row.IsChosen = ReferenceEquals(row, keep);

            UpdateSelectionUi();
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        // ---- project view ------------------------------------------------------

        /// <summary>
        /// Sort modes paired with their localised labels, in COMBO order.
        /// </summary>
        /// <remarks>
        /// Not enum order, deliberately. UnregisteredFirst leads the combo because it
        /// is the order most people want on first run, while staying LAST in the enum
        /// so the persisted integer for LastUsed keeps its meaning.
        /// </remarks>
        private static readonly (ProjectSortMode Mode, string Key)[] ProjectSortModes =
        {
            // First in the combo, but NOT the app default: AppSettings.ProjectSort
            // stays LastUsed so an existing saved choice is untouched. This is
            // offered, one click away, rather than imposed on someone who has
            // already picked an order.
            (ProjectSortMode.UnregisteredFirst, Loc.ProjSortUnregisteredFirst),
            (ProjectSortMode.LastUsed, Loc.ProjSortLastUsed),
            (ProjectSortMode.LeastUsed, Loc.ProjSortLeastUsed),
            (ProjectSortMode.MostVisited, Loc.ProjSortMostVisited),
            (ProjectSortMode.MostActive, Loc.ProjSortMostActive),
            (ProjectSortMode.NameAsc, Loc.ProjSortNameAsc),
            (ProjectSortMode.NameDesc, Loc.ProjSortNameDesc),
            (ProjectSortMode.Oldest, Loc.ProjSortOldest),
            (ProjectSortMode.Newest, Loc.ProjSortNewest),
            (ProjectSortMode.PathDepth, Loc.ProjSortPathDepth),
        };

        private void PopulateProjectSortBox()
        {
            var choices = ProjectSortModes
                .Select(x => new SortChoice(x.Mode, Loc.T(x.Key)))
                .ToList();

            ProjectSortBox.ItemsSource = choices;

            // SelectedItem is assigned after InitializeComponent in the constructor,
            // and it is matched by reference, so the item has to be this instance.
            ProjectSortBox.SelectedItem =
                choices.FirstOrDefault(c => c.Mode == _projectSort) ?? choices[0];
        }

        /// <summary>Show one of the two lists and hide the other's chrome.</summary>
        /// <remarks>
        /// The counter chip in the header is view-specific: it reports matched sessions
        /// on the session list but project count on the project list. Leaving it on the
        /// session count made the project view read "0 conversations" while 132
        /// projects were on screen.
        /// </remarks>
        private void ApplyView()
        {
            // The filter hint is a sibling of the text box, so it must be hidden
            // explicitly; a collapsed element still shows its own content otherwise.
            ProjectSearchHint.Visibility =
                string.IsNullOrEmpty(ProjectSearchBox.Text) && !_initializing
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            var projects = _view == AppView.Projects;

            // Collapse the PANELS, not the ListViews inside them. The two views share
            // one Grid cell, so a panel left Visible is a full-size, opaque,
            // hit-testable rectangle stacked on top of the other one. In the project view
            // that meant the session panel covered ProjectPanel and ate every click
            // while showing nothing - which is exactly what "the whole project screen
            // looks like it never loaded" was. Hiding only List left that behind.
            ProjectPanel.Visibility = projects ? Visibility.Visible : Visibility.Collapsed;
            SessionPanel.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;

            // Keep the inner ListView in step. A Collapsed ListView still reports its
            // Items, which the filter paths rely on, so this is cosmetic rather than
            // load-bearing; it just stops a stale Visible list from lingering inside a
            // collapsed panel.
            List.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;

            // These rows are the action bar's halves; only one set can be meaningful.
            ConversationActionRow.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;
            ProjectActionRow.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;
            ProjectOpenRow.Visibility = projects ? Visibility.Visible : Visibility.Collapsed;

            // The session toolbar holds the catalog search box, which filters the
            // session list. The project panel has its own filter, so showing both
            // would put a search box on screen wired to data that is not displayed.
            SessionToolbar.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;

            UpdateTotalChip();
            UpdateSelectionUi();

        }

        /// <summary>Header counter, reflecting whichever list is showing.</summary>
        private void UpdateTotalChip()
        {
            if (_view == AppView.Projects && _projects.Count > 0)
            {
                var shown = ProjectList.Items.Count;
                // ProjectRow exposes the flag as IsMissing, not Exists: Exists lives on
                // the underlying ProjectEntry and the row flips the sense of it.
                var total = _projects.Count(p => p.DirectoryExists || !_hideMissing);
                TotalChip.Text = shown == total
                    ? total.ToString()
                    : Loc.Format(Loc.ShownSuffix, shown, total);
                return;
            }

            TotalChip.Text = (_all.Count == 0 ? "—" : _all.Count.ToString());
        }

        private void OnViewSessionsClick(object sender, RoutedEventArgs e) => SwitchView(AppView.Sessions);

        private void OnViewProjectsClick(object sender, RoutedEventArgs e) => SwitchView(AppView.Projects);

        private void SwitchView(AppView view)
        {
            if (_view == view) return;

            _view = view;
            _settings.View = view;
            _settings.Save();

            ApplyView();

            if (view == AppView.Projects)
            {
                ReloadProjects();
                return;
            }

            // The catalog is read at startup either way, so _all is populated even when
            // the app opened on the project view. Reload only if it somehow is not.
            if (_all.Count == 0) Reload();
            else ApplyFilter();
        }

        /// <summary>
        /// Rebuild the project list by grouping the catalog's sessions into projects.
        /// </summary>
        /// <remarks>
        /// Grouped from the CATALOG, not from opencode's logs. A project is "a set of
        /// sessions that share a directory", so the sessions themselves are the source.
        /// The log scan found 135 directories, most of them long dead, which is not what
        /// a project list is; grouping the catalog gives one row per project with its
        /// sessions attached, which is what opening a project needs.
        /// <para>
        /// Colours are honoured from OpenChamber when it already has one for the path,
        /// so the same project looks the same in both apps. That is a READ of their
        /// settings file, never a write.
        /// </para>
        /// </remarks>
        private void ReloadProjects()
        {
            // One read of OpenChamber's settings serves two purposes: the colour a
            // project already has there, and whether it is there at all. They are
            // read together because a failure must degrade both the same way.
            Dictionary<string, string>? colors = null;
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var theirs = OpenChamberBridge.ReadProjects()
                    .Where(p => p.Path is not null)
                    .ToList();

                colors = theirs
                    .GroupBy(p => ProjectCatalog.Normalize(p.Path), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Color ?? string.Empty,
                                  StringComparer.OrdinalIgnoreCase);

                // Empty strings are dropped, so a registered project with no colour
                // still counts as registered. IsRegistered's own normalisation runs
                // again per project; doing it once here would be a second source of
                // truth for what counts as the same folder.
                foreach (var p in theirs)
                {
                    var key = ProjectCatalog.Normalize(p.Path);
                    if (key.Length > 0) registered.Add(key);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                          or UnauthorizedAccessException)
            {
                // An unreadable settings file leaves every project unregistered, which
                // is honest: we do not know that OpenChamber has it. It is not a throw,
                // because this runs during Load and the launcher is still useful
                // without the marks.
                colors = null;
            }

            _projects = ProjectCatalog.Group(_all, colors)
                                     .Select(p => new ProjectRow(p)
                                     {
                                         IsRegistered = registered.Contains(
                                             ProjectCatalog.Normalize(p.Path)),
                                     })
                                     .ToList();

            ApplyProjectFilter(_projectFilter, _projectSort, _hideMissing);
        }

        /// <summary>Filter, sort and bind the project rows.</summary>
        private void ApplyProjectFilter(
            string? filter, ProjectSortMode sort, bool hideMissing)
        {
            IEnumerable<ProjectRow> rows = _projects;

            if (hideMissing) rows = rows.Where(p => p.DirectoryExists);

            var text = (filter ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                rows = rows.Where(p =>
                    p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    p.Path.Contains(text, StringComparison.OrdinalIgnoreCase));
            }

            // ProjectSort orders ProjectEntry records, so map back onto the rows after.
            // The set is normalised here because ProjectEntry.Path is already
            // normalised but the sort's contract is "normalised paths".
            var registered = rows
                .Select(p => ProjectCatalog.Normalize(p.Path))
                .Where(k => k.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ordered = ProjectSort.Apply(rows.Select(p => p.Entry), sort, registered);
            var byPath = _projects
                .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<ProjectRow>();
            foreach (var entry in ordered)
                if (byPath.TryGetValue(entry.Path, out var row)) result.Add(row);

            ProjectList.ItemsSource = result;

            var hidden = _projects.Count(p => !p.DirectoryExists);
            ProjectStatus.Text = result.Count == 0
                ? Loc.T(Loc.ProjNone)
                : hidden > 0 && hideMissing
                    ? Loc.Format(Loc.ProjCountHidden, result.Count, hidden)
                    : Loc.Format(Loc.ProjCount, result.Count);

            UpdateTotalChip();
            UpdateSelectionUi();
        }

        private void OnProjectSearchChanged(object sender, TextChangedEventArgs e)
        {
            // TextChanged also fires while the tree is being built.
            if (_initializing) return;

            _projectFilter = ProjectSearchBox.Text ?? string.Empty;
            ProjectSearchHint.Visibility = string.IsNullOrEmpty(_projectFilter)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyProjectFilter(_projectFilter, _projectSort, _hideMissing);
        }

        private void OnProjectSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSortSave) return;
            if (ProjectSortBox.SelectedItem is not SortChoice choice) return;

            _projectSort = choice.Mode;
            _settings.ProjectSort = choice.Mode;
            _settings.Save();

            ApplyProjectFilter(_projectFilter, _projectSort, _hideMissing);
        }

        private void OnHideMissingChanged(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent for the XAML-declared IsChecked="True".
            if (_initializing) return;

            _hideMissing = HideMissingCheck.IsChecked == true;
            _settings.HideMissingProjects = _hideMissing;
            _settings.Save();

            ApplyProjectFilter(_projectFilter, _projectSort, _hideMissing);
        }

        /// <summary>
        /// The project row the user picked, if any.
        /// </summary>
        /// <remarks>
        /// Read from the row CONTAINER, not from <c>ProjectList.SelectedItem</c>: the
        /// items are <see cref="ProjectRow"/> instances, and WPF wraps each in a
        /// ListViewItem whose DataContext is the row. When a caller drives the list
        /// through UIA (SelectionItemPattern.Select), SelectedItem is not updated but
        /// the container selection is, so reading SelectedItem left the action buttons
        /// permanently disabled.
        /// </remarks>
        private ProjectRow? SelectedProject()
        {
            if (ProjectList.SelectedItem is ProjectRow direct) return direct;

            // ItemContainerGenerator is the abstract base, and it does expose both
            // Status and ContainerFromIndex. It does NOT have HasContainers (that was
            // the first guess, and it does not compile); Status is the right test and
            // also covers the pre-layout case. Do NOT cast to
            // IItemContainerGenerator: that interface is the panel-layout one and has
            // neither member.
            var generator = ProjectList.ItemContainerGenerator;
            if (generator.Status != GeneratorStatus.ContainersGenerated) return null;

            for (var i = 0; i < ProjectList.Items.Count; i++)
            {
                var container = generator.ContainerFromIndex(i) as ListViewItem;
                if (container?.IsSelected == true) return container.DataContext as ProjectRow;
            }

            return null;
        }

        private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires while the tree is being built, before the status elements exist.
            if (_initializing) return;
            UpdateProjectSelectionUi();
        }

        /// <summary>
        /// Double-clicking a project row opens its sessions in OpenChamber.
        /// </summary>
        /// <remarks>
        /// This used to launch an opencode TUI rooted at the project, which was both the
        /// wrong action and the one that produced the "a cmd window flashes and dies"
        /// report: <c>opencode</c> on PATH is <c>opencode.cmd</c>, an npm shim, and this
        /// machine's <c>opencode.jsonc</c> carries an unrecognised <c>plugins</c> key
        /// next to the correct <c>plugin</c>, so the process printed a config error and
        /// exited at once. Double-click is the natural gesture for "open this project",
        /// and opening the project is what this button now does.
        /// </remarks>
        private void OnProjectDoubleClick(object sender, MouseButtonEventArgs e)
            => Guard(() =>
            {
                // Double-click does not consult ProjectOcButton.IsEnabled, so it would
                // otherwise be a way around the guard the wait installs.
                if (_registrationWait is { IsWaiting: true })
                {
                    StatusText.Text = Loc.T(Loc.OcWaitAlreadyRunning);
                    return;
                }

                OpenProjectSet(
                    SelectedProject() ?? throw new InvalidOperationException(Loc.T(Loc.ProjNoSelection)));
            });

        // These are wrapped in lambdas rather than passed as method groups: Guard takes
        // an Action, and OpenSelectedProject is a two-argument void method, so there is
        // no group conversion to hand it directly.
        private void OnProjectOpenExplorerClick(object sender, RoutedEventArgs e)
            => Guard(() => OpenSelectedProject(p => _launcher.OpenDirectoryPath(p.Path),
                                               Loc.T(Loc.OpenExplorer)));

        private void OnProjectOpenTerminalClick(object sender, RoutedEventArgs e)
            => Guard(() => OpenSelectedProject(p => _launcher.OpenTerminal(p.Path),
                                               Loc.T(Loc.OpenTerminal)));

        private void OnProjectOpenEditorClick(object sender, RoutedEventArgs e)
            => Guard(() => OpenSelectedProject(p => _launcher.OpenEditor(p.Path),
                                               Loc.T(Loc.OpenEditor)));

        private void OnProjectOpenInOpencodeClick(object sender, RoutedEventArgs e)
            => Guard(() => OpenSelectedProject(p => _launcher.OpenProjectInOpencode(p.Path),
                                               "opencode"));

        // ---- OpenChamber: opening a SET of sessions ---------------------------
        //
        // The previous "select project" button is gone, but its write path is not:
        // RegisterAndLaunch calls OpenChamberBridge.Activate, which is the same
        // read-modify-write against a file a live Electron process owns. What made it
        // safe to keep is that it now runs only once OpenChamber is confirmed gone,
        // it takes a backup first, and a write that fails verification is rolled back
        // from that backup rather than merely reported.

        /// <summary>
        /// Open the ticked conversations in OpenChamber as one set.
        /// </summary>
        /// <remarks>
        /// The ticked conversations ARE the set, so this names the project that contains
        /// them rather than dispatching a deep link per session. See
        /// <see cref="LauncherService.OpenSessionSetInOpenChamber"/> for what OpenChamber
        /// does with that, and why a per-session dispatch was tried and dropped.
        /// </remarks>
        private void OnOpenProjectSetClick(object sender, RoutedEventArgs e)
            => Guard(() =>
            {
                var picked = SelectedSessions();
                if (picked.Count == 0) throw new InvalidOperationException(Loc.T(Loc.StatusPickFirst));

                // Newest first: the launcher opens the first id, and the newest is the
                // one worth landing on. SelectedSessions is already newest-first, so the
                // order is not reversed here.
                var ok = _launcher.OpenSessionSetInOpenChamber(picked.Select(s => s.Id).ToList());
                var label = DescribeSet(picked.Select(s => s.DirectoryLeaf));

                StatusText.Text = ok
                    ? Loc.Format(Loc.OcOpenedSet, label, picked.Count)
                    : Loc.Format(Loc.OcOpenFailed, label);
            });

        /// <summary>
        /// Name a set of sessions by the projects it spans: the single project name when
        /// they all sit in one, otherwise the first plus a count.
        /// </summary>
        private static string DescribeSet(IEnumerable<string> directories)
        {
            var dirs = directories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dirs.Count == 0) return Loc.T(Loc.OcSelection);
            if (dirs.Count == 1) return dirs[0];
            return $"{dirs[0]} +{dirs.Count - 1}";
        }

        private void OnProjectOpenSetClick(object sender, RoutedEventArgs e)
            => Guard(() => OpenProjectSet(
                SelectedProject() ?? throw new InvalidOperationException(Loc.T(Loc.ProjNoSelection))));

        /// <summary>
        /// Open a project's sessions in OpenChamber.
        /// </summary>
        /// <remarks>
        /// 工作区 and 项目 are the same action here, and that is not a shortcut: in
        /// OpenChamber both are containers of sessions, and no verb selects one rather
        /// than the other. Only the membership differs, so the UI offers this once
        /// instead of two buttons that do the same thing.
        /// </remarks>
        private void OpenProjectSet(ProjectRow project)
        {
            if (project.SessionCount == 0)
                throw new InvalidOperationException(Loc.T(Loc.WsNone));

            switch (ProjectRegistrationWait.Decide(
                       project.IsRegistered, LauncherService.IsOpenChamberRunning()))
            {
                case RegistrationAction.OpenByDeepLink:
                    OpenProjectByDeepLink(project);
                    break;

                case RegistrationAction.RegisterThenLaunch:
                    RegisterAndLaunch(project);
                    break;

                case RegistrationAction.WaitForExit:
                    WaitForOpenChamberThenRegister(project);
                    break;
            }
        }

        /// <summary>
        /// Open a project's sessions with one deep link, writing nothing.
        /// </summary>
        private void OpenProjectByDeepLink(ProjectRow project)
        {
            var ok = _launcher.OpenSessionSetInOpenChamber(project.SessionIds);

            StatusText.Text = ok
                ? Loc.Format(Loc.OcOpenedSet, project.Name, project.SessionCount)
                : Loc.Format(Loc.OcOpenFailed, project.Name);
        }

        /// <summary>
        /// Register a project in OpenChamber and start it on the result.
        /// </summary>
        /// <remarks>
        /// Every step here can fail, and each failure mode is different:
        /// <list type="bullet">
        /// <item>The re-check below closes the window between deciding the project was
        /// unregistered and writing the file. An updater restarting OpenChamber is
        /// exactly that window, and it is the same hazard the two-consecutive-zeros
        /// rule exists for.</item>
        /// <item>The backup is taken before the write, not after.</item>
        /// <item>A write that fails verification is <b>rolled back</b> from that backup.
        /// Verification runs after the damage, so on its own it is a report and not a
        /// repair; the restore is what makes the backup worth taking.</item>
        /// </list>
        /// </remarks>
        private void RegisterAndLaunch(ProjectRow project)
        {
            // Decided when the row was clicked, possibly minutes ago. An updater can
            // have restarted OpenChamber in between, and writing then is the exact
            // read-modify-write against a live process this whole path avoids.
            if (LauncherService.IsOpenChamberRunning())
            {
                StatusText.Text = Loc.Format(Loc.OcRestartedWhileWaiting, project.Name);
                return;
            }

            var settings = OpenChamberBridge.ResolveSettingsPath()
                ?? throw new FileNotFoundException(
                    "OpenChamber settings.json was not found; is OpenChamber installed?");

            // Counted BEFORE the write. VerifyRegistered can only prove nothing was
            // lost if it is told what "nothing" was, and it only runs at all if this
            // line succeeded — which is why it comes first and not after the write.
            var before = OpenChamberBridge.CountTopLevelKeys(
                File.ReadAllText(settings, Encoding.UTF8));

            // The specific file, not the directory: it is the state of the document as it
            // was immediately before THIS write, which is what a rollback needs.
            var backup = OpenChamberBridge.BackupSettings(settings);

            string id;
            try
            {
                id = OpenChamberBridge.Activate(project.Path, project.Name, project.Entry.ColorKey);
                OpenChamberBridge.VerifyRegistered(settings, id, before);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                                          or UnauthorizedAccessException)
            {
                // Put the file back. A write that failed verification has already
                // replaced the user's settings, and reporting that is not the same as
                // undoing it — the backup exists precisely for this moment.
                var undone = OpenChamberBridge.RestoreSettings(backup, settings);

                StatusText.Text = Loc.Format(
                    undone ? Loc.OcRegisterRolledBack : Loc.OcRegisterFailed,
                    project.Name, backup);
                return;
            }

            _launcher.OpenChamberApp();
            StatusText.Text = Loc.Format(Loc.OcRegisteredSet, project.Name, project.SessionCount);
        }

        /// <summary>
        /// Ask the user to quit OpenChamber, then register and relaunch once it has.
        /// </summary>
        /// <remarks>
        /// Closing OpenChamber's window is NOT enough and the message says so. Its
        /// close handler hides the window to the tray and the process survives
        /// (main.mjs:2193), so a user who closes the window would otherwise sit here
        /// until the timeout with no idea why nothing was happening.
        /// </remarks>
        private void WaitForOpenChamberThenRegister(ProjectRow project)
        {
            // Refuse a SECOND wait rather than replacing the first. Replacing it
            // looked harmless and was not: disposing the old wait resumes its
            // continuation, which posts to the dispatcher and runs AFTER this handler
            // returns — by which time the new wait exists. The stale callback then
            // ended the NEW wait and reported a timeout for a wait that had not
            // timed out, and nothing then registered when the user did what they were
            // told. One wait at a time, and re-entry is refused.
            if (_registrationWait is { IsWaiting: true })
            {
                StatusText.Text = Loc.T(Loc.OcWaitAlreadyRunning);
                return;
            }

            _registrationWaitCancelled = false;

            var wait = new ProjectRegistrationWait(
                ProjectRegistrationWait.DefaultPollInterval,
                ProjectRegistrationWait.DefaultTimeout);
            _registrationWait = wait;

            StatusText.Text = Loc.Format(Loc.OcWaitingQuit, project.Name);
            CancelWaitButton.Visibility = Visibility.Visible;
            ProjectOcButton.IsEnabled = false;

            // The await resumes on a pool thread, so every touch of a WPF element
            // goes back through the dispatcher. Nothing here touches the UI directly.
            _ = wait.WaitForExitAsync(CancellationToken.None).ContinueWith(
                completed =>
                {
                    var exited = completed.Status == TaskStatus.RanToCompletion && completed.Result;
                    var cancelled = _registrationWaitCancelled;

                    Dispatcher.InvokeAsync(() =>
                    {
                        // Closed while waiting: the user cannot see a registration
                        // happen, so do not perform one behind their back.
                        if (_closing) return;

                        // Only tear down a wait that is still OURS. Without this a
                        // superseded callback would end whatever wait has since taken
                        // its place.
                        if (!ReferenceEquals(_registrationWait, wait)) return;

                        EndRegistrationWait();

                        if (exited) RegisterAndLaunch(project);
                        else
                        {
                            StatusText.Text = cancelled
                                ? Loc.T(Loc.OcWaitCancelled)
                                : Loc.T(Loc.OcWaitTimedOut);
                        }
                    });
                },
                TaskScheduler.Default);
        }

        /// <summary>
        /// Undo everything <see cref="WaitForOpenChamberThenRegister"/> set up.
        /// </summary>
        /// <remarks>
        /// One method for every exit path — success, cancel, timeout — because the
        /// button left on screen after a successful registration reads as "still
        /// waiting", and the action button left disabled is a dead control.
        /// </remarks>
        private void EndRegistrationWait()
        {
            CancelWaitButton.Visibility = Visibility.Collapsed;
            ProjectOcButton.IsEnabled = true;

            _registrationWait?.Dispose();
            _registrationWait = null;
        }

        private void OnCancelRegistrationWaitClick(object sender, RoutedEventArgs e)
        {
            _registrationWaitCancelled = true;
            _registrationWait?.Cancel();
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _closing = true;
            _registrationWait?.Dispose();
            _registrationWait = null;
        }

        /// <summary>Run <paramref name="open"/> on the selected project, reporting by name.</summary>
        private void OpenSelectedProject(Action<ProjectRow> open, string target)
        {
            var project = SelectedProject() ?? throw new InvalidOperationException(Loc.T(Loc.ProjNoSelection));

            // Report a deleted directory before shelling out: Explorer and the editor
            // would either fail obscurely or silently create something.
            if (!project.DirectoryExists)
                throw new DirectoryNotFoundException(Loc.Format(Loc.ProjMissing, project.Path));

            open(project);
            StatusText.Text = Loc.Format(Loc.StatusOpenedIn, target, 1);
        }

        /// <summary>
        /// Combo entry pairing a sort mode with its localised label.
        /// </summary>
        /// <remarks>
        /// Not a record. A record's compiler-generated <c>PrintMembers</c> and the
        /// equality members made WPF pick the wrong member for display, and the closed
        /// combo rendered empty. A plain class with a real property named exactly
        /// <c>Label</c> is what the template's DisplayMemberPath binds to.
        /// </remarks>
        private sealed class SortChoice
        {
            public SortChoice(ProjectSortMode mode, string label)
            {
                Mode = mode;
                Label = label;
            }

            public ProjectSortMode Mode { get; }

            public string Label { get; }

            public override string ToString() => Label;
        }
    }

    /// <summary>
    /// List-row view model. Wraps the search hit so the checkbox, the highlight ranges and
    /// the formatted timestamp can bind, while <see cref="SessionInfo"/> stays a plain
    /// record shared with the non-UI code.
    /// </summary>
    internal sealed class SessionRow : INotifyPropertyChanged
    {
        private bool _isChosen;

        public required SessionInfo Session { get; init; }

        public string Id => Session.Id;
        public int Messages => Session.Messages;
        public string DisplayTitle => Session.DisplayTitle;
        public string DirectoryLeaf => Session.DirectoryLeaf;
        public DateTimeOffset Updated => Session.Updated;

        public string UpdatedText =>
            Session.Updated == DateTimeOffset.MinValue
                ? "—"
                : Session.Updated.ToString("yyyy-MM-dd HH:mm");

        /// <summary>
        /// Creation time, or an em dash when the catalog carries none — which is
        /// every session in a catalog generated before the column existed.
        /// </summary>
        public string CreatedText =>
            Session.Created == DateTimeOffset.MinValue
                ? "—"
                : Session.Created.ToString("yyyy-MM-dd HH:mm");

        /// <summary>True when the match was a subsequence hit rather than a literal one.</summary>
        public bool IsFuzzy { get; init; }

        /// <summary>Literal match offsets in the title, for highlighting.</summary>
        public IReadOnlyList<Range> TitleRanges { get; init; } = Array.Empty<Range>();

        /// <summary>Literal match offsets in the directory, for highlighting.</summary>
        public IReadOnlyList<Range> DirectoryRanges { get; init; } = Array.Empty<Range>();

        /// <summary>Ticked by the user. Drives both the checkbox and the row highlight.</summary>
        public bool IsChosen
        {
            get => _isChosen;
            set
            {
                if (_isChosen == value) return;
                _isChosen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChosen)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static SessionRow From(
            SessionInfo info, IReadOnlyList<Range>? titleRanges,
            IReadOnlyList<Range>? directoryRanges, bool isFuzzy) => new()
        {
            Session = info,
            TitleRanges = SessionSearch.Merge(titleRanges ?? Array.Empty<Range>()),
            DirectoryRanges = SessionSearch.Merge(directoryRanges ?? Array.Empty<Range>()),
            IsFuzzy = isFuzzy,
        };
    }
}

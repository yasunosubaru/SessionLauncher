using System;
using System.Collections.Generic;
using System.Globalization;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services
{
    /// <summary>
    /// UI strings for both languages.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain in-code table rather than .resx: no satellite-assembly
    /// build step, no culture-fallback surprises, and the whole vocabulary is visible
    /// in one place. The Chinese text is the key, so a missing translation is obvious.
    /// <para>
    /// Formatting goes through <see cref="Format"/> with named or positional
    /// placeholders rather than <c>string.Format</c>, because user-visible text and
    /// argument order routinely disagree between the two languages.
    /// </para>
    /// </remarks>
    public static class Loc
    {
        // ---- key constants (avoids typo-driven silent fallbacks) --------------

        public const string AppTitle = "app.title";
        public const string Catalog = "app.catalog";
        public const string Conversations = "app.conversations";
        public const string CatalogError = "app.catalogError";

        public const string SearchPlaceholder = "search.placeholder";
        public const string ClearSearchTip = "search.clearTip";
        public const string SelectAll = "btn.selectAll";
        public const string ClearSelection = "btn.clearSelection";
        public const string Reload = "btn.reload";

        public const string Pick = "col.pick";
        public const string ColUpdated = "col.updated";
        public const string ColCreated = "col.created";
        public const string ColMsgs = "col.msgs";
        public const string ColTitle = "col.title";
        public const string ColFolder = "col.folder";

        public const string NothingSelected = "sel.none";
        public const string SelectedOne = "sel.one";
        public const string SelectedMany = "sel.many";
        public const string SelectedStats = "sel.stats";
        public const string ShownSuffix = "sel.shownSuffix";
        public const string TickHint = "sel.tickHint";

        public const string OpenChamber = "act.openChamber";
        public const string OpenOpencode = "act.openOpencode";
        public const string CopyIds = "act.copyIds";
        public const string OpenFolders = "act.openFolders";

        public const string ProjectGroup = "act.projectGroup";
        public const string OpenExplorer = "act.openExplorer";
        public const string OpenTerminal = "act.openTerminal";
        public const string OpenEditor = "act.openEditor";
        public const string OpenProject = "act.openProject";
        public const string OpenWorkspaceNote = "act.workspaceNote";
        public const string OpenWorkspace = "act.openWorkspace";
        public const string WorkspaceTip = "act.workspaceTip";
        public const string OpenInOpenChamber = "act.openInOpenChamber";
        public const string OpenSet = "act.openSet";
        public const string OpenSetTip = "act.openSetTip";

        public const string FontBigger = "act.fontBigger";
        public const string FontSmaller = "act.fontSmaller";
        public const string FontReset = "act.fontReset";
        public const string FontPercent = "act.fontPercent";
        public const string LanguageLabel = "act.language";

        // ---- view switch + project list -------------------------------------
        public const string ViewSessions = "view.sessions";
        public const string ViewProjects = "view.projects";

        public const string ProjSortBy = "proj.sortBy";
        public const string ProjHideMissing = "proj.hideMissing";
        public const string ProjColName = "proj.colName";
        public const string ProjColLast = "proj.colLast";
        public const string ProjColVisits = "proj.colVisits";
        public const string ProjColLines = "proj.colLines";
        public const string ProjColExists = "proj.colExists";
        public const string ProjColPath = "proj.colPath";
        public const string ProjOpenGroup = "proj.openGroup";
        public const string ProjOpenExplorer = "proj.openExplorer";
        public const string ProjOpenInOpencode = "proj.openInOpencode";

        // Sort-mode labels, in enum order.
        public const string ProjSortLastUsed = "proj.sortLastUsed";
        public const string ProjSortLeastUsed = "proj.sortLeastUsed";
        public const string ProjSortMostVisited = "proj.sortMostVisited";
        public const string ProjSortMostActive = "proj.sortMostActive";
        public const string ProjSortNameAsc = "proj.sortNameAsc";
        public const string ProjSortNameDesc = "proj.sortNameDesc";
        public const string ProjSortOldest = "proj.sortOldest";
        public const string ProjSortNewest = "proj.sortNewest";
        public const string ProjSortPathDepth = "proj.sortPathDepth";

        public const string ProjLoading = "proj.loading";
        public const string ProjNoLogDir = "proj.noLogDir";
        public const string ProjScanning = "proj.scanning";
        public const string ProjCount = "proj.count";
        public const string ProjCountHidden = "proj.countHidden";
        public const string ProjNone = "proj.none";
        public const string ProjNoSelection = "proj.noSelection";
        public const string ProjMissing = "proj.missing";
        public const string ProjOpened = "proj.opened";
        public const string ProjYes = "proj.yes";
        public const string ProjNo = "proj.no";

        // ---- workspace ----
        public const string WsOpening = "ws.opening";
        public const string WsOpened = "ws.opened";
        public const string WsCreated = "ws.created";
        public const string WsFound = "ws.found";
        public const string WsNone = "ws.none";
        public const string WsNoCode = "ws.noCode";
        public const string WsStatusOne = "ws.statusOne";
        public const string WsStatusMany = "ws.statusMany";
        public const string WsStatusFound = "ws.statusFound";

        // ---- openchamber project / workspace ----
        public const string OcOpening = "oc.opening";
        public const string OcActivated = "oc.activated";
        public const string OcAdded = "oc.added";
        public const string OcWorkspace = "oc.workspace";
        public const string OcNoSettings = "oc.noSettings";
        public const string OcNotRunning = "oc.notRunning";
        public const string OcOpenFailed = "oc.openFailed";
        public const string OcOpenedSet = "oc.openedSet";
        public const string OcSelection = "oc.selection";
        public const string ProjColSessions = "proj.colSessions";
        public const string ProjColCreated = "proj.colCreated";

        public const string StatusOpenedOne = "st.openedOne";
        public const string StatusOpenedMany = "st.openedMany";
        public const string StatusCopied = "st.copied";
        public const string StatusResumed = "st.resumed";
        public const string StatusFolders = "st.folders";
        public const string StatusFoldersMissing = "st.foldersMissing";
        public const string StatusOpenedIn = "st.openedIn";
        public const string StatusSelectAll = "st.selectAll";
        public const string StatusCleared = "st.cleared";
        public const string StatusPickFirst = "st.pickFirst";
        public const string StatusBatchCap = "st.batchCap";
        public const string StatusNoFolder = "st.noFolder";
        public const string StatusEditorMissing = "st.editorMissing";
        public const string StatusTerminalMissing = "st.terminalMissing";
        public const string StatusProjectOpened = "st.projectOpened";
        public const string StatusProjectFirstOfMany = "st.projectFirstOfMany";
        public const string StatusProjectMissing = "st.projectMissing";
        public const string StatusSearchCount = "st.searchCount";

        // ---- state -----------------------------------------------------------

        private static readonly Dictionary<AppLang, Dictionary<string, string>> Tables =
            new()
            {
                [AppLang.ZhHans] = new()
                {
                    [AppTitle] = "Session Launcher",
                    [Catalog] = "会话目录",
                    [Conversations] = "{0} 条会话",
                    [CatalogError] = "无法读取会话目录：{0}",

                    [SearchPlaceholder] = "按标题、ID 或文件夹筛选…",
                    [ClearSearchTip] = "清除筛选",
                    [SelectAll] = "全选",
                    [ClearSelection] = "取消选择",
                    [Reload] = "刷新",

                    [Pick] = "选",
                    [ColUpdated] = "更新时间",
                    [ColMsgs] = "消息数",
                    [ColTitle] = "标题",
                    [ColFolder] = "文件夹",
                    [ColCreated] = "创建时间",

                    [NothingSelected] = "未选择任何会话",
                    [SelectedOne] = "已选 1 条 — {0}",
                    [SelectedMany] = "已选 {0} 条",
                    [SelectedStats] = "{0} 条消息 · {1} 个文件夹",
                    [ShownSuffix] = "{0} / {1} 显示",
                    [TickHint] = "勾选左侧复选框可多选",

                    [OpenChamber] = "在 OpenChamber 中打开",
                    [OpenOpencode] = "在 opencode 中打开",
                    [CopyIds] = "复制 ID",
                    [OpenFolders] = "打开文件夹",

                    [ProjectGroup] = "项目 / 工作区",
                    [OpenExplorer] = "资源管理器",
                    [OpenTerminal] = "终端",
                    [OpenEditor] = "编辑器",
                    [OpenProject] = "在 opencode 中打开项目",
                    [OpenWorkspaceNote] = "OpenChamber 没有按项目跳转的深链，只能手动切到对应文件夹",

                    [FontBigger] = "A+",
                    [FontSmaller] = "A-",
                    [FontReset] = "A",
                    [FontPercent] = "字体 {0}%",
                    [LanguageLabel] = "EN",

                    [StatusOpenedOne] = "已在 OpenChamber 中打开「{0}」。",
                    [StatusOpenedMany] =
                        "OpenChamber 一次只能显示一条 —— 已打开最近更新的：{0}" +
                        "（共选 {1} 条）。要全部恢复请用「在 opencode 中打开」。",
                    [StatusCopied] = "已复制 {0} 个会话 ID 到剪贴板。",
                    [StatusResumed] = "已在 opencode 中恢复 {0} 条会话。",
                    [StatusFolders] = "已打开 {0} 个文件夹。",
                    [StatusFoldersMissing] = "已打开 {0} 个文件夹；{1} 个路径已不存在。",
                    [StatusOpenedIn] = "已在{0}中打开 {1} 个项目。",

                    [ViewSessions] = "会话",
                    [ViewProjects] = "项目",
                    [ProjSortBy] = "排序",
                    [ProjHideMissing] = "隐藏已删除",
                    [ProjColName] = "项目",
                    [ProjColLast] = "最近使用",
                    [ProjColVisits] = "访问",
                    [ProjColLines] = "日志行",
                    [ProjColExists] = "存在",
                    [ProjColPath] = "路径",
                    [ProjOpenGroup] = "打开项目",
                    [ProjOpenExplorer] = "资源管理器",
                    [ProjOpenInOpencode] = "在 opencode 中打开",
                    [ProjSortLastUsed] = "最近使用",
                    [ProjSortLeastUsed] = "最少使用",
                    [ProjSortMostVisited] = "访问最多",
                    [ProjSortMostActive] = "最活跃",
                    [ProjSortNameAsc] = "名称 A→Z",
                    [ProjSortNameDesc] = "名称 Z→A",
                    [ProjSortOldest] = "最早创建",
                    [ProjSortNewest] = "最新创建",
                    [ProjSortPathDepth] = "路径深度",
                    [ProjLoading] = "正在读取 opencode 日志…",
                    [ProjNoLogDir] = "未找到 opencode 日志目录：{0}",
                    [ProjScanning] = "正在扫描 {0} 个日志文件…",
                    [ProjCount] = "共 {0} 个项目",
                    [ProjCountHidden] = "共 {0} 个项目（已隐藏 {1} 个不存在的）",
                    [ProjNone] = "没有匹配的项目。",
                    [ProjNoSelection] = "请先选中一个项目。",
                    [ProjMissing] = "项目路径已不存在：{0}",
                    [ProjOpened] = "已打开：{0}",
                    [ProjYes] = "是",
                    [ProjNo] = "否",

                    [OpenWorkspace] = "工作区",
                    [WorkspaceTip] =
                        "工作区 = VS Code 多根工作区。opencode 没有工作区概念，" +
                        "OpenChamber 也只支持 session/focus/connect 三个深链动词，" +
                        "所以这里生成 .code-workspace 并交给 VS Code 打开。",
                    [WsOpening] = "正在准备工作区…",
                    [WsOpened] = "已在 VS Code 中打开工作区：{0}",
                    [WsCreated] = "已生成多根工作区（{0} 个项目）：{1}",
                    [WsFound] = "找到 {0} 个 .code-workspace 工作区文件。",
                    [WsNone] = "这些项目里没有找到 .code-workspace 文件。",
                    [WsNoCode] = "未找到 VS Code。请设置环境变量 CODE_PATH 指向 code.cmd。",
                    [WsStatusOne] = "工作区：{0}",
                    [WsStatusMany] = "工作区：{0} 个项目",
                    [WsStatusFound] = "工作区：{0} 个",

                    [OpenInOpenChamber] = "在 OpenChamber 打开",
                    [OpenSet] = "打开整套会话",
                    [OpenSetTip] =
                        "一次性在 OpenChamber 中打开这些会话，每条一个标签页。" +
                        "OpenChamber 标签条上限 10 条，超出的不会打开。",
                    [OcOpening] = "正在 OpenChamber 中打开项目…",
                    [OcActivated] = "已在 OpenChamber 中打开项目：{0}",
                    [OcAdded] = "已把 {0} 加入 OpenChamber 并打开（新增项目）。",
                    [OcWorkspace] = "已打开工作区 {0}：项目已设为当前，其对话树显示在侧边栏。",
                    [OcNoSettings] = "找不到 OpenChamber 的 settings.json，它装了吗？",
                    [OcNotRunning] = "已写入 OpenChamber 设置，但它没在运行，���动后生效。",
                    [OcOpenFailed] = "无法启动 OpenChamber：{0}",
                    [OcOpenedSet] = "已在 OpenChamber 中打开 {0} 的 {1} 条会话。",
                    [OcSelection] = "选中项",
                    [ProjColSessions] = "会话",
                    [ProjColCreated] = "创建时间",
                    [StatusSelectAll] = "已选择全部 {0} 条会话（当前显示 {1} 条）。",
                    [StatusCleared] = "已取消选择。",
                    [StatusPickFirst] = "请先勾选至少一条会话。",
                    [StatusBatchCap] =
                        "已选 {0} 条。每条会开一个 opencode 窗口，上限 {1} 条 —— 请缩小选择范围。",
                    [StatusNoFolder] = "所选会话都没有记录文件夹路径。",
                    [StatusEditorMissing] =
                        "未找到编辑器。请把可执行文件路径写进环境变量 SESSIONLAUNCHER_EDITOR。",
                    [StatusTerminalMissing] = "未找到可用的终端程序。",
                    [StatusProjectOpened] = "已在 opencode 中打开项目：{0}",
                    [StatusProjectFirstOfMany] =
                        "已在 opencode 中打开最近更新的项目：{0}（共 {1} 个，需要全部打开请用终端或资源管理器）",
                    [StatusProjectMissing] = "项目路径已不存在：{0}",
                    [StatusSearchCount] = "匹配 {0} / {1} 条",
                },

                [AppLang.En] = new()
                {
                    [AppTitle] = "Session Launcher",
                    [Catalog] = "Catalog",
                    [Conversations] = "{0} conversations",
                    [CatalogError] = "Could not read catalog: {0}",

                    [SearchPlaceholder] = "Filter by title, id or folder…",
                    [ClearSearchTip] = "Clear filter",
                    [SelectAll] = "Select all",
                    [ClearSelection] = "Clear selection",
                    [Reload] = "Reload",

                    [Pick] = "",
                    [ColUpdated] = "Updated",
                    [ColMsgs] = "Msgs",
                    [ColTitle] = "Title",
                    [ColFolder] = "Folder",
                    [ColCreated] = "Created",

                    [NothingSelected] = "No conversation selected",
                    [SelectedOne] = "1 selected — {0}",
                    [SelectedMany] = "{0} selected",
                    [SelectedStats] = "{0} messages  ·  {1} folder(s)",
                    [ShownSuffix] = "{0} of {1} shown",
                    [TickHint] = "Tick the checkbox to select several",

                    [OpenChamber] = "Open in OpenChamber",
                    [OpenOpencode] = "Open in opencode",
                    [CopyIds] = "Copy ids",
                    [OpenFolders] = "Open folders",

                    [ProjectGroup] = "Project & workspace",
                    [OpenExplorer] = "Explorer",
                    [OpenTerminal] = "Terminal",
                    [OpenEditor] = "Editor",
                    [OpenProject] = "Open project in opencode",
                    [OpenWorkspaceNote] =
                        "OpenChamber has no project deep link — switch to the folder manually",

                    [FontBigger] = "A+",
                    [FontSmaller] = "A-",
                    [FontReset] = "A",
                    [FontPercent] = "Font {0}%",
                    [LanguageLabel] = "中文",

                    [StatusOpenedOne] = "Opened “{0}” in OpenChamber.",
                    [StatusOpenedMany] =
                        "OpenChamber shows one at a time — opened the most recent: {0} " +
                        "of {1} selected. Use “Open in opencode” to resume them all.",
                    [StatusCopied] = "Copied {0} session id(s) to the clipboard.",
                    [StatusResumed] = "Resumed {0} conversation(s) in opencode.",
                    [StatusFolders] = "Opened {0} folder(s).",
                    [StatusFoldersMissing] = "Opened {0} folder(s); {1} no longer exist.",
                    [StatusOpenedIn] = "Opened {1} project(s) in {0}.",

                    [ViewSessions] = "Sessions",
                    [ViewProjects] = "Projects",
                    [ProjSortBy] = "Sort",
                    [ProjHideMissing] = "Hide missing",
                    [ProjColName] = "Project",
                    [ProjColLast] = "Last used",
                    [ProjColVisits] = "Visits",
                    [ProjColLines] = "Log lines",
                    [ProjColExists] = "Exists",
                    [ProjColPath] = "Path",
                    [ProjOpenGroup] = "Open project",
                    [ProjOpenExplorer] = "Explorer",
                    [ProjOpenInOpencode] = "Open in opencode",
                    [ProjSortLastUsed] = "Recently used",
                    [ProjSortLeastUsed] = "Least recently used",
                    [ProjSortMostVisited] = "Most visited",
                    [ProjSortMostActive] = "Most active",
                    [ProjSortNameAsc] = "Name A-Z",
                    [ProjSortNameDesc] = "Name Z-A",
                    [ProjSortOldest] = "Oldest first",
                    [ProjSortNewest] = "Newest first",
                    [ProjSortPathDepth] = "Path depth",
                    [ProjLoading] = "Reading opencode logs...",
                    [ProjNoLogDir] = "No opencode log directory found: {0}",
                    [ProjScanning] = "Scanning {0} log file(s)...",
                    [ProjCount] = "{0} project(s)",
                    [ProjCountHidden] = "{0} project(s) ({1} missing hidden)",
                    [ProjNone] = "No matching projects.",
                    [ProjNoSelection] = "Select a project first.",
                    [ProjMissing] = "Project path no longer exists: {0}",
                    [ProjOpened] = "Opened: {0}",
                    [ProjYes] = "yes",
                    [ProjNo] = "no",

                    [OpenWorkspace] = "Workspace",
                    [WorkspaceTip] =
                        "A workspace here means a VS Code multi-root workspace. " +
                        "opencode has no workspace concept and OpenChamber's deep link " +
                        "only understands session, focus and connect, so a " +
                        ".code-workspace file is written and handed to VS Code.",
                    [WsOpening] = "Preparing workspace...",
                    [WsOpened] = "Opened workspace in VS Code: {0}",
                    [WsCreated] = "Built a multi-root workspace from {0} project(s): {1}",
                    [WsFound] = "Found {0} .code-workspace file(s).",
                    [WsNone] = "No .code-workspace file found in these projects.",
                    [WsNoCode] = "Visual Studio Code not found. Set CODE_PATH to code.cmd.",
                    [WsStatusOne] = "Workspace: {0}",
                    [WsStatusMany] = "Workspace: {0} projects",
                    [WsStatusFound] = "Workspace: {0}",

                    [OpenInOpenChamber] = "Open in OpenChamber",
                    [OpenSet] = "Open set of sessions",
                    [OpenSetTip] =
                        "Open all of these in OpenChamber at once, one tab each. " +
                        "OpenChamber's tab strip caps at 10, so any beyond that stay closed.",
                    [OcOpening] = "Opening the project in OpenChamber...",
                    [OcActivated] = "Opened project in OpenChamber: {0}",
                    [OcAdded] = "Added {0} to OpenChamber and opened it (new project).",
                    [OcWorkspace] =
                        "Opened workspace {0}: the project is now current, and its conversation " +
                        "tree is shown in the sidebar.",
                    [OcNoSettings] = "Could not find OpenChamber's settings.json. Is it installed?",
                    [OcNotRunning] = "Wrote OpenChamber's settings, but it is not running; it will apply on launch.",
                    [OcOpenFailed] = "Could not start OpenChamber: {0}",
                    [OcOpenedSet] = "Opened {1} session(s) of {0} in OpenChamber.",
                    [OcSelection] = "the selection",
                    [ProjColSessions] = "Sessions",
                    [ProjColCreated] = "Created",
                    [StatusSelectAll] = "Selected all {0} conversations ({1} currently shown).",
                    [StatusCleared] = "Selection cleared.",
                    [StatusPickFirst] = "Tick at least one conversation first.",
                    [StatusBatchCap] =
                        "{0} selected. This opens one opencode window each, capped at {1} — " +
                        "narrow the selection first.",
                    [StatusNoFolder] = "None of the selected conversations recorded a folder.",
                    [StatusEditorMissing] =
                        "No editor found. Set SESSIONLAUNCHER_EDITOR to an executable path.",
                    [StatusTerminalMissing] = "No usable terminal found.",
                    [StatusProjectOpened] = "Opened project in opencode: {0}",
                    [StatusProjectFirstOfMany] =
                        "Opened the most recently used project in opencode: {0} " +
                        "(of {1}; use Terminal or Explorer to open them all)",
                    [StatusProjectMissing] = "Project path no longer exists: {0}",
                    [StatusSearchCount] = "{0} of {1} match",
                },
            };

        /// <summary>Raised after <see cref="SetLanguage"/> changes the active language.</summary>
        public static event EventHandler? Changed;

        public static AppLang Language { get; private set; } = AppLang.ZhHans;

        /// <summary>Language to switch to when the toggle is used.</summary>
        public static AppLang Other => Language == AppLang.ZhHans ? AppLang.En : AppLang.ZhHans;

        /// <summary>Short label for the toggle, e.g. "EN" while Chinese is active.</summary>
        public static string ToggleLabel => T(LanguageLabel);

        public static void SetLanguage(AppLang lang)
        {
            if (Language == lang) return;
            Language = lang;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Toggle() => SetLanguage(Other);

        /// <summary>
        /// Look up a string. Falls back to Simplified Chinese, then to the key itself, so a
        /// missing translation degrades to something visible rather than blank.
        /// </summary>
        public static string T(string key)
        {
            if (Tables.TryGetValue(Language, out var table) && table.TryGetValue(key, out var value))
                return value;
            if (Tables[AppLang.ZhHans].TryGetValue(key, out var fallback))
                return fallback;
            return key;
        }

        /// <summary>
        /// Look up and interpolate <c>{0}</c>-style placeholders.
        /// </summary>
        /// <param name="key">A key from this class.</param>
        /// <param name="args">Values for the placeholders, in order.</param>
        public static string Format(string key, params object?[] args) => Interpolate(T(key), args);

        /// <summary>
        /// Interpolate <c>{0}</c>-style placeholders in an already-localised string.
        /// </summary>
        /// <remarks>
        /// Named separately from <see cref="Format"/>: a <c>params</c> array and a plain
        /// array parameter are the same signature to the compiler, so they cannot be
        /// overloads of each other.
        /// </remarks>
        public static string Interpolate(string template, params object?[] args)
        {
            if (string.IsNullOrEmpty(template) || args.Length == 0) return template;

            try
            {
                // Replace by index rather than using string.Format, so a stray brace in a
                // session title can never throw and take the status line down with it.
                for (var i = 0; i < args.Length; i++)
                    template = template.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}",
                                                args[i]?.ToString() ?? string.Empty);
            }
            catch
            {
                // Formatting must never be fatal.
            }

            return template;
        }
    }
}

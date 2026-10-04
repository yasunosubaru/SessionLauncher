// LauncherService.cs
//
// Everything SessionLauncher asks the shell to do: open a conversation in
// OpenChamber or opencode, open a folder in Explorer / a terminal / an editor.
//
// No NuGet, no abstraction layer. Each method is one Process.Start with the
// argument shape that target actually needs, and the awkward parts are commented
// where the next reader will hit them.
//
// RECONSTRUCTED 2026-10-01. A bad scripted rewrite truncated this file to zero
// bytes and there is no VCS here, so it was rebuilt from its call sites, the
// README, and the self-tests. The public surface below is exactly what callers
// use; see the notes on each member for the behaviour that was verified before.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services;

/// <summary>Opens things. One instance per window; holds no mutable state.</summary>
public sealed class LauncherService
{
    /// <summary>OpenChamber's custom scheme. Only three verbs exist: connect, focus, session.</summary>
    public const string DeepLinkScheme = "openchamber";

    /// <summary>
    /// OpenChamber's executable, or null when it is not installed.
    /// </summary>
    /// <remarks>
    /// Overridable with <c>SESSIONLAUNCHER_OPENCHAMBER_EXE</c> so a build can be
    /// pointed at a portable install. Probed per user because the Squirrel install
    /// lives under <c>%LOCALAPPDATA%\@openchamberelectron\</c>, not Program Files.
    /// </remarks>
    public string? OpenChamberExe { get; } = ResolveOpenChamberExe();

    /// <summary>Override for the editor, or null.</summary>
    public string? EditorOverride { get; } = Env("SESSIONLAUNCHER_EDITOR");

    // ---- conversations ------------------------------------------------------

    /// <summary>Resume a conversation in OpenChamber.</summary>
    /// <remarks>
    /// The deep link is <c>openchamber://session/&lt;id&gt;</c>. When OpenChamber is
    /// already running this arrives as a <c>second-instance</c> event and merely
    /// focuses the window; either way the renderer ends up calling
    /// <c>setCurrentSession(id, null)</c>.
    /// </remarks>
    public void OpenInOpenChamber(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        OpenSessionInOpenChamber(session.Id);
    }

    /// <summary>Open one session in OpenChamber by id.</summary>
    public void OpenSessionInOpenChamber(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var exe = OpenChamberExe
            ?? throw new FileNotFoundException(
                "OpenChamber.exe not found. Set SESSIONLAUNCHER_OPENCHAMBER_EXE to override.");

        Start(exe, $"{DeepLinkScheme}://session/{Uri.EscapeDataString(sessionId)}");
    }

    /// <summary>
    /// Open a project's set of sessions in OpenChamber.
    /// </summary>
    /// <returns>True when a session was dispatched.</returns>
    /// <remarks>
    /// This opens the SET in the only sense OpenChamber actually supports, and the
    /// distinction matters because the obvious implementation is a no-op that reports
    /// success.
    /// <para>
    /// The first attempt dispatched <c>openchamber://session/&lt;id&gt;</c> once per
    /// session, on the theory that OpenChamber appends a header tab on every session
    /// change. The source supports the theory — <c>SessionTabsStrip.tsx</c> calls
    /// <c>ensureTab</c> whenever <c>currentSessionId</c> changes — and the theory is
    /// still true. It buys nothing on screen, for two independent reasons:
    /// <list type="number">
    /// <item>The strip is not rendered while a project is active.
    /// <c>Header.tsx:737</c> computes <c>showHeaderMetaRow = !isChatContext
    /// &amp;&amp; !workStatusPanelVisible &amp;&amp; Boolean(activeProjectLabel ||
    /// currentBranchLabel || worktreeBadgeKind)</c>, and the meta-row branch renders
    /// the title where the strip would otherwise go. With a project active — which is
    /// the entire point of opening a project — the strip is never mounted.</item>
    /// <item>Nothing recoverable is left behind. This machine's
    /// <c>AppData\Roaming\OpenChamber\Local Storage\leveldb</c> has no
    /// <c>session-tabs-store</c> key and no array of two or more session ids
    /// anywhere in it.</item>
    /// </list>
    /// So N dispatches left exactly one visible session, took N times as long, and
    /// let the status line report a number ("opened 10 sessions") that was our own
    /// dispatch count rather than anything the user could see.
    /// <para>
    /// What does work is ONE deep link to the project's most recent session.
    /// OpenChamber resolves that session's own project, switches to it, and its
    /// sidebar then shows the project's whole session set grouped beneath the project
    /// node — the project-then-sessions tree, which is what opening a set of
    /// sessions means here. Verified against a real install: linking one session
    /// moved OpenChamber to that session's project with its conversations listed
    /// under it.
    /// <para>
    /// <b>Nothing is written anywhere.</b> OpenChamber updates its own
    /// <c>activeProjectId</c> and <c>lastDirectory</c> in response to the navigation;
    /// this process never touches its settings file.
    /// </remarks>
    public bool OpenSessionSetInOpenChamber(IReadOnlyList<string> sessionIds)
    {
        if (sessionIds is null || sessionIds.Count == 0) return false;

        // ProjectCatalog hands these over newest first, so the first usable entry is
        // the one to open.
        var target = sessionIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (target is null) return false;

        OpenSessionInOpenChamber(target);
        return true;
    }

    /// <summary>
    /// True when OpenChamber is running, in any of its processes.
    /// </summary>
    /// <remarks>
    /// The count itself lives on <see cref="ProjectRegistrationWait"/> because the
    /// self-test harness stages that file and not this one; see the note there for
    /// why a window-based check would be wrong.
    /// </remarks>
    public static bool IsOpenChamberRunning() => ProjectRegistrationWait.OpenChamberProcessCount() > 0;

    /// <summary>
    /// Bring OpenChamber to the front, starting it if needed.
    /// </summary>
    /// <returns>True when it was already running, false when it had to be started.</returns>
    /// <remarks>
    /// Launched with no arguments on purpose: the deep link verbs are connect, focus
    /// and session, and none of them selects a project, so there is no argument that
    /// would do it.
    /// </remarks>
    public bool OpenChamberApp()
    {
        var exe = OpenChamberExe
            ?? throw new FileNotFoundException(
                "OpenChamber.exe not found. Set SESSIONLAUNCHER_OPENCHAMBER_EXE to override.");

        var wasRunning = IsOpenChamberRunning();

        // Shell-execute so the Squirrel shim resolves rather than being treated as a
        // raw executable with no runtime attached.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();

        return wasRunning;
    }

    /// <summary>
    /// Resume a conversation in the opencode CLI (<c>opencode -s &lt;id&gt;</c>).
    /// </summary>
    /// <remarks>
    /// Runs inside a terminal window, not as a bare child process. opencode is a TUI: it
    /// draws to a console, and started from a WPF app with
    /// <c>UseShellExecute = false</c> it gets a console that belongs to this process, so
    /// the window closes the moment opencode exits. When opencode does exit at once —
    /// which it does on this machine, see <see cref="RunOpencode"/> — the user saw
    /// nothing but a flash. In a terminal the error stays on screen to be read.
    /// </remarks>
    public void OpenInOpencode(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var dir = string.IsNullOrWhiteSpace(session.ProjectPath) ? null : session.ProjectPath;
        RunOpencode(dir, $"-s {session.Id}");
    }

    // ---- folders ------------------------------------------------------------

    /// <summary>Open a conversation's folder in Explorer.</summary>
    public void OpenDirectory(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        OpenDirectoryPath(session.ProjectPath);
    }

    /// <summary>Open a folder in Explorer.</summary>
    public void OpenDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>Open a folder in a terminal.</summary>
    /// <remarks>
    /// Windows Terminal when present, else <c>cmd.exe</c>. <c>wt.exe</c> is launched
    /// through the shell on purpose: under
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> it is an App Execution Alias stub
    /// that hands off to the terminal broker and exits. Started with
    /// <c>UseShellExecute = false</c> it appears to succeed and then does nothing, so
    /// a before/after process count proves nothing either way.
    /// </remarks>
    public void OpenTerminal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (ResolveOnPath("wt.exe") is not null)
        {
            Start("wt.exe", $"-d \"{path}\"");
            return;
        }

        Start("cmd.exe", $"/k cd /d \"{path}\"");
    }

    /// <summary>
    /// Open a folder in an editor: <c>$SESSIONLAUNCHER_EDITOR</c>, else VS Code,
    /// Notepad++, then notepad.
    /// </summary>
    /// <remarks>
    /// The per-user VS Code layout is probed BEFORE the system one. This machine has
    /// VS Code 1.137.0 under <c>%LOCALAPPDATA%\Programs\Microsoft VS Code\</c> and
    /// nothing in Program Files; probing only the system paths made "open in editor"
    /// silently open Notepad++ on a machine that has VS Code.
    /// </remarks>
    public void OpenEditor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!string.IsNullOrWhiteSpace(EditorOverride))
        {
            Start(EditorOverride, $"\"{path}\"");
            return;
        }

        var code = ResolveCodeCommand();
        if (code is not null)
        {
            Start(code, $"\"{path}\"");
            return;
        }

        foreach (var candidate in new[]
                 {
                     @"%LOCALAPPDATA%\Programs\Notepad++\notepad++.exe",
                     @"%ProgramFiles%\Notepad++\notepad++.exe",
                 })
        {
            var exe = Expand(candidate);
            if (File.Exists(exe))
            {
                Start(exe, $"\"{path}\"");
                return;
            }
        }

        // notepad is always present, and opening a directory in it is useless but
        // harmless; better than doing nothing at all.
        Start("notepad.exe", $"\"{path}\"");
    }

    /// <summary>
    /// Start an opencode TUI rooted at the project, so its session list is that
    /// project's rather than the global one.
    /// </summary>
    public void OpenProjectInOpencode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RunOpencode(path, string.Empty);
    }

    /// <summary>
    /// Run opencode inside a terminal window, rooted at <paramref name="workingDirectory"/>.
    /// </summary>
    /// <remarks>
    /// A TUI needs a console it does not own, so this goes through <c>wt.exe</c> or
    /// <c>cmd.exe</c> rather than starting the process directly. The failure this fixes
    /// is silent and was reported as "a cmd window opens and then disappears":
    /// <c>opencode</c> on PATH is <c>opencode.cmd</c>, an npm batch shim, so starting it
    /// with <c>UseShellExecute = false</c> hands it a console tied to this process. When
    /// opencode exits, the window goes with it.
    /// <para>
    /// The shell command also clears <c>OPENCODE_CONFIG</c> first. On this machine it
    /// points at <c>~/.config/openchamber/opencode.managed.json</c>, OpenChamber's own
    /// config, whose whole purpose is to add OpenChamber's agent tool to a CLI session.
    /// A conversation opened from SessionLauncher is not an OpenChamber conversation, so
    /// inheriting that config would pull in a tool the session has no business having.
    /// <para>
    /// <b>opencode could not start at all on this machine</b> when this was written, for a
    /// reason on both sides of that variable: <c>opencode.managed.json</c> spelled the key
    /// <c>"plugins"</c> instead of <c>"plugin"</c>, and so did
    /// <c>~/.config/opencode/opencode.jsonc</c>, which is what opencode falls back to
    /// when the variable is cleared. Either one alone was enough for opencode to reject
    /// the config and exit at once:
    /// <code>
    /// Configuration is invalid at ...
    /// &#9432; Unrecognized key: plugins
    /// </code>
    /// Both were corrected on 2026-10-01, with backups beside this repo's tooling. The
    /// managed one is OpenChamber's file, so a future OpenChamber version that writes the
    /// old key would bring this straight back — in a terminal window rather than a flash.
    /// </para>
    /// </remarks>
    private void RunOpencode(string? workingDirectory, string arguments)
    {
        var opencode = ResolveOnPath("opencode.cmd") ?? ResolveOnPath("opencode.exe");
        if (opencode is null)
            throw new FileNotFoundException(
                "opencode was not found on PATH. Install it, or set it up so `opencode` resolves.");

        // A full path with spaces cannot go through wt.exe's command parsing unquoted,
        // and cmd needs different quoting again, so each host gets its own shape.
        var quoted = $"\"{opencode}\"";
        var line = string.IsNullOrWhiteSpace(arguments)
            ? quoted
            : $"{quoted} {arguments}";

        // `set "VAR="` rather than `set VAR=`: unquoted, `set` swallows everything after
        // the `=` as the value, so `set VAR= && cmd` assigns VAR the text "&& cmd" and
        // runs nothing. The quoted form assigns exactly empty. Verified against cmd
        // directly, and the whole line is what cmd executes, not what this app does.
        line = "set \"OPENCODE_CONFIG=\" && " + line;

        if (ResolveOnPath("wt.exe") is not null)
        {
            // wt takes the command as trailing argv, parsed with CommandLineToArgvW
            // rules, and it does NOT interpret `&&`. Two consequences, both found by
            // testing rather than reading:
            //   - The line must arrive as ONE argument. Left unquoted it is split on
            //     spaces, `cmd /k` receives only `set`, and the terminal just sits there
            //     titled "set" doing nothing.
            //   - So the line goes to `cmd`, which does understand `&&`, and the inner
            //     quotes are backslash-escaped for wt's own parser.
            // Verified end to end: the command runs in the project directory with
            // OPENCODE_CONFIG cleared, and opencode reports 1.18.33.
            var escaped = line.Replace("\"", "\\\"");
            var wtArgs = string.IsNullOrWhiteSpace(workingDirectory)
                ? $"cmd /k \"{escaped}\""
                : $"-d \"{workingDirectory}\" cmd /k \"{escaped}\"";
            Start("wt.exe", wtArgs);
            return;
        }

        // cmd /k keeps the window up after opencode exits, which is the whole point when
        // opencode is going to fail.
        var cd = string.IsNullOrWhiteSpace(workingDirectory) ? "" : $"cd /d \"{workingDirectory}\" && ";
        Start("cmd.exe", $"/k {cd}{line}");
    }

    /// <summary>
    /// Put text on the clipboard.
    /// </summary>
    /// <remarks>
    /// STA is required for the clipboard and this is called from the UI thread, which
    /// is already STA. Setting <c>Text</c> (rather than <c>SetDataObject</c>) keeps the
    /// text alive after the process that set it exits, which matters here because the
    /// only consumer is whatever the user pastes into next.
    /// </remarks>
    public void CopyText(string text)
    {
        if (text is null) return;
        System.Windows.Clipboard.SetText(text);
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>
    /// Start a process through the shell, so aliases and shims resolve.
    /// </summary>
    private static void Start(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true })
            ?.Dispose();
    }

    /// <summary>Locate <c>code.cmd</c>, per-user install first.</summary>
    private static string? ResolveCodeCommand()
    {
        foreach (var candidate in new[]
                 {
                     @"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd",
                     @"%ProgramFiles%\Microsoft VS Code\bin\code.cmd",
                 })
        {
            var exe = Expand(candidate);
            if (File.Exists(exe)) return exe;
        }

        return ResolveOnPath("code.cmd");
    }

    /// <summary>Locate OpenChamber's executable, per-user install first.</summary>
    private static string? ResolveOpenChamberExe()
    {
        var overridden = Env("SESSIONLAUNCHER_OPENCHAMBER_EXE");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
            return overridden;

        foreach (var candidate in new[]
                 {
                     @"%LOCALAPPDATA%\Programs\@openchamberelectron\OpenChamber.exe",
                     @"%LOCALAPPDATA%\@openchamberelectron\OpenChamber.exe",
                     @"%ProgramFiles%\OpenChamber\OpenChamber.exe",
                 })
        {
            var exe = Expand(candidate);
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    /// <summary>
    /// Full path of an executable on PATH, or null.
    /// </summary>
    /// <remarks>
    /// PATH is walked here rather than handed to <c>Process.Start</c> because a caller
    /// needs to KNOW whether a tool exists before choosing between two behaviours —
    /// the terminal picks <c>wt.exe</c> or <c>cmd.exe</c> on this answer.
    /// </remarks>
    public static string? ResolveOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Contains(Path.DirectorySeparatorChar) && File.Exists(fileName))
            return fileName;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim(), fileName);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry throws rather than skipping.
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Read an environment variable, trimmed, or null when unset/blank.</summary>
    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Expand %VAR% in a path without requiring the file to exist.</summary>
    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}

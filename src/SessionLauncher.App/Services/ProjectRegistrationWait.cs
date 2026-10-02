// ProjectRegistrationWait.cs
//
// Waits for OpenChamber to be gone, so a project can be registered in its
// settings.json without racing the process that owns that file.
//
// WHY "GONE" AND NOT "NO WINDOW"
//
// This machine runs four processes called OpenChamber and only one of them owns
// a window. Closing that window does not exit the app: main.mjs:2193 routes the
// close through shouldHideMainWindowToTray, so the process survives in the tray
// and would go on overwriting settings.json on its next save. The only
// unambiguous signal is a process count of zero.
//
// WHY TWO CONSECUTIVE ZEROS
//
// An updater restarting OpenChamber takes the count to zero for a moment and
// brings it straight back. Writing settings.json into that gap would be a
// read-modify-write against a file the dying process may still flush, and
// OpenChamber would then come back and overwrite us. One zero sample is a dip;
// two in a row is an exit.
//
// The process count is injected so the self-test drives the whole state machine
// without polling anything real, and so the poll interval can be set to zero and
// the suite does not actually sleep.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SessionLauncher.App.Services;

/// <summary>What clicking a project row should do.</summary>
public enum RegistrationAction
{
    /// <summary>
    /// One deep link to the project's newest conversation. OpenChamber resolves
    /// the session's project, switches to it and shows its sessions. Nothing is
    /// written anywhere.
    /// </summary>
    OpenByDeepLink,

    /// <summary>
    /// OpenChamber is not running, so back the settings up, add the project, set it
    /// active, verify, and start OpenChamber on the result.
    /// </summary>
    RegisterThenLaunch,

    /// <summary>
    /// OpenChamber is running and does not know this project. Writing its settings
    /// now would be a read-modify-write against a file a live process owns, so wait
    /// for it to exit instead.
    /// </summary>
    WaitForExit,
}

public sealed class ProjectRegistrationWait : IDisposable
{
    /// <summary>
    /// How many OpenChamber processes exist.
    /// </summary>
    /// <remarks>
    /// Zero is the only unambiguous "it is not running". This machine has four
    /// processes called OpenChamber and only one owns a window, and closing that
    /// window hides it to the tray rather than exiting — so a window-based check
    /// would report "gone" while a live process still owns settings.json.
    /// <para>
    /// This lives here rather than on <see cref="LauncherService"/> because the
    /// self-test harness stages this file but not that one: LauncherService drags in
    /// Process.Start and the editor/terminal lookup chain. One definition, no
    /// dependency the harness cannot satisfy.
    /// </para>
    /// </remarks>
    public static int OpenChamberProcessCount()
    {
        var processes = Process.GetProcessesByName("OpenChamber");
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>How often the process list is re-read while waiting.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long to wait before giving up.
    /// </summary>
    /// <remarks>
    /// INFINITE by default, and that is the feature rather than a missing setting.
    /// The user is expected to quit OpenChamber whenever they get round to it —
    /// possibly hours later. A finite timeout would expire while they were away and
    /// oblige them to come back and click a second time, which is precisely the
    /// "please go and do something now" this is meant not to be.
    /// <para>
    /// Nothing is leaked by waiting forever: the loop is a timer, not a thread, and
    /// <see cref="Dispose"/> stops it.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// How many zero samples in a row count as an exit.
    /// </summary>
    /// <remarks>Two. One is a dip; see the file header for what dips.</remarks>
    private const int RequiredConsecutiveZeros = 2;

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private readonly Func<int> _processCount;
    private readonly CancellationTokenSource _cancel = new();

    private int _waiting;

    /// <param name="pollInterval">Delay between samples. Zero means "as fast as possible".</param>
    /// <param name="timeout">Give up after this long.</param>
    /// <param name="processCount">
    /// How many OpenChamber processes exist right now. Defaults to the real check;
    /// the self-test injects a script so it never polls anything.
    /// </param>
    public ProjectRegistrationWait(
        TimeSpan pollInterval,
        TimeSpan timeout,
        Func<int>? processCount = null)
    {
        _pollInterval = pollInterval < TimeSpan.Zero ? TimeSpan.Zero : pollInterval;
        _timeout = timeout;
        _processCount = processCount ?? OpenChamberProcessCount;
    }

    /// <summary>True while a wait is in flight.</summary>
    public bool IsWaiting => Volatile.Read(ref _waiting) > 0;

    /// <summary>
    /// Which action opening <paramref name="projectPath"/> calls for.
    /// </summary>
    /// <remarks>
    /// Registered wins over running: a deep link needs nothing from OpenChamber, so
    /// there is no reason to make the user close anything. The two unregistered
    /// cases differ only in whether the write is safe yet.
    /// </remarks>
    public static RegistrationAction Decide(bool isRegistered, bool openChamberRunning)
        => isRegistered ? RegistrationAction.OpenByDeepLink
           : openChamberRunning ? RegistrationAction.WaitForExit
           : RegistrationAction.RegisterThenLaunch;

    /// <summary>
    /// Resolve true once OpenChamber has been gone for two consecutive samples.
    /// </summary>
    /// <returns>
    /// True only on a confirmed exit. False on timeout, on <see cref="Cancel"/>, on
    /// <see cref="Dispose"/>, and on a token that was already cancelled — every
    /// "we did not establish that it is safe" case, which the caller must treat as
    /// "do not write".
    /// </returns>
    public async Task<bool> WaitForExitAsync(CancellationToken ct)
    {
        // Before the loop, not inside it: the test drives a delegate that throws if
        // called, so polling even once with a cancelled token is a failure.
        if (ct.IsCancellationRequested) return false;

        Interlocked.Increment(ref _waiting);
        try
        {
            // Linked, so Cancel(), Dispose() and the caller's token all stop the loop
            // through one path rather than three.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancel.Token);
            var token = linked.Token;

            var clock = Stopwatch.StartNew();
            var consecutiveZeros = 0;

            while (true)
            {
                if (token.IsCancellationRequested) return false;

                if (_processCount() == 0)
                {
                    consecutiveZeros++;
                    if (consecutiveZeros >= RequiredConsecutiveZeros) return true;
                }
                else
                {
                    consecutiveZeros = 0;
                }

                // TimeSpan cannot represent "never", so an infinite deadline is carried as
                // Timeout.InfiniteTimeSpan, which is NEGATIVE one millisecond. Left
                // unguarded, `elapsed >= that` is true immediately and the wait would
                // expire before its first poll — the opposite of what it asks for.
                if (_timeout != Timeout.InfiniteTimeSpan && clock.Elapsed >= _timeout)
                    return false;

                try
                {
                    await Task.Delay(_pollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Stop waiting. Safe to call when nothing is waiting.</summary>
    public void Cancel()
    {
        try { _cancel.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed; nothing left to cancel */ }
    }

    /// <summary>
    /// Cancel and release. Safe to call twice, and safe to call while a wait is in
    /// flight — which is how the window's Closed handler keeps a timer from
    /// outliving the app.
    /// </summary>
    public void Dispose()
    {
        Cancel();
        _cancel.Dispose();
    }

    /// <summary>Number of assertions checked. Throws on the first failure.</summary>
    public static int RunSelfTest()
    {
        var n = 0;
        void Check(bool ok, string what)
        {
            n++;
            if (!ok) throw new InvalidOperationException("ProjectRegistrationWait self-test failed: " + what);
        }

        // A script of process counts, replayed one entry per poll. The tail repeats
        // the last value forever, so a test can say "and then it stays that way".
        static Func<int> Script(List<int> counts, List<int>? calls = null)
        {
            var i = 0;
            return () =>
            {
                var value = counts[i < counts.Count ? i : counts.Count - 1];
                calls?.Add(value);
                i++;
                return value;
            };
        }

        // ---- the two-consecutive-zeros rule ----
        //
        // A 1 ms interval, not zero. The first version of this helper failed to
        // advance its script, so every poll returned the first value and these two
        // cases spun until their 30-second timeouts — sixty seconds of a suite that
        // normally finishes in well under one. A broken test that fails slowly is a
        // broken test you stop reading.
        var fast = TimeSpan.FromMilliseconds(1);

        {
            var calls = new List<int>();
            using var wait = new ProjectRegistrationWait(
                fast, TimeSpan.FromSeconds(30), Script(new() { 4, 4, 0, 0 }, calls));
            Check(wait.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "two consecutive zeros mean OpenChamber is gone");
            Check(string.Join(",", calls.Take(4)) == "4,4,0,0",
                  "the polls are 4, 4, then the two zeros"
                + $" (saw={string.Join(",", calls.Take(6))})");
        }

        {
            // A single zero in the middle must not resolve it. This is the updater
            // restart, and getting it wrong means writing into a live file.
            var calls = new List<int>();
            using var wait = new ProjectRegistrationWait(
                fast, TimeSpan.FromSeconds(30), Script(new() { 4, 0, 4, 4, 0, 0 }, calls));
            Check(wait.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "a transient zero does not stop the wait, and a later pair still does");
            Check(calls.Count >= 6,
                  "the transient zero at poll 2 was not accepted as one half of a pair"
                + $" (polls={calls.Count}, saw={string.Join(",", calls)})");
        }

        // ---- timeout ----
        {
            using var wait = new ProjectRegistrationWait(
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300),
                Script(new() { 0, 4 }));
            Check(!wait.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "one zero then a running app times out rather than proceeding");
        }

        {
            using var wait = new ProjectRegistrationWait(
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300),
                Script(new() { 4 }));
            Check(!wait.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "an app that never exits times out");
        }

        // ---- cancellation ----
        {
            using var wait = new ProjectRegistrationWait(
                TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30), Script(new() { 4 }));
            using var cts = new CancellationTokenSource();
            var task = wait.WaitForExitAsync(cts.Token);
            SpinUntil(() => wait.IsWaiting, TimeSpan.FromSeconds(5));
            wait.Cancel();
            Check(!task.GetAwaiter().GetResult(), "Cancel ends the wait without reporting an exit");
        }

        {
            // An already-cancelled token must not poll even once: the delegate
            // throws if it is called, which proves the loop checks the token first.
            using var wait = new ProjectRegistrationWait(
                fast, TimeSpan.FromSeconds(30),
                () => throw new InvalidOperationException("polled despite a cancelled token"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Check(!wait.WaitForExitAsync(cts.Token).GetAwaiter().GetResult(),
                  "an already-cancelled token returns immediately without polling");
        }

        // ---- IsWaiting, and Dispose ----
        {
            using var wait = new ProjectRegistrationWait(
                TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30), Script(new() { 4 }));
            Check(!wait.IsWaiting, "IsWaiting is false before the wait starts");

            var task = wait.WaitForExitAsync(CancellationToken.None);
            SpinUntil(() => wait.IsWaiting, TimeSpan.FromSeconds(5));
            Check(wait.IsWaiting, "IsWaiting is true while the wait is running");

            wait.Dispose();
            Check(task.Wait(TimeSpan.FromSeconds(10)),
                  "Dispose completes the wait instead of leaving it hanging");
            Check(!wait.IsWaiting, "IsWaiting is false again afterwards");
            wait.Dispose();   // must be safe to dispose twice
        }

        // ---- the all-clear fast path ----
        {
            using var wait = new ProjectRegistrationWait(
                fast, TimeSpan.FromSeconds(30), () => 0);
            Check(wait.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "an app that is already gone is detected");
        }

        // ---- defaults ----
        {
            Check(ProjectRegistrationWait.DefaultPollInterval == TimeSpan.FromMilliseconds(700),
                  "the poll interval is 700 ms");

            // No deadline. The whole point is that the user quits OpenChamber
            // whenever they get round to it and everything happens afterwards; a
            // timeout would expire while they are at lunch and then oblige them to
            // come back and click again.
            Check(ProjectRegistrationWait.DefaultTimeout == Timeout.InfiniteTimeSpan,
                  "the default wait never gives up");

            // Which has to be more than a constant change. A delegate that stays
            // non-zero for 300 polls and then goes to zero must still resolve TRUE.
            // Any finite default measured in tens of milliseconds would have given
            // up long before poll 300, which is exactly the behaviour being removed.
            var stubborn = 0;
            using var patient = new ProjectRegistrationWait(
                TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan,
                () => (++stubborn < 300) ? 4 : 0);
            Check(patient.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult(),
                  "a wait survives 300 non-zero samples and still reports the exit"
                + $" (saw {stubborn} polls)");
            Check(stubborn >= 300, "and it really did poll that many times");
        }

        // ---- the dispatch decision ----
        //
        // Pure, so it can be pinned without a window and without OpenChamber. The
        // three inputs are the only things that vary, and getting any of them wrong
        // has a consequence that is not visible until it has already happened:
        // writing settings.json under a running OpenChamber loses the write, or
        // sending a deep link for an unregistered project opens nothing.

        Check(Decide(true, false) == RegistrationAction.OpenByDeepLink,
              "a registered project opens by deep link when OpenChamber is closed");
        Check(Decide(true, true) == RegistrationAction.OpenByDeepLink,
              "and identically when it is running — a deep link needs nothing from it");
        Check(Decide(false, false) == RegistrationAction.RegisterThenLaunch,
              "an unregistered project with OpenChamber closed is registered immediately");
        Check(Decide(false, true) == RegistrationAction.WaitForExit,
              "an unregistered project with OpenChamber running waits instead of writing");

        // Every combination must land on exactly one action; a fourth value would
        // be a state the UI has no handling for.
        var seen = new HashSet<RegistrationAction>();
        foreach (var registered in new[] { false, true })
            foreach (var running in new[] { false, true })
                seen.Add(Decide(registered, running));
        Check(seen.Count == 3, "the four combinations produce exactly three actions");

        return n;
    }

    private static void SpinUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < limit) Thread.Sleep(5);
    }
}
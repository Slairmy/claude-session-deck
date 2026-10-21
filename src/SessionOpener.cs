using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClaudeSessionDeck
{
    /// <summary>Brings a registered session's terminal to the front, or starts it in a new Windows Terminal window.</summary>
    internal static class SessionOpener
    {
        /// <summary>Command-line entry for "claude-session-deck open &lt;name&gt;".</summary>
        public static string OpenByName(string name)
        {
            Session session = Store.Load().FirstOrDefault(s => s.name == name);
            return session == null ? "No session named '" + name + "'" : OpenSession(session);
        }

        /// <summary>Brings the session's window and tab to the front, or launches it when no window exists.</summary>
        public static string OpenSession(Session session)
        {
            long window = Store.WindowOf(session.name);
            bool running = Tabs.MarkOf(window, session) != ' ';
            Hook.LogQuietly("open", "'" + session.name + "' window=" + window + " pid=" + session.pid + " pane=" + session.pane + " running=" + running);
            if (running && window == 0)
            {
                // Running, but its window is not on record (or the handle went stale, e.g. the tab moved to another window).
                ConsoleWindows.Forget(session.pid);
                window = session.pid > 0 ? ConsoleWindows.Of(session.pid) : 0;
                if (window == 0) { return "running, but not in a Windows Terminal window the deck can focus"; }
                Store.SetWindow(session.name, window);
            }
            if (running)
            {
                // One lookup at a time: overlapping clicks would otherwise fight over which tab is selected.
                Stopwatch waited = Stopwatch.StartNew();
                lock (Gate)
                {
                    if (waited.ElapsedMilliseconds > 200) { Hook.LogQuietly("open", "'" + session.name + "' waited " + waited.ElapsedMilliseconds + "ms for an earlier lookup"); }
                    Native.Focus(window);
                    return Reveal(window, session);
                }
            }
            Launch(session);
            return "launched";
        }

        private static readonly object Gate = new object();

        /// <summary>
        /// Cheapest way first, each step visible only if the earlier ones could not decide: the remembered pane when its tab is
        /// showing, the tab titled with the session name, the remembered pane in another tab, and only then a bounded scan.
        /// Every step is logged with the time it took, so a slow machine shows up as slow steps and timeouts in hook.log.
        /// </summary>
        private static string Reveal(long window, Session session)
        {
            Stopwatch clock = Stopwatch.StartNew();
            string name = session.name;
            string result = RevealCore(window, session, clock);
            Hook.LogQuietly("open", "'" + name + "' => " + result + " in " + clock.ElapsedMilliseconds + "ms");
            return result;
        }

        private static string RevealCore(long window, Session session, Stopwatch clock)
        {
            string name = session.name;
            List<string> others = Store.Load().Where(s => s.name != name).Select(s => s.name).ToList();

            bool inView = Tabs.FocusPaneInView(window, session.pane);
            Hook.LogQuietly("open", "'" + name + "' step 1 remembered pane in view=" + inView + " (" + clock.ElapsedMilliseconds + "ms)");
            if (inView)
            {
                if (!Tabs.ShowsOtherSession(window, name, others)) { return "focused (pane)"; }
                Hook.LogQuietly("open", "'" + name + "' remembered pane shows another session; forgetting it");
                Store.ForgetPane(name);
            }

            bool exact = Tabs.SelectExact(window, name);
            Hook.LogQuietly("open", "'" + name + "' step 2 tab titled with the name=" + exact + " (" + clock.ElapsedMilliseconds + "ms)");
            if (exact) { return "focused"; }

            bool elsewhere = Tabs.FocusPane(window, session.pane);
            Hook.LogQuietly("open", "'" + name + "' step 3 remembered pane in another tab=" + elsewhere + " (" + clock.ElapsedMilliseconds + "ms)");
            if (elsewhere)
            {
                if (!Tabs.ShowsOtherSession(window, name, others)) { return "focused (pane)"; }
                Hook.LogQuietly("open", "'" + name + "' remembered pane shows another session; forgetting it");
                Store.ForgetPane(name);
            }

            string pane = Tabs.ScanForSession(window, name);
            Hook.LogQuietly("open", "'" + name + "' step 4 scan found pane='" + pane + "' (" + clock.ElapsedMilliseconds + "ms)");
            if (pane == null) { return "focused (tab not found)"; }
            // Remember where it was, so the next click goes straight there.
            Store.SetPane(name, pane);
            return "focused (pane)";
        }

        private static void Launch(Session session)
        {
            string dir = session.dir;
            if (!Directory.Exists(dir)) { throw new InvalidOperationException("Directory not found: " + dir); }
            if (!Store.IsSafeText(dir)) { throw new InvalidOperationException("Directory path contains unsupported characters"); }
            dir = dir.Length > 3 ? dir.TrimEnd('\\') : (dir.EndsWith("\\") ? dir + "." : dir);

            string claudeArgs = session.started
                ? "--resume " + session.id
                : "--session-id " + session.id + " -n \"" + session.name + "\"";

            List<long> before = Native.Snapshot();
            StartTerminal("-w new -d \"" + dir + "\" cmd.exe /k claude " + claudeArgs);
            if (!session.started) { Store.MarkStarted(session.name); }

            long handle = Native.WaitForNewWindow(before, 8000);
            if (handle != 0)
            {
                Store.SetWindow(session.name, handle);
                // A fresh window holds exactly this session's pane.
                Store.SetPane(session.name, Tabs.FocusedPaneId(handle));
            }
        }

        private static void StartTerminal(string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("wt.exe", arguments);
            start.UseShellExecute = true;
            Process.Start(start);
        }
    }
}

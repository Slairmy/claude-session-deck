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
                Native.Focus(window);
                if (Tabs.FocusPane(window, session.pane)) { return "focused (pane)"; }
                if (Tabs.Select(window, session.name)) { return "focused"; }
                return Tabs.SelectPane(window, session.name) ? "focused (pane)" : "focused (tab not found)";
            }
            Launch(session);
            return "launched";
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
            if (handle != 0) { Store.SetWindow(session.name, handle); }
        }

        private static void StartTerminal(string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("wt.exe", arguments);
            start.UseShellExecute = true;
            Process.Start(start);
        }
    }
}

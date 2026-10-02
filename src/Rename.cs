using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// Renames a running Claude Code session. Claude Code has no outside interface for this, so the
    /// "/rename" command is written into the session's own console input; the new name then shows up in
    /// the tab title, which is also how the deck finds the tab again. Only idle, open sessions are touched,
    /// and nothing is switched or focused.
    /// </summary>
    internal static class Rename
    {
        private const int WaitForTitleMs = 8000;

        public static string Run(Session session, string requestedName)
        {
            string result = RunCore(session, requestedName);
            Hook.Log("rename", session.name + " -> '" + requestedName + "': " + result);
            return result;
        }

        private static string RunCore(Session session, string requestedName)
        {
            string newName = (requestedName ?? string.Empty).Trim();
            if (!Store.IsSafeText(newName)) { return "Name is empty or contains one of ; \" ' ` $"; }
            if (newName == session.name) { return "That is already the name"; }
            if (Store.Load().Any(s => s.name == newName)) { return "Another session already uses that name"; }

            long window = Store.WindowOf(session.name);
            // Claude Code reports whether the session is idle itself, which does not depend on any tab.
            LiveClaude live = ClaudeRegistry.Find(session.pid);
            if (live != null)
            {
                if (live.Status != "idle") { return "The session is busy; try again when it is waiting for input"; }
            }
            else
            {
                // No report (older Claude Code): fall back to the tab title.
                if (window == 0) { return "Open the session first: a closed session cannot be renamed from here"; }
                System.Windows.Automation.AutomationElement tab = Tabs.Find(window, session.name);
                if (tab == null)
                {
                    // The tab title no longer carries the session name (for example after a /rename done
                    // by hand). A window with a single tab can still be told apart.
                    List<System.Windows.Automation.AutomationElement> tabs = Tabs.Of(window);
                    if (tabs.Count == 1) { tab = tabs[0]; }
                }
                // A session in a split pane has no tab title of its own, but its pane is known.
                if (tab == null && string.IsNullOrEmpty(session.pane))
                {
                    return "Could not tell which tab is this session's: none is titled '" + session.name + "' and the window has several";
                }
                if (tab != null && !Tabs.IsIdle(Tabs.TitleOf(tab))) { return "The session is busy; try again when it is waiting for input"; }
            }

            if (!Native.IsProcessNamed(session.pid, "claude"))
            {
                return "Do not know this session's process yet; restart the session once so the deck can learn it";
            }

            string transcript = TranscriptPath(session.id);
            long transcriptLength = transcript == null ? 0 : new FileInfo(transcript).Length;

            Native.SendToConsole(session.pid, "/rename " + newName, false);
            Thread.Sleep(250); // keep the text and the Enter apart so Enter is not read as part of a paste
            Native.SendToConsole(session.pid, string.Empty, true);

            int waited = 0;
            while (waited < WaitForTitleMs)
            {
                Thread.Sleep(200);
                waited += 200;
                // The tab shows only the focused pane's title, so the session's own transcript is the surer sign.
                // Claude Code's own report is the surest sign; the tab title and the transcript cover older versions.
                LiveClaude now = ClaudeRegistry.Find(session.pid);
                if ((now != null && now.Name == newName) || (window != 0 && Tabs.HasExact(window, newName)) || TitleWritten(transcript, transcriptLength, newName))
                {
                    Store.RenameSession(session.name, newName);
                    return "Renamed to '" + newName + "'";
                }
            }
            return "Sent /rename, but the tab title did not change, so the name was left as it was";
        }

        private static string TranscriptPath(string sessionId)
        {
            string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(projects)) { return null; }
            foreach (string directory in Directory.GetDirectories(projects))
            {
                string candidate = Path.Combine(directory, sessionId + ".jsonl");
                if (File.Exists(candidate)) { return candidate; }
            }
            return null;
        }

        // Claude Code appends a "custom-title" entry to the session's transcript when /rename succeeds.
        private static bool TitleWritten(string transcript, long fromLength, string name)
        {
            if (transcript == null) { return false; }
            try
            {
                using (FileStream file = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (file.Length <= fromLength) { return false; }
                    file.Seek(fromLength, SeekOrigin.Begin);
                    byte[] added = new byte[file.Length - fromLength];
                    int read = file.Read(added, 0, added.Length);
                    return Encoding.UTF8.GetString(added, 0, read).Contains("\"customTitle\":\"" + name + "\"");
                }
            }
            catch (IOException)
            {
                return false; // being written right now; the next poll will see it
            }
        }
    }
}

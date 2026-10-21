using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// Claude Code SessionStart / SessionEnd hook. Registers sessions the user started by hand in a
    /// Windows Terminal window, and starts the floating session list the first time. Never throws: every outcome,
    /// including why a session was skipped, goes to hook.log.
    /// </summary>
    internal static class Hook
    {
        public static void Run(string eventName)
        {
            try
            {
                RunCore(eventName);
            }
            catch (Exception ex)
            {
                Log(eventName, "ERROR: " + ex.Message);
            }
        }

        public static void Log(string eventName, string message)
        {
            string line = DateTime.Now.ToString("s") + " [" + eventName + "] " + message + Environment.NewLine;
            File.AppendAllText(Path.Combine(Store.Root, "hook.log"), line, new UTF8Encoding(false));
        }

        /// <summary>For diagnostics inside the click path: a locked log file must never make opening a session fail.</summary>
        public static void LogQuietly(string eventName, string message)
        {
            try { Log(eventName, message); }
            catch (Exception) { }
        }

        private static void RunCore(string eventName)
        {
            Dictionary<string, object> payload = ReadPayload();
            string sessionId = Text(payload, "session_id");
            if (sessionId.Length == 0) { Log(eventName, "skip: payload has no session_id"); return; }
            if (Native.IsHeadlessRun()) { Log(eventName, "skip: headless run " + sessionId); return; }

            if (eventName == "end") { End(payload, sessionId); }
            else { Start(payload, sessionId); }
        }

        private static void End(Dictionary<string, object> payload, string sessionId)
        {
            string reason = Text(payload, "reason");
            if (reason != "clear")
            {
                Store.WithLock(delegate { Store.Save(Store.Load().Where(s => s.id != sessionId).ToList()); });
            }
            Log("end", "removed " + sessionId + " (reason=" + reason + ")");
        }

        private static void Start(Dictionary<string, object> payload, string sessionId)
        {
            string source = Text(payload, "source");
            if (source == "compact") { Log("start", "skip: compact"); return; }

            // WT_SESSION is not passed on to hooks, and the foreground window is only where the user happens to be
            // looking. Ask the system which terminal shows claude's console; use the foreground only as a last resort.
            int claudePid = Native.FindAncestor("claude");
            long foreground = Native.Foreground();
            long window = Native.TerminalWindowOfConsole(claudePid);
            if (window == 0) { window = Native.TerminalWindowOfAncestor(); }
            if (window == 0 && Native.IsTerminal(foreground)) { window = foreground; }
            if (window == 0)
            {
                Log("start", "skip: no Windows Terminal window found for pid=" + claudePid + " (foreground=" + foreground + ")");
                return;
            }
            // Only the window being typed in has the focus that identifies the pane.
            bool typingInTerminal = window == foreground;
            foreground = window;

            string givenTitle = Store.ToSafeName(Text(payload, "session_title"));
            string cwd = Text(payload, "cwd");
            string pane = typingInTerminal ? Tabs.FocusedPaneId(foreground) : string.Empty;
            string name = null;
            string previousName = null;
            LiveClaude running = ClaudeRegistry.Find(claudePid);
            string procStart = running == null ? null : running.ProcStart;
            Store.PruneDead();
            Store.WithLock(delegate
            {
                List<Session> sessions = Store.Load();
                Session existing = sessions.FirstOrDefault(s => s.id == sessionId);
                if (existing == null && source == "clear") { existing = SessionForClear(sessions, foreground); }

                if (existing != null)
                {
                    previousName = existing.name;
                    name = ChooseName(sessions, existing, givenTitle);
                    // A restart outside the terminal's focus cannot tell the pane; keep what was learned before.
                    string knownPane = pane.Length > 0 ? pane : (existing.pane ?? string.Empty);
                    sessions[sessions.IndexOf(existing)] = new Session { name = name, dir = cwd, id = sessionId, started = true, pid = claudePid, procStart = procStart, pane = knownPane };
                }
                else
                {
                    name = ChooseNewName(sessions, givenTitle, cwd, sessionId);
                    sessions.Add(new Session { name = name, dir = cwd, id = sessionId, started = true, pid = claudePid, procStart = procStart, pane = pane });
                }
                Store.Save(sessions);
            });

            if (previousName != null && previousName != name) { Store.ForgetWindow(previousName); }
            Store.SetWindow(name, foreground);
            Log("start", "registered '" + name + "' handle=" + foreground + " pid=" + claudePid + " pane=" + pane + " source=" + source);

            // Give untitled sessions a unique title so their tab can be found again later.
            if (givenTitle.Length == 0 && source != "clear") { EmitSessionTitle(name); }
            Overlay.EnsureRunning();
        }

        // After /clear the session id changes; keep the entry that owns this tab so its name survives.
        private static Session SessionForClear(List<Session> sessions, long window)
        {
            Dictionary<string, long> windows = Store.LoadWindows();
            List<Session> inWindow = new List<Session>();
            foreach (Session session in sessions)
            {
                long handle;
                if (windows.TryGetValue(session.name, out handle) && handle == window) { inWindow.Add(session); }
            }
            if (inWindow.Count <= 1) { return inWindow.FirstOrDefault(); }

            string selected = Tabs.StripDecoration(Tabs.SelectedTitle(window));
            return inWindow.FirstOrDefault(s => string.Equals(s.name, selected, StringComparison.OrdinalIgnoreCase));
        }

        private static string ChooseName(List<Session> sessions, Session existing, string givenTitle)
        {
            bool taken = givenTitle.Length > 0 && sessions.Any(s => s != existing && s.name == givenTitle);
            return (givenTitle.Length > 0 && !taken) ? givenTitle : existing.name;
        }

        private static string ChooseNewName(List<Session> sessions, string givenTitle, string cwd, string sessionId)
        {
            string suffix = sessionId.Substring(0, Math.Min(4, sessionId.Length));
            string leaf = Store.ToSafeName(Path.GetFileName((cwd ?? string.Empty).TrimEnd('\\', '/')));
            string name = givenTitle.Length > 0 ? givenTitle : (leaf.Length > 0 ? leaf : "claude") + "-" + suffix;
            return sessions.Any(s => s.name == name) ? name + "-" + suffix : name;
        }

        private static void EmitSessionTitle(string name)
        {
            Dictionary<string, object> specific = new Dictionary<string, object>();
            specific["hookEventName"] = "SessionStart";
            specific["sessionTitle"] = name;
            Dictionary<string, object> output = new Dictionary<string, object>();
            output["hookSpecificOutput"] = specific;

            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(output));
            using (Stream stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
            }
        }

        private static Dictionary<string, object> ReadPayload()
        {
            using (Stream stdin = Console.OpenStandardInput())
            using (StreamReader reader = new StreamReader(stdin, new UTF8Encoding(false)))
            {
                string json = reader.ReadToEnd();
                return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            }
        }

        private static string Text(Dictionary<string, object> payload, string key)
        {
            object value;
            return payload.TryGetValue(key, out value) && value != null ? value.ToString() : string.Empty;
        }
    }
}

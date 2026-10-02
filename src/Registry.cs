using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeSessionDeck
{
    /// <summary>One interactive Claude Code process, as Claude Code itself reports it.</summary>
    internal sealed class LiveClaude
    {
        public int Pid;
        public string SessionId;
        public string Cwd;
        public string Name;
        public bool NameIsUserSet;
        public string Status; // "busy", "idle", ...; empty when unknown
        public string ProcStart; // the process creation time (FILETIME) Claude Code recorded; guards against pid reuse
    }

    /// <summary>
    /// Reads ~/.claude/sessions/&lt;pid&gt;.json, which every running Claude Code process maintains. Unlike the
    /// SessionStart/SessionEnd hooks it cannot be missed (a killed terminal just leaves a file whose process
    /// is gone), and unlike tab titles it does not depend on how the terminal renders anything.
    /// </summary>
    internal static class ClaudeRegistry
    {
        // FILETIME ticks are 100 ns: five seconds of slack between Claude Code's clock reading and the OS's.
        private const long StartSlackTicks = 50000000L;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string Directory
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("DECK_CLAUDE_HOME");
                if (string.IsNullOrEmpty(home)) { home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"); }
                return Path.Combine(home, "sessions");
            }
        }

        /// <summary>Interactive sessions whose process is still the one that wrote the file.</summary>
        public static List<LiveClaude> ReadLive()
        {
            List<LiveClaude> live = new List<LiveClaude>();
            string directory = Directory;
            if (!System.IO.Directory.Exists(directory)) { return live; }
            foreach (string file in System.IO.Directory.GetFiles(directory, "*.json"))
            {
                LiveClaude entry = TryParse(file);
                if (entry != null && IsSameProcess(entry)) { live.Add(entry); }
            }
            return live;
        }

        public static LiveClaude Find(int pid)
        {
            return pid <= 0 ? null : ReadLive().FirstOrDefault(e => e.Pid == pid);
        }

        /// <summary>Parses one registry file; null when it is not an interactive session or is unreadable right now.</summary>
        public static LiveClaude Parse(string json)
        {
            Dictionary<string, object> values;
            try { values = Json.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; } // half-written file: the next poll will see it whole
            catch (InvalidOperationException) { return null; }
            if (values == null) { return null; }

            string kind = Text(values, "kind");
            if (kind.Length > 0 && kind != "interactive") { return null; }
            int pid;
            string sessionId = Text(values, "sessionId");
            if (sessionId.Length == 0 || !int.TryParse(Text(values, "pid"), out pid) || pid <= 0) { return null; }

            return new LiveClaude
            {
                Pid = pid,
                SessionId = sessionId,
                Cwd = Text(values, "cwd"),
                Name = Text(values, "name"),
                NameIsUserSet = Text(values, "nameSource") == "user",
                Status = Text(values, "status"),
                ProcStart = Text(values, "procStart")
            };
        }

        private static LiveClaude TryParse(string file)
        {
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream))
                {
                    return Parse(reader.ReadToEnd());
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool IsSameProcess(LiveClaude entry)
        {
            if (!Native.IsProcessNamed(entry.Pid, "claude")) { return false; }
            long recorded;
            if (!long.TryParse(entry.ProcStart, out recorded)) { return true; } // nothing to compare against
            try
            {
                using (Process process = Process.GetProcessById(entry.Pid))
                {
                    return Math.Abs(process.StartTime.ToFileTimeUtc() - recorded) < StartSlackTicks;
                }
            }
            catch (Exception) { return true; } // cannot inspect it (access denied, exited meanwhile): trust the name check
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? value.ToString() : string.Empty;
        }
    }
}

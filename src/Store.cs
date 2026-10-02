using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeSessionDeck
{
    internal sealed class Session
    {
        public string name { get; set; }
        public string dir { get; set; }
        public string id { get; set; }
        public bool started { get; set; }
        public int pid { get; set; } // the claude.exe process, learned from the hook; 0 when unknown
        public string procStart { get; set; } // that process's creation time as Claude Code recorded it; tells a reused pid from the original
        public string pane { get; set; } // UIA runtime id of the terminal pane it runs in, learned from the hook; empty when unknown
    }

    /// <summary>
    /// sessions.json: the registered sessions. sessions.windows.json: session name -> terminal window
    /// handle (kept apart because handles die with the window). Both are plain UTF-8 JSON.
    /// </summary>
    internal static class Store
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string Root
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private static string SessionsPath
        {
            get
            {
                string overridePath = Environment.GetEnvironmentVariable("DECK_STORE");
                return string.IsNullOrEmpty(overridePath) ? Path.Combine(Root, "sessions.json") : overridePath;
            }
        }

        private static string WindowsPath
        {
            get { return Path.ChangeExtension(SessionsPath, ".windows.json"); }
        }

        /// <summary>Serialises read-modify-write cycles across the hook processes and the floating window.</summary>
        public static void WithLock(Action action)
        {
            using (Mutex mutex = new Mutex(false, "claude-session-deck-store-lock"))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { owned = true; }
                    action();
                }
                finally
                {
                    if (owned) { mutex.ReleaseMutex(); }
                }
            }
        }

        public static List<Session> Load()
        {
            string raw = ReadText(SessionsPath);
            if (raw.Length == 0) { return new List<Session>(); }
            if (raw.TrimStart().StartsWith("{"))
            {
                return new List<Session> { Json.Deserialize<Session>(raw) };
            }
            return Json.Deserialize<List<Session>>(raw) ?? new List<Session>();
        }

        public static void Save(List<Session> sessions)
        {
            WriteAtomic(SessionsPath, Json.Serialize(sessions));
        }

        public static Dictionary<string, long> LoadWindows()
        {
            Dictionary<string, long> result = new Dictionary<string, long>();
            string raw = ReadText(WindowsPath);
            if (raw.Length == 0) { return result; }
            Dictionary<string, object> parsed = Json.Deserialize<Dictionary<string, object>>(raw);
            foreach (KeyValuePair<string, object> pair in parsed)
            {
                result[pair.Key] = Convert.ToInt64(pair.Value);
            }
            return result;
        }

        /// <summary>
        /// Drops sessions whose claude process is gone. SessionEnd never runs when the terminal is closed or the
        /// process is killed, so without this such entries stay forever. Sessions with an unknown pid (0) are kept.
        /// </summary>
        public static void PruneDead()
        {
            if (!Load().Any(IsDead)) { return; } // cheap check first: no lock, no write

            List<string> removed = new List<string>();
            WithLock(delegate
            {
                List<Session> sessions = Load();
                List<Session> alive = sessions.Where(s => !IsDead(s)).ToList();
                if (alive.Count == sessions.Count) { return; }
                removed.AddRange(sessions.Where(IsDead).Select(s => s.name));
                Save(alive);
            });
            foreach (string name in removed) { ForgetWindow(name); }
        }

        private static bool IsDead(Session session)
        {
            return session.pid > 0 && !Native.IsProcessNamed(session.pid, "claude");
        }

        public static void SetWindow(string name, long handle)
        {
            WithLock(delegate
            {
                Dictionary<string, long> windows = LoadWindows();
                windows[name] = handle;
                WriteAtomic(WindowsPath, Json.Serialize(windows));
            });
        }

        /// <summary>Forgets windows recorded for names that are no longer sessions (left behind by removals made before windows were cleaned up with them).</summary>
        public static void PruneWindows(ICollection<string> sessionNames)
        {
            WithLock(delegate
            {
                Dictionary<string, long> windows = LoadWindows();
                List<string> orphans = windows.Keys.Where(k => !sessionNames.Contains(k)).ToList();
                if (orphans.Count == 0) { return; }
                foreach (string name in orphans) { windows.Remove(name); }
                WriteAtomic(WindowsPath, Json.Serialize(windows));
            });
        }

        /// <summary>Keeps a session's window when the session is renamed.</summary>
        public static void MoveWindow(string oldName, string newName)
        {
            WithLock(delegate
            {
                Dictionary<string, long> windows = LoadWindows();
                long handle;
                if (!windows.TryGetValue(oldName, out handle)) { return; }
                windows.Remove(oldName);
                windows[newName] = handle;
                WriteAtomic(WindowsPath, Json.Serialize(windows));
            });
        }

        public static void ForgetWindow(string name)
        {
            WithLock(delegate
            {
                Dictionary<string, long> windows = LoadWindows();
                if (windows.Remove(name)) { WriteAtomic(WindowsPath, Json.Serialize(windows)); }
            });
        }

        /// <summary>The live terminal window of a session, or 0 when it is not open.</summary>
        public static long WindowOf(string name)
        {
            long handle;
            if (LoadWindows().TryGetValue(name, out handle) && Native.IsTerminal(handle)) { return handle; }
            return 0;
        }

        /// <summary>Changes a session's name and moves its window handle to the new name.</summary>
        public static void RenameSession(string oldName, string newName)
        {
            WithLock(delegate
            {
                List<Session> sessions = Load();
                Session target = sessions.FirstOrDefault(s => s.name == oldName);
                if (target == null) { return; }
                target.name = newName;
                Save(sessions);

                Dictionary<string, long> windows = LoadWindows();
                long handle;
                if (windows.TryGetValue(oldName, out handle))
                {
                    windows.Remove(oldName);
                    windows[newName] = handle;
                    WriteAtomic(WindowsPath, Json.Serialize(windows));
                }
            });
        }

        public static void MarkStarted(string name)
        {
            WithLock(delegate
            {
                List<Session> sessions = Load();
                Session target = sessions.FirstOrDefault(s => s.name == name);
                if (target == null) { return; }
                target.started = true;
                Save(sessions);
            });
        }

        // Characters that would break the wt / cmd command lines.
        public static bool IsSafeText(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && text.IndexOfAny(new[] { ';', '"', '\'', '`', '$' }) < 0;
        }

        public static string ToSafeName(string text)
        {
            StringBuilder cleaned = new StringBuilder();
            foreach (char c in text ?? string.Empty)
            {
                cleaned.Append(";\"'`$".IndexOf(c) >= 0 ? '_' : c);
            }
            return cleaned.ToString().Trim();
        }

        // Readers (the floating window) never see a half-written file: write aside, then swap in.
        private static void WriteAtomic(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, Utf8);
            if (File.Exists(path)) { File.Replace(temp, path, null); }
            else { File.Move(temp, path); }
        }

        private static string ReadText(string path)
        {
            if (!File.Exists(path)) { return string.Empty; }
            string raw = File.ReadAllText(path, Utf8);
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw;
        }
    }
}

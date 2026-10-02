using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeSessionDeck
{
    internal sealed class ReconcilePlan
    {
        public List<Session> Sessions = new List<Session>();
        public List<string> Removed = new List<string>();
        public List<string> Added = new List<string>();
        public Dictionary<string, string> Renamed = new Dictionary<string, string>();
        public bool Changed;
    }

    /// <summary>
    /// Brings sessions.json in line with the Claude Code processes that are really running (ClaudeRegistry).
    /// The SessionStart/SessionEnd hooks are the fast path; this is the safety net for everything they can miss:
    /// a session whose hook never ran or was skipped, a terminal closed without SessionEnd, a pid that was reused,
    /// a /clear that changed the session id, a /rename done inside claude.
    /// </summary>
    internal static class Reconciler
    {
        /// <summary>The decision, with no side effects. <paramref name="processAlive"/> is only asked about sessions the registry does not list.</summary>
        public static ReconcilePlan Plan(List<Session> stored, List<LiveClaude> live, Func<Session, bool> processAlive, Func<LiveClaude, bool> wantNew)
        {
            ReconcilePlan plan = new ReconcilePlan();
            HashSet<LiveClaude> claimed = new HashSet<LiveClaude>();
            HashSet<string> taken = new HashSet<string>(stored.Select(s => s.name));

            foreach (Session session in stored)
            {
                bool stale;
                LiveClaude match = MatchLive(session, live, claimed, out stale);
                if (match == null)
                {
                    if (stale || (session.pid > 0 && !processAlive(session)))
                    {
                        plan.Removed.Add(session.name);
                        taken.Remove(session.name);
                    }
                    else { plan.Sessions.Add(session); }
                    continue;
                }
                claimed.Add(match);
                plan.Sessions.Add(Adopt(session, match, taken, plan));
            }

            foreach (LiveClaude entry in live)
            {
                if (claimed.Contains(entry) || !wantNew(entry)) { continue; }
                Session added = NewSession(entry, taken);
                plan.Sessions.Add(added);
                plan.Added.Add(added.name);
            }

            plan.Changed = plan.Removed.Count > 0 || plan.Added.Count > 0 || !SameSessions(stored, plan.Sessions);
            return plan;
        }

        /// <summary>Reads the registry and applies the plan to sessions.json and the window map.</summary>
        public static ReconcilePlan Run(List<LiveClaude> live, Func<LiveClaude, bool> wantNew)
        {
            Func<Session, bool> alive = s => Native.IsProcessNamed(s.pid, "claude");
            if (!Plan(Store.Load(), live, alive, wantNew).Changed) { return new ReconcilePlan(); } // common case: no lock, no write

            ReconcilePlan applied = null;
            Store.WithLock(delegate
            {
                applied = Plan(Store.Load(), live, alive, wantNew);
                if (applied.Changed) { Store.Save(applied.Sessions); }
            });

            foreach (string name in applied.Removed) { Store.ForgetWindow(name); }
            foreach (KeyValuePair<string, string> rename in applied.Renamed) { Store.MoveWindow(rename.Key, rename.Value); }
            if (applied.Changed)
            {
                Hook.Log("reconcile", "added=[" + string.Join(", ", applied.Added) + "] removed=[" + string.Join(", ", applied.Removed)
                    + "] renamed=[" + string.Join(", ", applied.Renamed.Select(r => r.Key + "->" + r.Value)) + "]");
            }
            return applied;
        }

        /// <summary>
        /// Keeps each live session's terminal window current: looked up again every few seconds, because a tab
        /// dragged into another window gets a new window handle. Sessions the deck cannot place (not in Windows Terminal) are left alone.
        /// </summary>
        public static void SyncWindows(List<LiveClaude> live)
        {
            List<Session> sessions = Store.Load();
            Store.PruneWindows(new HashSet<string>(sessions.Select(s => s.name)));
            Dictionary<string, long> known = Store.LoadWindows();
            foreach (Session session in sessions)
            {
                if (session.pid <= 0 || !live.Any(e => e.Pid == session.pid)) { continue; }
                long window = ConsoleWindows.Of(session.pid);
                long current;
                if (window != 0 && (!known.TryGetValue(session.name, out current) || current != window)) { Store.SetWindow(session.name, window); }
            }
        }

        // The registry entry for this session, or null. A reused pid (same number, different process) is "stale", not a match.
        private static LiveClaude MatchLive(Session session, List<LiveClaude> live, HashSet<LiveClaude> claimed, out bool stale)
        {
            stale = false;
            if (session.pid > 0)
            {
                LiveClaude byPid = live.FirstOrDefault(e => e.Pid == session.pid && !claimed.Contains(e));
                if (byPid != null)
                {
                    bool reused = !string.IsNullOrEmpty(session.procStart) && !string.IsNullOrEmpty(byPid.ProcStart) && session.procStart != byPid.ProcStart;
                    if (!reused) { return byPid; }
                    stale = true;
                }
            }
            LiveClaude byId = live.FirstOrDefault(e => e.SessionId == session.id && !claimed.Contains(e));
            if (byId != null) { stale = false; }
            // Same process as a session that was already matched: a leftover duplicate entry.
            else if (session.pid > 0 && claimed.Any(e => e.Pid == session.pid)) { stale = true; }
            return byId;
        }

        private static Session Adopt(Session session, LiveClaude entry, HashSet<string> taken, ReconcilePlan plan)
        {
            string name = session.name;
            string wanted = Store.ToSafeName(entry.Name);
            if (entry.NameIsUserSet && wanted.Length > 0 && wanted != session.name && !taken.Contains(wanted))
            {
                taken.Remove(session.name);
                taken.Add(wanted);
                plan.Renamed[session.name] = wanted;
                name = wanted;
            }
            return new Session
            {
                name = name,
                dir = string.IsNullOrEmpty(session.dir) ? entry.Cwd : session.dir,
                id = entry.SessionId,
                started = true,
                pid = entry.Pid,
                procStart = entry.ProcStart,
                pane = session.pane
            };
        }

        private static Session NewSession(LiveClaude entry, HashSet<string> taken)
        {
            string suffix = entry.SessionId.Substring(0, Math.Min(4, entry.SessionId.Length));
            string given = entry.NameIsUserSet ? Store.ToSafeName(entry.Name) : string.Empty;
            string leaf = Store.ToSafeName(Path.GetFileName((entry.Cwd ?? string.Empty).TrimEnd('\\', '/')));
            string name = given.Length > 0 ? given : (leaf.Length > 0 ? leaf : "claude") + "-" + suffix;
            if (taken.Contains(name)) { name = name + "-" + suffix; }
            taken.Add(name);
            return new Session { name = name, dir = entry.Cwd, id = entry.SessionId, started = true, pid = entry.Pid, procStart = entry.ProcStart, pane = string.Empty };
        }

        private static bool SameSessions(List<Session> a, List<Session> b)
        {
            if (a.Count != b.Count) { return false; }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].name != b[i].name || a[i].dir != b[i].dir || a[i].id != b[i].id || a[i].started != b[i].started
                    || a[i].pid != b[i].pid || a[i].procStart != b[i].procStart || a[i].pane != b[i].pane) { return false; }
            }
            return true;
        }
    }

    /// <summary>
    /// Which Windows Terminal window shows a process's console, with a short memory so the floating window's
    /// one-second refresh does not attach to a console every tick.
    /// </summary>
    internal static class ConsoleWindows
    {
        private const double FoundSeconds = 10;
        private const double MissSeconds = 30;
        private static readonly Dictionary<int, KeyValuePair<long, DateTime>> Cache = new Dictionary<int, KeyValuePair<long, DateTime>>();

        public static long Of(int pid)
        {
            lock (Cache)
            {
                KeyValuePair<long, DateTime> hit;
                if (Cache.TryGetValue(pid, out hit) && (DateTime.UtcNow - hit.Value).TotalSeconds < (hit.Key != 0 ? FoundSeconds : MissSeconds)) { return hit.Key; }
            }
            long window = Native.TerminalWindowOfConsole(pid);
            lock (Cache) { Cache[pid] = new KeyValuePair<long, DateTime>(window, DateTime.UtcNow); }
            return window;
        }

        public static void Forget(int pid)
        {
            lock (Cache) { Cache.Remove(pid); }
        }
    }
}

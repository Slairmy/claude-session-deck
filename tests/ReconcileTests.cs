using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeSessionDeck.Tests
{
    // A tiny runner: this project has no test framework and is built with the compiler that ships with Windows.
    internal static class Runner
    {
        private static int failures;
        private static int passed;

        private static int Main()
        {
            Run("registry: an interactive session parses", ParsesInteractive);
            Run("registry: non-interactive, broken and incomplete files are ignored", IgnoresUnusable);
            Run("a running session the hook never registered is added", AddsUnregistered);
            Run("a session outside Windows Terminal is not added", SkipsUnwanted);
            Run("a session whose process is gone is removed", RemovesDead);
            Run("a session with an unknown pid is kept", KeepsUnknownPid);
            Run("a live process the registry does not list is kept", KeepsAliveUnlisted);
            Run("/clear: the new session id is adopted, the name kept", AdoptsNewIdAfterClear);
            Run("a session registered without a pid learns it from the registry", LearnsPid);
            Run("a reused pid is not mistaken for the old session", DetectsPidReuse);
            Run("a name chosen inside claude is adopted", AdoptsUserName);
            Run("a name that would clash is not adopted", KeepsNameOnClash);
            Run("a name claude generated itself is not adopted", IgnoresAutoName);
            Run("a duplicate entry for one process is dropped", DropsDuplicate);
            Run("nothing to change reports no change", ReportsNoChange);

            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("  ok    " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("  FAIL  " + name + "\n        " + ex.Message); }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) { throw new Exception(message); }
        }

        private static Session Stored(string name, string id, int pid, string procStart)
        {
            return new Session { name = name, dir = "C:\\work", id = id, started = true, pid = pid, procStart = procStart, pane = string.Empty };
        }

        private static LiveClaude Live(string id, int pid, string procStart, string name, bool userSet, string status)
        {
            return new LiveClaude { Pid = pid, SessionId = id, Cwd = "C:\\work", Name = name, NameIsUserSet = userSet, Status = status, ProcStart = procStart };
        }

        private static ReconcilePlan Plan(List<Session> stored, List<LiveClaude> live, bool processAlive, bool wantNew)
        {
            return Reconciler.Plan(stored, live, s => processAlive, e => wantNew);
        }

        private static List<Session> One(Session s) { return new List<Session> { s }; }
        private static List<LiveClaude> One(LiveClaude e) { return new List<LiveClaude> { e }; }
        private static List<Session> None() { return new List<Session>(); }

        private static void ParsesInteractive()
        {
            LiveClaude e = ClaudeRegistry.Parse("{\"pid\":13872,\"sessionId\":\"abc\",\"cwd\":\"C:\\\\x\",\"kind\":\"interactive\",\"name\":\"n\",\"nameSource\":\"user\",\"status\":\"busy\",\"procStart\":\"134353976259556480\"}");
            Check(e != null && e.Pid == 13872 && e.SessionId == "abc" && e.Cwd == "C:\\x", "fields not read");
            Check(e.NameIsUserSet && e.Name == "n" && e.Status == "busy" && e.ProcStart == "134353976259556480", "name/status/procStart not read");
        }

        private static void IgnoresUnusable()
        {
            Check(ClaudeRegistry.Parse("{\"pid\":1,\"sessionId\":\"a\",\"kind\":\"sdk-cli\"}") == null, "non-interactive kind accepted");
            Check(ClaudeRegistry.Parse("{\"pid\":1,\"sessionId\":") == null, "half-written file accepted");
            Check(ClaudeRegistry.Parse("") == null, "empty file accepted");
            Check(ClaudeRegistry.Parse("{\"pid\":1}") == null, "file without a session id accepted");
            Check(ClaudeRegistry.Parse("{\"sessionId\":\"a\"}") == null, "file without a pid accepted");
        }

        private static void AddsUnregistered()
        {
            ReconcilePlan plan = Plan(None(), One(Live("abcd1234", 10, "111", "mine", true, "idle")), true, true);
            Check(plan.Changed && plan.Added.Count == 1, "not added");
            Session s = plan.Sessions.Single();
            Check(s.name == "mine" && s.id == "abcd1234" && s.pid == 10 && s.procStart == "111" && s.started, "wrong fields: " + s.name);
        }

        private static void SkipsUnwanted()
        {
            ReconcilePlan plan = Plan(None(), One(Live("abcd1234", 10, "111", "mine", true, "idle")), true, false);
            Check(plan.Sessions.Count == 0 && !plan.Changed, "added although not wanted");
        }

        private static void RemovesDead()
        {
            ReconcilePlan plan = Plan(One(Stored("old", "s1", 10, "111")), new List<LiveClaude>(), false, true);
            Check(plan.Sessions.Count == 0 && plan.Removed.SequenceEqual(new[] { "old" }), "dead session kept");
        }

        private static void KeepsUnknownPid()
        {
            ReconcilePlan plan = Plan(One(Stored("manual", "s1", 0, null)), new List<LiveClaude>(), false, true);
            Check(plan.Sessions.Count == 1 && !plan.Changed, "pid-less session dropped");
        }

        private static void KeepsAliveUnlisted()
        {
            ReconcilePlan plan = Plan(One(Stored("quiet", "s1", 10, null)), new List<LiveClaude>(), true, true);
            Check(plan.Sessions.Count == 1 && !plan.Changed, "live session dropped when the registry had no file for it");
        }

        private static void AdoptsNewIdAfterClear()
        {
            ReconcilePlan plan = Plan(One(Stored("work", "old-id", 10, "111")), One(Live("new-id", 10, "111", "", false, "idle")), true, true);
            Session s = plan.Sessions.Single();
            Check(s.id == "new-id" && s.name == "work" && plan.Removed.Count == 0, "id not updated or session replaced");
        }

        private static void LearnsPid()
        {
            ReconcilePlan plan = Plan(One(Stored("manual", "s1", 0, null)), One(Live("s1", 42, "999", "manual", false, "idle")), true, true);
            Check(plan.Sessions.Single().pid == 42 && plan.Sessions.Single().procStart == "999", "pid not learned");
        }

        private static void DetectsPidReuse()
        {
            ReconcilePlan plan = Plan(One(Stored("old", "s1", 10, "111")), One(Live("other", 10, "222", "other", true, "busy")), true, true);
            Check(plan.Removed.SequenceEqual(new[] { "old" }), "reused pid matched the old session");
            Check(plan.Sessions.Count == 1 && plan.Sessions[0].id == "other" && plan.Sessions[0].name == "other", "new process not registered as its own session");
        }

        private static void AdoptsUserName()
        {
            ReconcilePlan plan = Plan(One(Stored("old-name", "s1", 10, "111")), One(Live("s1", 10, "111", "new-name", true, "idle")), true, true);
            Check(plan.Sessions.Single().name == "new-name" && plan.Renamed["old-name"] == "new-name", "rename not adopted");
        }

        private static void KeepsNameOnClash()
        {
            List<Session> stored = new List<Session> { Stored("a", "s1", 10, "111"), Stored("b", "s2", 11, "222") };
            ReconcilePlan plan = Plan(stored, One(Live("s1", 10, "111", "b", true, "idle")), true, true);
            Check(plan.Sessions[0].name == "a" && plan.Renamed.Count == 0, "renamed onto a name already in use");
        }

        private static void IgnoresAutoName()
        {
            ReconcilePlan plan = Plan(One(Stored("mine", "s1", 10, "111")), One(Live("s1", 10, "111", "auto-title", false, "idle")), true, true);
            Check(plan.Sessions.Single().name == "mine" && !plan.Changed, "auto-generated name overwrote the chosen one");
        }

        private static void DropsDuplicate()
        {
            List<Session> stored = new List<Session> { Stored("a", "s1", 10, "111"), Stored("a-copy", "s0", 10, "111") };
            ReconcilePlan plan = Plan(stored, One(Live("s1", 10, "111", "", false, "idle")), true, true);
            Check(plan.Sessions.Count == 1 && plan.Sessions[0].name == "a" && plan.Removed.SequenceEqual(new[] { "a-copy" }), "duplicate kept");
        }

        private static void ReportsNoChange()
        {
            ReconcilePlan plan = Plan(One(Stored("a", "s1", 10, "111")), One(Live("s1", 10, "111", "a", true, "busy")), true, true);
            Check(!plan.Changed, "reported a change when nothing differs");
        }
    }
}

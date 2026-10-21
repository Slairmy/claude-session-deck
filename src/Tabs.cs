using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// Finds and selects tabs inside a Windows Terminal window through UI Automation.
    /// Tabs are matched by the Claude session title shown in the tab ("✳ my-session"), because
    /// UIA runtime ids are not stable when tabs are added.
    /// </summary>
    internal static class Tabs
    {
        private static readonly Condition IsTabItem =
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

        public static List<AutomationElement> Of(long handle)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            try
            {
                AutomationElement window = AutomationElement.FromHandle(new IntPtr(handle));
                foreach (AutomationElement tab in window.FindAll(TreeScope.Descendants, IsTabItem))
                {
                    result.Add(tab);
                }
            }
            catch (Exception)
            {
                // The window closed while we were looking, or UIA is unavailable: report no tabs.
            }
            return result;
        }

        public static string TitleOf(AutomationElement tab)
        {
            try { return tab.Current.Name ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        // "✳ my-session" -> "my-session"; quotes around the title are dropped too.
        public static string StripDecoration(string title)
        {
            string text = title.Trim();
            int space = text.IndexOf(' ');
            if (space > 0 && space <= 2) { text = text.Substring(space + 1); }
            return text.Trim().Trim('"', '“', '”').Trim();
        }

        public static AutomationElement Find(long handle, string sessionName)
        {
            AutomationElement partial = null;
            foreach (AutomationElement tab in Of(handle))
            {
                string title = TitleOf(tab);
                if (string.Equals(StripDecoration(title), sessionName, StringComparison.OrdinalIgnoreCase)) { return tab; }
                if (partial == null && title.IndexOf(sessionName, StringComparison.OrdinalIgnoreCase) >= 0) { partial = tab; }
            }
            return partial;
        }

        /// <summary>
        /// '*' when the session is running, ' ' when it is not. The claude process decides: a tab shows only the focused pane's
        /// title and titles can be renamed or redrawn, so they cannot prove a session is closed. Only a session whose process
        /// is unknown (pid 0, registered by hand before it ever started) falls back to looking for its tab.
        /// </summary>
        public static char MarkOf(long window, Session session)
        {
            if (session.pid > 0) { return Native.IsProcessNamed(session.pid, "claude") ? '*' : ' '; }
            if (window == 0) { return ' '; }
            return Exists(window, session.name) ? '*' : '?';
        }

        public static bool Exists(long handle, string sessionName)
        {
            return Find(handle, sessionName) != null;
        }

        // Claude Code puts a "✳" in front of the tab title while it waits for input; a spinner glyph means busy.
        public static bool IsIdle(string title)
        {
            return title.TrimStart().StartsWith("✳");
        }

        /// <summary>A tab whose title, minus the status glyph, is exactly <paramref name="sessionName"/>.</summary>
        public static bool HasExact(long handle, string sessionName)
        {
            foreach (AutomationElement tab in Of(handle))
            {
                if (string.Equals(StripDecoration(TitleOf(tab)), sessionName, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static readonly Condition IsPane =
            new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl");

        private static string PaneId(AutomationElement pane)
        {
            try { return string.Join(".", pane.GetRuntimeId()); }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Runtime id of the pane that has keyboard focus in <paramref name="handle"/>; empty when unknown.</summary>
        public static string FocusedPaneId(long handle)
        {
            try
            {
                AutomationElement window = AutomationElement.FromHandle(new IntPtr(handle));
                foreach (AutomationElement pane in window.FindAll(TreeScope.Descendants, IsPane))
                {
                    if (pane.Current.HasKeyboardFocus) { return PaneId(pane); }
                }
            }
            catch (Exception)
            {
                // The window closed while we were looking, or UIA is unavailable: the pane stays unknown.
            }
            return string.Empty;
        }

        /// <summary>Focuses the pane with this runtime id when its tab is already showing; never switches tabs.</summary>
        public static bool FocusPaneInView(long handle, string paneId)
        {
            if (string.IsNullOrEmpty(paneId)) { return false; }
            return FocusPaneInViewCore(handle, paneId);
        }

        /// <summary>
        /// Focuses the pane with this runtime id, selecting the tab that holds it when it is not showing. Panes of tabs that
        /// are not selected do not exist in the UIA tree, so the other tabs are tried one by one (each only until its panes
        /// have rendered); the tab that was selected before is restored when the pane is nowhere.
        /// </summary>
        public static bool FocusPane(long handle, string paneId)
        {
            if (string.IsNullOrEmpty(paneId)) { return false; }
            if (FocusPaneInViewCore(handle, paneId)) { return true; }
            AutomationElement original = SelectedTab(handle);
            HashSet<string> shown = PaneIds(PanesOf(handle));
            foreach (AutomationElement tab in Of(handle))
            {
                if (original != null && Automation.Compare(tab, original)) { continue; }
                if (!SelectTab(tab)) { continue; }
                Stopwatch switched = Stopwatch.StartNew();
                shown = PaneIds(PanesAfterSwitch(handle, shown));
                bool found = FocusPaneInViewCore(handle, paneId);
                Hook.LogQuietly("open", "  walk tab '" + TitleOf(tab) + "' rendered in " + switched.ElapsedMilliseconds + "ms, pane found=" + found);
                if (found) { return true; }
            }
            Hook.LogQuietly("open", "  pane " + paneId + " is in no tab; restoring the original tab");
            Reselect(original);
            return false;
        }

        private static List<AutomationElement> PanesOf(long handle)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            try
            {
                AutomationElement window = AutomationElement.FromHandle(new IntPtr(handle));
                foreach (AutomationElement pane in window.FindAll(TreeScope.Descendants, IsPane)) { result.Add(pane); }
            }
            catch (Exception)
            {
                // Window closed or UIA unavailable: no panes.
            }
            return result;
        }

        private static HashSet<string> PaneIds(List<AutomationElement> panes)
        {
            return new HashSet<string>(panes.Select(PaneId));
        }

        /// <summary>Waits until the panes on screen are not just <paramref name="before"/>, i.e. the tab switch has rendered.</summary>
        private static List<AutomationElement> PanesAfterSwitch(long handle, HashSet<string> before)
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<AutomationElement> panes;
            do
            {
                panes = PanesOf(handle);
                if (panes.Any(p => !before.Contains(PaneId(p)))) { return panes; }
                Thread.Sleep(30);
            } while (clock.ElapsedMilliseconds < SwitchWaitMs);
            Hook.LogQuietly("open", "  TIMEOUT: the tab switch did not render new panes within " + SwitchWaitMs + "ms (machine slow?)");
            return panes;
        }

        private static AutomationElement SelectedTab(long handle)
        {
            foreach (AutomationElement tab in Of(handle))
            {
                try
                {
                    if (((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected) { return tab; }
                }
                catch (Exception)
                {
                    // Not a selectable item; keep looking.
                }
            }
            return null;
        }

        private static void Reselect(AutomationElement tab)
        {
            if (tab != null) { SelectTab(tab); }
        }

        private static bool FocusPaneInViewCore(long handle, string paneId)
        {
            try
            {
                AutomationElement window = AutomationElement.FromHandle(new IntPtr(handle));
                foreach (AutomationElement pane in window.FindAll(TreeScope.Descendants, IsPane))
                {
                    if (PaneId(pane) != paneId) { continue; }
                    pane.SetFocus();
                    return true;
                }
            }
            catch (Exception)
            {
                // Window closed or the pane went away mid-call: report not found.
            }
            return false;
        }

        private const int SwitchWaitMs = 300;
        private const int TitleWaitMs = 500;
        private const int ScanTitleWaitMs = 250;

        /// <summary>
        /// Last resort for a session whose pane is unknown and whose name is no tab's title (it sits in a split pane, and a
        /// tab only shows the focused pane's title). Focuses each pane of each tab through UIA until the tab title becomes the
        /// session name: the tab already showing is searched first, single-pane tabs are skipped, and the tab selected
        /// before is restored when nothing is found. Returns the pane's runtime id, or null.
        /// </summary>
        public static string ScanForSession(long handle, string sessionName)
        {
            AutomationElement original = SelectedTab(handle);
            List<AutomationElement> tabs = Of(handle).OrderBy(t => IsSame(t, original) ? 0 : 1).ToList();
            HashSet<string> shown = new HashSet<string>();
            foreach (AutomationElement tab in tabs)
            {
                bool showing = IsSame(tab, original);
                if (!showing && !SelectTab(tab)) { continue; }
                List<AutomationElement> panes = showing ? PanesOf(handle) : PanesAfterSwitch(handle, shown);
                shown = PaneIds(panes);
                if (panes.Count < 2)
                {
                    Hook.LogQuietly("open", "  scan tab '" + TitleOf(tab) + "': " + panes.Count + " pane(s), skipped");
                    continue;
                }
                foreach (AutomationElement pane in panes)
                {
                    if (!Focus(pane)) { continue; }
                    Stopwatch waited = Stopwatch.StartNew();
                    bool hit = WaitForTitle(handle, sessionName, ScanTitleWaitMs);
                    Hook.LogQuietly("open", "  scan tab '" + TitleOf(tab) + "' pane " + PaneId(pane) + " title now '" + SelectedTitle(handle)
                        + "' hit=" + hit + " after " + waited.ElapsedMilliseconds + "ms" + (hit ? string.Empty : " (timed out)"));
                    if (hit) { return PaneId(pane); }
                }
            }
            Hook.LogQuietly("open", "  scan found nothing; restoring the original tab");
            Reselect(original);
            return null;
        }

        private static bool IsSame(AutomationElement tab, AutomationElement other)
        {
            return other != null && Automation.Compare(tab, other);
        }

        private static bool Focus(AutomationElement pane)
        {
            try { pane.SetFocus(); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Selects the tab whose title, minus the status glyph, is exactly the session name.</summary>
        public static bool SelectExact(long handle, string sessionName)
        {
            foreach (AutomationElement tab in Of(handle))
            {
                if (string.Equals(StripDecoration(TitleOf(tab)), sessionName, StringComparison.OrdinalIgnoreCase)) { return SelectTab(tab); }
            }
            return false;
        }

        /// <summary>
        /// True only when the selected tab's title plainly names a different session, i.e. the pane that was focused is the wrong
        /// one. An unfamiliar title proves nothing (titles get renamed and redrawn), so it counts as fine.
        /// </summary>
        public static bool ShowsOtherSession(long handle, string sessionName, ICollection<string> otherNames)
        {
            if (WaitForTitle(handle, sessionName, TitleWaitMs)) { return false; }
            string title = StripDecoration(SelectedTitle(handle));
            bool other = otherNames.Any(n => string.Equals(n, title, StringComparison.OrdinalIgnoreCase));
            Hook.LogQuietly("open", "  title never became '" + sessionName + "' within " + TitleWaitMs + "ms; it is '" + title + "' (names another session=" + other + ")");
            return other;
        }

        private static bool SelectTab(AutomationElement tab)
        {
            try
            {
                ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TitleIs(long handle, string sessionName)
        {
            return string.Equals(StripDecoration(SelectedTitle(handle)), sessionName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WaitForTitle(long handle, string sessionName, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            do
            {
                if (TitleIs(handle, sessionName)) { return true; }
                Thread.Sleep(50);
            } while (clock.ElapsedMilliseconds < timeoutMs);
            return false;
        }

        public static string SelectedTitle(long handle)
        {
            foreach (AutomationElement tab in Of(handle))
            {
                try
                {
                    SelectionItemPattern pattern = (SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern);
                    if (pattern.Current.IsSelected) { return TitleOf(tab); }
                }
                catch (Exception)
                {
                    // Not a selectable item; keep looking.
                }
            }
            return string.Empty;
        }
    }
}

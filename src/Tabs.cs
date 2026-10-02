using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public static bool Select(long handle, string sessionName)
        {
            AutomationElement tab = Find(handle, sessionName);
            if (tab == null) { return false; }
            try
            {
                ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
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

        /// <summary>Focuses the pane with this runtime id, selecting the tab that holds it when needed.</summary>
        public static bool FocusPane(long handle, string paneId)
        {
            if (string.IsNullOrEmpty(paneId)) { return false; }
            if (FocusPaneInView(handle, paneId)) { return true; }
            foreach (AutomationElement tab in Of(handle))
            {
                if (SelectTab(tab) && WaitForPane(handle, paneId)) { return true; }
            }
            return false;
        }

        private static bool WaitForPane(long handle, string paneId)
        {
            Stopwatch clock = Stopwatch.StartNew();
            do
            {
                if (FocusPaneInView(handle, paneId)) { return true; }
                Thread.Sleep(50);
            } while (clock.ElapsedMilliseconds < TitleWaitMs);
            return false;
        }

        private static bool FocusPaneInView(long handle, string paneId)
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

        private const int MaxPanesPerTab = 8;
        private const int TitleWaitMs = 500;

        /// <summary>
        /// Brings a session that sits in a split pane to the front. A tab only shows the focused pane's title, so the
        /// pane is found by stepping focus through every pane of every tab until the tab title becomes the session name.
        /// The window must already be the foreground one: "wt -w 0" addresses the most recently used window.
        /// </summary>
        public static bool SelectPane(long handle, string sessionName)
        {
            List<AutomationElement> tabs = Of(handle);
            foreach (AutomationElement tab in tabs)
            {
                if (tabs.Count > 1 && !SelectTab(tab)) { continue; }
                if (TitleIs(handle, sessionName)) { return true; }
                for (int step = 0; step < MaxPanesPerTab; step++)
                {
                    MoveFocusToNextPane();
                    if (WaitForTitle(handle, sessionName)) { return true; }
                }
            }
            return false;
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

        private static bool WaitForTitle(long handle, string sessionName)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < TitleWaitMs)
            {
                if (TitleIs(handle, sessionName)) { return true; }
                Thread.Sleep(50);
            }
            return false;
        }

        private static void MoveFocusToNextPane()
        {
            ProcessStartInfo start = new ProcessStartInfo("wt.exe", "-w 0 move-focus nextInOrder");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process wt = Process.Start(start)) { wt.WaitForExit(3000); }
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

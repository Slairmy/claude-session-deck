using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeSessionDeck
{
    /// <summary>
    /// Learns which pane each session runs in while the user simply works in the terminal, so that opening a session later
    /// can go straight to its pane instead of searching. The pair "focused pane + selected tab title" is only trusted once it
    /// was seen on two consecutive looks, because a tab title lags a moment behind a focus change.
    /// </summary>
    internal static class PaneLearner
    {
        private static string lastPane = string.Empty;
        private static string lastTitle = string.Empty;

        public static void Learn(List<Session> sessions)
        {
            try
            {
                long window = Native.Foreground();
                if (!Native.IsTerminal(window)) { Reset(); return; }

                string pane = Tabs.FocusedPaneId(window);
                string title = Tabs.StripDecoration(Tabs.SelectedTitle(window));
                bool stable = pane.Length > 0 && pane == lastPane && title == lastTitle;
                lastPane = pane;
                lastTitle = title;
                if (!stable) { return; }

                Session match = sessions.FirstOrDefault(s => string.Equals(s.name, title, StringComparison.OrdinalIgnoreCase));
                if (match != null && match.pane != pane) { Store.SetPane(match.name, pane); }
            }
            catch (Exception ex)
            {
                // Learning is a convenience; the window may close mid-look.
                Hook.Log("overlay", "pane learning skipped: " + ex.Message);
            }
        }

        private static void Reset()
        {
            lastPane = string.Empty;
            lastTitle = string.Empty;
        }
    }
}

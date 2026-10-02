using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeSessionDeck
{
    internal static class Program
    {
        // Commands of claude-session-deck.exe (claude-session-deck-gui.exe is the same program that opens the floating window directly):
        //   (none) | ensure-overlay   start the floating window unless it is already running
        //   hook start|end            Claude Code SessionStart / SessionEnd hook (payload on stdin)
        //   open <name>               bring a registered session's window and tab to the front
        //   rename <old> <new>        rename a running, idle session (types /rename into it)
        //   overlay                   run the floating window in this process
        //   debug-send <pid> <text>   developer aid: type a line into another process's console
        private static int Main(string[] args)
        {
            try
            {
                return Dispatch(args);
            }
            catch (Exception ex)
            {
                Hook.Log("fatal", ex.ToString());
                return 1;
            }
        }

        private static int Dispatch(string[] args)
        {
            if (args.Length >= 2 && args[0] == "hook")
            {
                Hook.Run(args[1]);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "open")
            {
                Print(SessionOpener.OpenByName(string.Join(" ", args, 1, args.Length - 1)));
                return 0;
            }
            if (args.Length >= 3 && args[0] == "rename")
            {
                Session target = Store.Load().FirstOrDefault(s => s.name == args[1]);
                Print(Attempt(delegate { return target == null ? "No session named '" + args[1] + "'" : Rename.Run(target, args[2]); }));
                return 0;
            }
            if (args.Length >= 3 && args[0] == "debug-send")
            {
                Print(Attempt(delegate { Native.SendToConsole(int.Parse(args[1]), args[2], true); return "sent"; }));
                return 0;
            }
            if (args.Length >= 1 && args[0] == "overlay")
            {
                Overlay.Run();
                return 0;
            }
            if (args.Length >= 1 && args[0] == "ensure-overlay")
            {
                Overlay.EnsureRunning();
                return 0;
            }
#if GUI
            Overlay.Run();
#else
            Overlay.EnsureRunning();
#endif
            return 0;
        }

        // Commands that attach to another console lose this process's own console; report failures as text.
        private static string Attempt(Func<string> action)
        {
            try { return action(); }
            catch (Exception ex) { return "failed: " + ex.Message; }
        }

        // Written straight to the stdout handle: the Console class caches handles that FreeConsole invalidated.
        private static void Print(string text)
        {
            Native.ReattachParentConsole();
            byte[] bytes = new UTF8Encoding(false).GetBytes(text + Environment.NewLine);
            using (Stream stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
            }
        }
    }
}

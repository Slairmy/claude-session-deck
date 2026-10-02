using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSessionDeck
{
    /// <summary>Win32 helpers: Windows Terminal window discovery, focus, and process inspection.</summary>
    internal static class Native
    {
        private const string HostClass = "CASCADIA_HOSTING_WINDOW_CLASS";
        private const uint ProcessQueryAndRead = 0x0400 | 0x0010;
        private static readonly Regex HeadlessFlag = new Regex(@"(^|\s)(-p|--print)(\s|$)");

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2A;
            public IntPtr Reserved2B;
            public IntPtr UniqueProcessId;
            public IntPtr ParentProcessId;
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string reserved;
            public string desktop;
            public string title;
            public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
            public short showWindow, reserved2Size;
            public IntPtr reserved2, stdInput, stdOutput, stdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr process, thread;
            public int processId, threadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfo startup, out ProcessInformation info);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort vk, scan;
            public uint flags, time;
            public IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput mouse;
            [FieldOffset(0)] public KeyboardInput key;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint type;
            public InputUnion data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        /// <summary>Types text into whatever window has the keyboard focus (Unicode key events, no IME).</summary>
        public static void TypeText(string text)
        {
            List<Input> events = new List<Input>();
            foreach (char c in text)
            {
                events.Add(KeyEvent(0, c, 0x0004));          // KEYEVENTF_UNICODE, key down
                events.Add(KeyEvent(0, c, 0x0004 | 0x0002)); // ... key up
            }
            Send(events);
        }

        public static void PressEnter()
        {
            Send(new List<Input> { KeyEvent(0x0D, 0, 0), KeyEvent(0x0D, 0, 0x0002) });
        }

        private static Input KeyEvent(ushort vk, ushort scan, uint flags)
        {
            Input input = new Input();
            input.type = 1; // INPUT_KEYBOARD
            input.data.key.vk = vk;
            input.data.key.scan = scan;
            input.data.key.flags = flags;
            return input;
        }

        private static void Send(List<Input> events)
        {
            Input[] array = events.ToArray();
            uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf(typeof(Input)));
            if (sent != array.Length)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput was blocked");
            }
        }

        /// <summary>
        /// Starts a program that outlives the caller and shares none of its handles. Process.Start would
        /// either inherit the hook's stdout pipe (Claude Code waits for that pipe to close) or go through
        /// ShellExecute, which takes several seconds on this machine.
        /// </summary>
        public static void StartDetached(string exePath, string arguments)
        {
            const uint detachedProcess = 0x00000008;
            const uint newProcessGroup = 0x00000200;
            const uint breakawayFromJob = 0x01000000;

            StartupInfo startup = new StartupInfo();
            startup.cb = Marshal.SizeOf(startup);
            ProcessInformation info;
            string directory = System.IO.Path.GetDirectoryName(exePath);

            // Leave any job object the caller is in when that is allowed; otherwise start normally.
            bool started = CreateProcess(exePath, new StringBuilder("\"" + exePath + "\"" + arguments), IntPtr.Zero, IntPtr.Zero, false,
                detachedProcess | newProcessGroup | breakawayFromJob, IntPtr.Zero, directory, ref startup, out info);
            if (!started)
            {
                started = CreateProcess(exePath, new StringBuilder("\"" + exePath + "\"" + arguments), IntPtr.Zero, IntPtr.Zero, false,
                    detachedProcess | newProcessGroup, IntPtr.Zero, directory, ref startup, out info);
            }
            if (!started) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            CloseHandle(info.process);
            CloseHandle(info.thread);
        }

        /// <summary>True when the file starts with the "MZ" header every Windows program has (a corrupted or encrypted copy does not).</summary>
        public static bool IsProgramFile(string path)
        {
            try
            {
                using (System.IO.FileStream file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
                }
            }
            catch (System.IO.IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>True when the handle is a live, visible Windows Terminal window.</summary>
        public static bool IsTerminal(long handle)
        {
            if (handle == 0) { return false; }
            IntPtr hwnd = new IntPtr(handle);
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) { return false; }
            StringBuilder text = new StringBuilder(128);
            GetClassName(hwnd, text, text.Capacity);
            return text.ToString() == HostClass;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int which, IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

        /// <summary>
        /// The Windows Terminal window hosting this process, found through its parent chain; 0 when it is not run from one.
        /// Used when the foreground window is not the terminal (the user clicked elsewhere while claude started).
        /// The topmost window wins when the terminal process owns several.
        /// </summary>
        public static long TerminalWindowOfAncestor()
        {
            int terminal = FindAncestor("WindowsTerminal");
            if (terminal == 0) { return 0; }
            foreach (long window in Snapshot())
            {
                uint owner;
                GetWindowThreadProcessId(new IntPtr(window), out owner);
                if (owner == (uint)terminal) { return window; }
            }
            return 0;
        }

        /// <summary>
        /// The Windows Terminal window that shows the console of process <paramref name="pid"/>; 0 when unknown.
        /// Works when the shell was handed over to Windows Terminal (its parent is explorer, so the process tree
        /// never reaches WindowsTerminal.exe): the console's pseudo window is owned by the terminal window.
        /// </summary>
        public static long TerminalWindowOfConsole(int pid)
        {
            if (pid <= 4) { return 0; }
            lock (ConsoleLock)
            {
                // Attaching to another console replaces this process's standard handles, and they are not
                // restored by detaching: a hook would lose the pipe Claude Code reads its output from.
                int[] which = { -10, -11, -12 }; // STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE
                IntPtr[] saved = which.Select(w => GetStdHandle(w)).ToArray();
                FreeConsole();
                try
                {
                    if (!AttachConsole((uint)pid)) { return 0; }
                    IntPtr console = GetConsoleWindow();
                    if (console == IntPtr.Zero) { return 0; }
                    long owner = GetAncestor(console, 3).ToInt64(); // GA_ROOTOWNER
                    return IsTerminal(owner) ? owner : 0;
                }
                finally
                {
                    FreeConsole();
                    for (int i = 0; i < which.Length; i++) { SetStdHandle(which[i], saved[i]); }
                }
            }
        }

        public static List<long> Snapshot()
        {
            List<long> found = new List<long>();
            EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (IsTerminal(hwnd.ToInt64())) { found.Add(hwnd.ToInt64()); }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static long Foreground()
        {
            return GetForegroundWindow().ToInt64();
        }

        public static bool Focus(long handle)
        {
            IntPtr hwnd = new IntPtr(handle);
            if (IsIconic(hwnd)) { ShowWindow(hwnd, 9); }
            if (SetForegroundWindow(hwnd)) { return true; }
            // Windows may refuse focus changes; a synthetic Alt tap lifts the restriction.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            return SetForegroundWindow(hwnd);
        }

        /// <summary>Waits for a terminal window that was not in <paramref name="before"/>; 0 on timeout.</summary>
        public static long WaitForNewWindow(List<long> before, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                foreach (long handle in Snapshot())
                {
                    if (!before.Contains(handle)) { return handle; }
                }
                System.Threading.Thread.Sleep(50);
            }
            return 0;
        }

        /// <summary>
        /// True when this process was started (directly or through a few shells) by a non-interactive
        /// "claude -p" run. Any failure to inspect a process counts as "interactive".
        /// </summary>
        public static bool IsHeadlessRun()
        {
            int pid = Process.GetCurrentProcess().Id;
            for (int depth = 0; depth < 5 && pid > 4; depth++)
            {
                IntPtr process = OpenProcess(ProcessQueryAndRead, false, pid);
                if (process == IntPtr.Zero) { return false; }
                try
                {
                    ProcessBasicInformation info = new ProcessBasicInformation();
                    int returned;
                    if (NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf(info), out returned) != 0) { return false; }
                    string commandLine = ReadCommandLine(process, info.PebBaseAddress);
                    if (commandLine != null && HeadlessFlag.IsMatch(commandLine)) { return true; }
                    pid = info.ParentProcessId.ToInt32();
                }
                finally
                {
                    CloseHandle(process);
                }
            }
            return false;
        }

        /// <summary>Process id of the nearest ancestor whose image name is <paramref name="name"/>, or 0.</summary>
        public static int FindAncestor(string name)
        {
            int pid = Process.GetCurrentProcess().Id;
            for (int depth = 0; depth < 6 && pid > 4; depth++)
            {
                IntPtr process = OpenProcess(ProcessQueryAndRead, false, pid);
                if (process == IntPtr.Zero) { return 0; }
                int parent;
                try
                {
                    ProcessBasicInformation info = new ProcessBasicInformation();
                    int returned;
                    if (NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf(info), out returned) != 0) { return 0; }
                    parent = info.ParentProcessId.ToInt32();
                }
                finally
                {
                    CloseHandle(process);
                }
                if (IsProcessNamed(parent, name)) { return parent; }
                pid = parent;
            }
            return 0;
        }

        public static bool IsProcessNamed(int pid, string name)
        {
            if (pid <= 0) { return false; }
            try { return string.Equals(Process.GetProcessById(pid).ProcessName, name, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; } // no such process
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct ConsoleKeyRecord
        {
            [FieldOffset(0)] public ushort eventType;
            [FieldOffset(4)] public int keyDown;
            [FieldOffset(8)] public ushort repeat;
            [FieldOffset(10)] public ushort vk;
            [FieldOffset(12)] public ushort scan;
            [FieldOffset(14)] public ushort ch;
            [FieldOffset(16)] public uint control;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        // Must be the wide (W) entry point: without it the records are read as ANSI and non-ASCII
        // characters arrive mangled.
        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
        private static extern bool WriteConsoleInput(IntPtr console, ConsoleKeyRecord[] records, uint length, out uint written);

        private static readonly object ConsoleLock = new object();

        /// <summary>
        /// Types into another process's console input, without touching keyboard focus. Works for the
        /// pseudo console Windows Terminal gives each tab. Attaches to the target console only for the call.
        /// </summary>
        public static void SendToConsole(int pid, string text, bool pressEnter)
        {
            List<ConsoleKeyRecord> records = new List<ConsoleKeyRecord>();
            foreach (char c in text)
            {
                records.Add(ConsoleKey(true, 0, 0, c));
                records.Add(ConsoleKey(false, 0, 0, c));
            }
            if (pressEnter)
            {
                records.Add(ConsoleKey(true, 0x0D, 0x1C, '\r'));
                records.Add(ConsoleKey(false, 0x0D, 0x1C, '\r'));
            }

            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot attach to the session's console"); }
                try
                {
                    IntPtr input = CreateFile("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (input == new IntPtr(-1)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the console input"); }
                    try
                    {
                        uint written;
                        ConsoleKeyRecord[] array = records.ToArray();
                        if (!WriteConsoleInput(input, array, (uint)array.Length, out written) || written != array.Length)
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Writing to the console input failed");
                        }
                    }
                    finally
                    {
                        CloseHandle(input);
                    }
                }
                finally
                {
                    FreeConsole();
                }
            }
        }

        /// <summary>Re-attaches a command-line tool to the console it was started from, after SendToConsole.</summary>
        public static void ReattachParentConsole()
        {
            AttachConsole(0xFFFFFFFF);
        }

        private static ConsoleKeyRecord ConsoleKey(bool down, ushort vk, ushort scan, char c)
        {
            ConsoleKeyRecord record = new ConsoleKeyRecord();
            record.eventType = 1; // KEY_EVENT
            record.keyDown = down ? 1 : 0;
            record.repeat = 1;
            record.vk = vk;
            record.scan = scan;
            record.ch = c;
            return record;
        }

        // Reads PEB -> ProcessParameters -> CommandLine of a 64-bit process (x64 layout).
        private static string ReadCommandLine(IntPtr process, IntPtr peb)
        {
            if (IntPtr.Size != 8) { return null; }
            IntPtr read;
            byte[] pointer = new byte[8];
            if (!ReadProcessMemory(process, new IntPtr(peb.ToInt64() + 0x20), pointer, new IntPtr(8), out read)) { return null; }
            long parameters = BitConverter.ToInt64(pointer, 0);

            byte[] unicodeString = new byte[16];
            if (!ReadProcessMemory(process, new IntPtr(parameters + 0x70), unicodeString, new IntPtr(16), out read)) { return null; }
            int length = BitConverter.ToUInt16(unicodeString, 0);
            long buffer = BitConverter.ToInt64(unicodeString, 8);
            if (length <= 0 || buffer == 0) { return null; }

            byte[] text = new byte[length];
            if (!ReadProcessMemory(process, new IntPtr(buffer), text, new IntPtr(length), out read)) { return null; }
            return Encoding.Unicode.GetString(text);
        }
    }
}

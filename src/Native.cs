using System;
using System.Runtime.InteropServices;

namespace MusicToMic
{
    internal static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint iconSize);
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        /// <summary>Hands unused memory back to Windows (after the window is hidden).</summary>
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(GetCurrentProcess());
        }
    }
}

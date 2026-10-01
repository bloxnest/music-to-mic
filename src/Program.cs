using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MusicToMic
{
    internal static class Program
    {
        private const string MutexName = @"Local\MusicToMic.Running";
        private const string ShowEventName = @"Local\MusicToMic.Show";
        private const string ExitEventName = @"Local\MusicToMic.Exit";

        /// <summary>
        ///   MusicToMic.exe          open the window
        ///   MusicToMic.exe --tray   start hidden in the hidden icons (used by "Start with Windows")
        ///   MusicToMic.exe --exit   close the copy that's running
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            var flags = new List<string>(args);
            if (flags.Contains("--exit"))
            {
                Signal(ExitEventName);
                WaitUntilClosed();
                return 0;
            }

            bool firstCopy;
            using (var mutex = new Mutex(true, MutexName, out firstCopy))
            {
                if (!firstCopy)
                {
                    // already running: bring that copy's window up instead of starting another
                    Native.AllowSetForegroundWindow(-1);
                    Signal(ShowEventName);
                    return 0;
                }
                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
                {
                    // keeps running (and routing) with the window hidden; only Exit (or --exit) ends it
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var main = new MainWindow(show, exit);
                    bool hidden = flags.Contains("--tray") || Settings.Load().StartMinimized;
                    if (!hidden) main.Window.Show();
                    else app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Native.TrimMemory));
                    app.Run();
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        private static void Signal(string eventName)
        {
            EventWaitHandle handle;
            if (EventWaitHandle.TryOpenExisting(eventName, out handle))
                using (handle) handle.Set();
        }

        /// <summary>The running copy holds the mutex until it ends, so getting the mutex means it's gone.</summary>
        private static void WaitUntilClosed()
        {
            Mutex running;
            if (!Mutex.TryOpenExisting(MutexName, out running)) return;
            using (running)
            {
                try
                {
                    if (running.WaitOne(5000)) running.ReleaseMutex();
                }
                catch (AbandonedMutexException) { }
            }
            Thread.Sleep(300);
        }
    }
}

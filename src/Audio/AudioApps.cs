using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicToMic.Audio
{
    /// <summary>An app that has sound open (playing or paused), e.g. Spotify or Chrome.</summary>
    internal sealed class AudioApp
    {
        public string Exe;          // lower case, e.g. "chrome.exe": what the source lock remembers
        public string Name;         // e.g. "Google Chrome"
        public int ProcessId;       // the app's main process
        public bool Playing;        // making sound right now

        public SourceTarget ToTarget()
        {
            return new SourceTarget { Exe = Exe, Name = Name, ProcessId = ProcessId };
        }
    }

    /// <summary>
    /// Finds the apps that have audio open, from Windows' per-app audio sessions (the same list as the volume
    /// mixer). Apps like Chrome play from a helper process, so each session is traced up to the app's main
    /// process; capturing that process includes all of its helpers.
    /// </summary>
    internal static class AudioApps
    {
        /// <summary>Per-app capture needs Windows 10 version 2004 (build 19041) or later.</summary>
        public static bool PerAppCapture
        {
            get { return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 19041; }
        }

        // what each session's process is: its app (exe) and main process. A running process keeps its id,
        // so the full process list is only re-read when a session from a new process shows up.
        private struct Owner
        {
            public string Exe;
            public int Root;
            public string Name;
        }

        private static readonly Dictionary<int, Owner> owners = new Dictionary<int, Owner>();
        private static readonly int self = Process.GetCurrentProcess().Id;

        public static List<AudioApp> List()
        {
            List<Session> sessions = Sessions();
            bool unknown = false;
            foreach (Session session in sessions)
                if (session.ProcessId != 0 && !owners.ContainsKey(session.ProcessId)) unknown = true;
            if (unknown) Resolve(sessions);

            var byExe = new Dictionary<string, AudioApp>();
            var alive = new HashSet<int>();
            foreach (Session session in sessions)
            {
                Owner owner;
                if (session.ProcessId == 0 || !owners.TryGetValue(session.ProcessId, out owner)) continue;
                alive.Add(session.ProcessId);
                if (owner.Exe == null || owner.Root == self || session.ProcessId == self) continue;
                AudioApp app;
                if (!byExe.TryGetValue(owner.Exe, out app))
                {
                    app = new AudioApp { Exe = owner.Exe, ProcessId = owner.Root, Name = owner.Name };
                    byExe[owner.Exe] = app;
                }
                if (session.Playing) app.Playing = true;
            }
            // forget processes whose sessions are gone (their ids can be reused later)
            foreach (int pid in new List<int>(owners.Keys)) if (!alive.Contains(pid)) owners.Remove(pid);
            var list = new List<AudioApp>(byExe.Values);
            list.Sort((a, b) => a.Playing != b.Playing ? (a.Playing ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>The app's main process right now (after it was closed and reopened, it has a new one), or null.</summary>
        public static AudioApp Find(string exe)
        {
            foreach (AudioApp app in List())
                if (app.Exe == exe) return app;
            // running but no sound opened yet (e.g. Spotify before the first song): its main process
            Dictionary<int, ProcessEntry> processes = Snapshot();
            foreach (KeyValuePair<int, ProcessEntry> p in processes)
            {
                if (p.Value.Exe != exe) continue;
                int root = RootOf(p.Key, processes);
                return new AudioApp { Exe = exe, ProcessId = root, Name = NameOf(root, exe, null) };
            }
            return null;
        }

        private static void Resolve(List<Session> sessions)
        {
            Dictionary<int, ProcessEntry> processes = Snapshot();
            foreach (Session session in sessions)
            {
                ProcessEntry entry;
                if (session.ProcessId == 0 || owners.ContainsKey(session.ProcessId)) continue;
                if (!processes.TryGetValue(session.ProcessId, out entry))
                {
                    owners[session.ProcessId] = new Owner { Exe = null, Root = self };   // gone or hidden: ignore
                    continue;
                }
                int root = RootOf(session.ProcessId, processes);
                owners[session.ProcessId] = new Owner { Exe = entry.Exe, Root = root, Name = NameOf(root, entry.Exe, session.DisplayName) };
            }
        }

        // ---------- sessions ----------

        private struct Session
        {
            public int ProcessId;
            public bool Playing;
            public string DisplayName;
        }

        // one session list per output device, opened once and kept (reopening them every refresh costs CPU)
        private static readonly Dictionary<string, IAudioSessionManager2> managers = new Dictionary<string, IAudioSessionManager2>();

        /// <summary>Call when devices change, so the next refresh opens the new set.</summary>
        public static void DevicesChanged()
        {
            foreach (IAudioSessionManager2 m in managers.Values) Marshal.ReleaseComObject(m);
            managers.Clear();
        }

        private static List<Session> Sessions()
        {
            var found = new List<Session>();
            if (managers.Count == 0) OpenManagers();
            foreach (IAudioSessionManager2 manager in managers.Values)
            {
                try { ReadSessions(manager, found); }
                catch (COMException) { }
                catch (InvalidCastException) { }
            }
            return found;
        }

        private static void OpenManagers()
        {
            IMMDeviceEnumerator enumerator = Devices.NewEnumerator();
            IMMDeviceCollection devices = null;
            try
            {
                if (enumerator.EnumAudioEndpoints(DataFlow.Render, AudioConst.DEVICE_STATE_ACTIVE, out devices) < 0) return;
                int count;
                devices.GetCount(out count);
                for (int d = 0; d < count; d++)
                {
                    IMMDevice device;
                    if (devices.Item(d, out device) < 0) continue;
                    try
                    {
                        string id;
                        device.GetId(out id);
                        Guid iid = AudioConst.IID_IAudioSessionManager2;
                        object manager;
                        if (device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out manager) >= 0)
                            managers[id] = (IAudioSessionManager2)manager;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                if (devices != null) Marshal.ReleaseComObject(devices);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static void ReadSessions(IAudioSessionManager2 manager, List<Session> found)
        {
            IAudioSessionEnumerator sessions = null;
            try
            {
                if (manager.GetSessionEnumerator(out sessions) < 0) return;
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    object item;
                    if (sessions.GetSession(i, out item) < 0) continue;
                    try
                    {
                        // each call here goes to the Windows audio service, so only ask what's needed
                        var control = (IAudioSessionControl2)item;
                        SessionState state;
                        int pid;
                        if (control.GetProcessId(out pid) < 0 || pid == 0 || pid == self) continue;
                        control.GetState(out state);
                        if (state == SessionState.Expired) continue;
                        float peak = 0;
                        if (state == SessionState.Active)
                        {
                            var meter = item as IAudioMeterInformation;
                            if (meter != null) meter.GetPeakValue(out peak);
                        }
                        string display = null;
                        if (!owners.ContainsKey(pid)) control.GetDisplayName(out display);
                        found.Add(new Session { ProcessId = pid, Playing = state == SessionState.Active && peak > 0.0005f, DisplayName = display });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
            }
        }

        // ---------- processes ----------

        private struct ProcessEntry
        {
            public int Parent;
            public string Exe;
        }

        /// <summary>Climbs from a helper process to the topmost process of the same app (same exe).</summary>
        private static int RootOf(int pid, Dictionary<int, ProcessEntry> processes)
        {
            int current = pid;
            for (int guard = 0; guard < 32; guard++)
            {
                ProcessEntry me, parent;
                if (!processes.TryGetValue(current, out me) || !processes.TryGetValue(me.Parent, out parent)) break;
                if (parent.Exe != me.Exe || me.Parent == current) break;
                current = me.Parent;
            }
            return current;
        }

        private static Dictionary<int, ProcessEntry> Snapshot()
        {
            var map = new Dictionary<int, ProcessEntry>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);   // all processes
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                    map[entry.th32ProcessID] = new ProcessEntry { Parent = entry.th32ParentProcessID, Exe = entry.szExeFile.ToLowerInvariant() };
            }
            finally
            {
                CloseHandle(snap);
            }
            return map;
        }

        private static readonly Dictionary<string, string> names = new Dictionary<string, string>();

        /// <summary>A friendly name: the exe's description ("Google Chrome", "Spotify"), else the session's, else the exe name.</summary>
        private static string NameOf(int pid, string exe, string sessionName)
        {
            string path = ImagePath(pid);
            string key = path ?? exe;
            string name;
            if (names.TryGetValue(key, out name)) return name;
            name = null;
            if (path != null)
            {
                try { name = FileVersionInfo.GetVersionInfo(path).FileDescription; }
                catch (FileNotFoundException) { }
            }
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(sessionName) && !sessionName.StartsWith("@")) name = sessionName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(exe);
                if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
            name = name.Trim();
            names[key] = name;
            return name;
        }

        private static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try
            {
                var text = new StringBuilder(1024);
                int size = text.Capacity;
                return QueryFullProcessImageNameW(handle, 0, text, ref size) ? text.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

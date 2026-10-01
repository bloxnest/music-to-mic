using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using MusicToMic.Audio;

namespace MusicToMic
{
    /// <summary>A named set of volumes (0..1).</summary>
    internal sealed class Preset
    {
        public string Name;
        public float Music, Mic, Master;
        public bool BuiltIn;

        public override string ToString() { return Name; }

        public static readonly Preset[] BuiltIns =
        {
            new Preset { Name = "Music", Music = 1f, Mic = 0f, Master = 1f, BuiltIn = true },
            new Preset { Name = "Background Music", Music = 0.25f, Mic = 1f, Master = 1f, BuiltIn = true },
            new Preset { Name = "Balanced", Music = 0.5f, Mic = 1f, Master = 1f, BuiltIn = true },
        };
    }

    /// <summary>Options, saved as plain text in %APPDATA%\MusicToMic\settings.ini.</summary>
    internal sealed class Settings
    {
        // defaults used when the app opens (empty = pick automatically)
        public string DefaultSourceExe = "", DefaultSourceName = "";
        public string DefaultMicId = "", DefaultMicName = "";
        public string DefaultOutputId = "", DefaultOutputName = "";

        public RouteMode Mode = RouteMode.MicAndMusic;
        public float MusicVolume = 0.5f, MicVolume = 1f, MasterVolume = 1f;
        public bool RememberVolumes = true, RememberMode = true;

        public bool PushToMusic = false;
        public int PushKey = 0x77;   // F8

        public bool StartMinimized = false, RouteAtLaunch = false, AutoReconnect = true;
        public List<Preset> Custom = new List<Preset>();

        internal static string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicToMic");

        private static string FilePath { get { return Path.Combine(Folder, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "defaultSourceExe": s.DefaultSourceExe = v; break;
                        case "defaultSourceName": s.DefaultSourceName = v; break;
                        case "defaultMicId": s.DefaultMicId = v; break;
                        case "defaultMicName": s.DefaultMicName = v; break;
                        case "defaultOutputId": s.DefaultOutputId = v; break;
                        case "defaultOutputName": s.DefaultOutputName = v; break;
                        case "mode": int m; if (int.TryParse(v, out m) && m >= 0 && m <= 2) s.Mode = (RouteMode)m; break;
                        case "musicVolume": s.MusicVolume = Volume(v, s.MusicVolume); break;
                        case "micVolume": s.MicVolume = Volume(v, s.MicVolume); break;
                        case "masterVolume": s.MasterVolume = Volume(v, s.MasterVolume); break;
                        case "rememberVolumes": s.RememberVolumes = v == "1"; break;
                        case "rememberMode": s.RememberMode = v == "1"; break;
                        case "pushToMusic": s.PushToMusic = v == "1"; break;
                        case "pushKey": int k; if (int.TryParse(v, out k) && KeyNames.Problem(k) == null) s.PushKey = k; break;
                        case "startMinimized": s.StartMinimized = v == "1"; break;
                        case "routeAtLaunch": s.RouteAtLaunch = v == "1"; break;
                        case "autoReconnect": s.AutoReconnect = v == "1"; break;
                        case "preset":
                            string[] p = v.Split('|');
                            if (p.Length == 4 && p[0].Trim().Length > 0)
                                s.Custom.Add(new Preset { Name = p[0].Trim(), Music = Volume(p[1], 0.5f), Mic = Volume(p[2], 1f), Master = Volume(p[3], 1f) });
                            break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!s.RememberVolumes)
            {
                var fresh = new Settings();
                s.MusicVolume = fresh.MusicVolume;
                s.MicVolume = fresh.MicVolume;
                s.MasterVolume = fresh.MasterVolume;
            }
            if (!s.RememberMode) s.Mode = RouteMode.MicAndMusic;
            return s;
        }

        public void Save()
        {
            var lines = new List<string>
            {
                "defaultSourceExe=" + DefaultSourceExe,
                "defaultSourceName=" + DefaultSourceName,
                "defaultMicId=" + DefaultMicId,
                "defaultMicName=" + DefaultMicName,
                "defaultOutputId=" + DefaultOutputId,
                "defaultOutputName=" + DefaultOutputName,
                "mode=" + (int)Mode,
                "musicVolume=" + Text(MusicVolume),
                "micVolume=" + Text(MicVolume),
                "masterVolume=" + Text(MasterVolume),
                "rememberVolumes=" + Flag(RememberVolumes),
                "rememberMode=" + Flag(RememberMode),
                "pushToMusic=" + Flag(PushToMusic),
                "pushKey=" + PushKey,
                "startMinimized=" + Flag(StartMinimized),
                "routeAtLaunch=" + Flag(RouteAtLaunch),
                "autoReconnect=" + Flag(AutoReconnect),
            };
            foreach (Preset p in Custom)
                lines.Add("preset=" + p.Name.Replace("|", "/") + "|" + Text(p.Music) + "|" + Text(p.Mic) + "|" + Text(p.Master));
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static float Volume(string text, float fallback)
        {
            float v;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            return Math.Max(0f, Math.Min(1f, v));
        }

        private static string Text(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Flag(bool on) { return on ? "1" : "0"; }
    }

    /// <summary>"Start with Windows": a per-user Run entry (no admin rights needed).</summary>
    internal static class StartWithWindows
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Music to Mic";

        /// <summary>Self-tests only: never touch the real Run entry.</summary>
        internal static bool LeaveAlone = false;

        private static string Command
        {
            get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; }
        }

        public static bool IsOn
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static bool Set(bool on)
        {
            if (LeaveAlone) return true;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue(ValueName, Command);
                    else key.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        /// <summary>Keeps the entry pointing at this exe if the folder was moved.</summary>
        public static void RefreshPath()
        {
            if (!LeaveAlone && IsOn) Set(true);
        }
    }

    /// <summary>Names for push-to-music keys (Windows virtual-key codes), and which ones can't be used.</summary>
    internal static class KeyNames
    {
        public static string Problem(int vk)
        {
            if (vk == 0x01 || vk == 0x02) return "the left and right mouse buttons are needed for playing";
            if (vk == 0x1B) return "Esc cancels";
            if (vk == 0x5B || vk == 0x5C) return "the Windows key opens Start";
            if (vk == 0x0D) return "Enter opens Roblox chat";
            if (vk == 0xBF) return "/ opens Roblox chat";
            if (vk <= 0 || vk > 0xFE) return "it isn't a key";
            return null;
        }

        public static string Name(int vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            switch (vk)
            {
                case 0x04: return "Middle mouse";
                case 0x05: return "Mouse 4";
                case 0x06: return "Mouse 5";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
                case 0x13: return "Pause";
                case 0x14: return "Caps Lock";
                case 0x20: return "Space";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left arrow";
                case 0x26: return "Up arrow";
                case 0x27: return "Right arrow";
                case 0x28: return "Down arrow";
                case 0x2C: return "Print Screen";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x90: return "Num Lock";
                case 0x91: return "Scroll Lock";
                case 0xA0: return "Left Shift";
                case 0xA1: return "Right Shift";
                case 0xA2: return "Left Ctrl";
                case 0xA3: return "Right Ctrl";
                case 0xA4: return "Left Alt";
                case 0xA5: return "Right Alt";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            return "Key " + vk.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}

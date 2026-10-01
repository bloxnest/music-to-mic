using System.Diagnostics;
using System.Reflection;

[assembly: AssemblyTitle("Music to Mic")]
[assembly: AssemblyDescription("Music to Mic for Roblox: play an app's audio through a virtual microphone, by xRed1.")]
[assembly: AssemblyProduct("Music to Mic")]
[assembly: AssemblyCompany("xRed1")]
[assembly: AssemblyCopyright("Copyright (c) 2026 xRed1. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MusicToMic
{
    internal static class AppInfo
    {
        public const string Name = "Music to Mic";
        public const string Version = "1.0.0";
        public const string Developer = "xRed1";
        public const string CableDownload = "https://vb-audio.com/Cable/";

        public static void Open(string target)
        {
            try { Process.Start(target); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}

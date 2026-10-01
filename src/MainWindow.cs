using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MusicToMic.Audio;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MusicToMic
{
    internal enum Page { Main, Settings, Setup, Log }

    /// <summary>One entry in a dropdown: what it says, a small note on the right, and what it stands for.</summary>
    public sealed class Choice
    {
        public string Label { get; set; }
        public string Note { get; set; }
        public object Value { get; set; }
        public override string ToString() { return Label; }
    }

    /// <summary>
    /// The window, built like BloxNest/NudgeNest: layout and animations in MainWindow.xaml, drawn by WPF on the
    /// graphics card. Nothing animates while idle; the level meters only update while routing with the window
    /// open. Closing or minimizing hides it to the hidden icons and the routing keeps going.
    /// </summary>
    internal sealed class MainWindow
    {
        private static readonly Color Gray = Hex("#6B6B7A"), Green = Hex("#4ADE80"), Amber = Hex("#F2C26B"), Red = Hex("#F87171"), Violet = Hex("#A78BFA");
        private static readonly Brush Light = Freeze(Hex("#EEEEF4")), Muted = Freeze(Hex("#A6A6B5")), Danger = Freeze(Red), Accent = Freeze(Violet);

        public readonly Window Window;
        private readonly Settings settings;
        private readonly Router router;
        private readonly EventWaitHandle showSignal, exitSignal;
        private RegisteredWaitHandle showWait, exitWait;

        private readonly FrameworkElement windowRoot, mainPanel, settingsPanel, setupPanel, logPanel;
        private readonly ScaleTransform rootScale;
        private readonly ComboBox sourceBox, micBox, outputBox, presetBox, savedPresetBox, defaultSourceBox, defaultMicBox, defaultOutputBox;
        private readonly RadioButton[] modeSegments;
        private readonly Slider musicSlider, micSlider, masterSlider;
        private readonly ToggleButton muteMusicButton, muteMicButton, muteAllButton;
        private readonly CheckBox pushToggle, monitorToggle, optStartMinimized, optWindows, optRouteAtLaunch, optAutoReconnect, optRememberVolumes, optRememberMode;
        private readonly TextBlock statusText, statusDetail, outputHint, musicValue, micValue, masterValue, pushHint, monitorHint, startText,
                                   musicDb, micDb, outDb, limitText, keyValueText, keyHint, presetListText, logText, cableIcon, cableTitle, cableText;
        private readonly Run stepSeven;
        private readonly Button startButton, stopButton, changeKeyButton, cableDownloadButton;
        private readonly FrameworkElement presetPicker, presetNamer, savedPresetRow;
        private readonly TextBox presetName;
        private readonly SolidColorBrush dotFill;
        private readonly Meter musicMeter, micMeter, outMeter;
        private readonly ScrollViewer logScroll;
        private readonly DispatcherTimer meterTimer;
        private FrameworkElement currentPanel;
        private bool loading, quitting, hiding, capturing, toldAboutTray;
        private Health shownHealth = (Health)(-1);
        private string sourceSignature, micSignature, outputSignature;
        private DateTime limitUntil = DateTime.MinValue;

        private Forms.NotifyIcon tray;
        private Drawing.Icon onIcon, offIcon, warnIcon;
        private Forms.ContextMenuStrip trayMenu;
        private Forms.ToolStripMenuItem routingItem, muteMusicItem, muteMicItem;

        /// <summary>What Exit does. Self-tests replace it so the test run keeps going.</summary>
        internal Action ShutDown = delegate { Application.Current.Shutdown(); };

        public MainWindow(EventWaitHandle showSignal, EventWaitHandle exitSignal)
        {
            this.showSignal = showSignal;
            this.exitSignal = exitSignal;
            Logger.Write(AppInfo.Name + " " + AppInfo.Version + " started");
            settings = Settings.Load();
            router = new Router(settings);

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);

            windowRoot = Find<FrameworkElement>("WindowRoot");
            rootScale = (ScaleTransform)windowRoot.RenderTransform;
            mainPanel = Find<FrameworkElement>("MainPanel");
            settingsPanel = Find<FrameworkElement>("SettingsPanel");
            setupPanel = Find<FrameworkElement>("SetupPanel");
            logPanel = Find<FrameworkElement>("LogPanel");
            sourceBox = Find<ComboBox>("SourceBox");
            micBox = Find<ComboBox>("MicBox");
            outputBox = Find<ComboBox>("OutputBox");
            presetBox = Find<ComboBox>("PresetBox");
            savedPresetBox = Find<ComboBox>("SavedPresetBox");
            defaultSourceBox = Find<ComboBox>("DefaultSourceBox");
            defaultMicBox = Find<ComboBox>("DefaultMicBox");
            defaultOutputBox = Find<ComboBox>("DefaultOutputBox");
            modeSegments = Find<UniformGrid>("ModeSegments").Children.OfType<RadioButton>().ToArray();
            musicSlider = Find<Slider>("MusicSlider");
            micSlider = Find<Slider>("MicSlider");
            masterSlider = Find<Slider>("MasterSlider");
            muteMusicButton = Find<ToggleButton>("MuteMusicButton");
            muteMicButton = Find<ToggleButton>("MuteMicButton");
            muteAllButton = Find<ToggleButton>("MuteAllButton");
            pushToggle = Find<CheckBox>("PushToggle");
            monitorToggle = Find<CheckBox>("MonitorToggle");
            optStartMinimized = Find<CheckBox>("OptStartMinimized");
            optWindows = Find<CheckBox>("OptWindows");
            optRouteAtLaunch = Find<CheckBox>("OptRouteAtLaunch");
            optAutoReconnect = Find<CheckBox>("OptAutoReconnect");
            optRememberVolumes = Find<CheckBox>("OptRememberVolumes");
            optRememberMode = Find<CheckBox>("OptRememberMode");
            statusText = Find<TextBlock>("StatusText");
            statusDetail = Find<TextBlock>("StatusDetail");
            outputHint = Find<TextBlock>("OutputHint");
            musicValue = Find<TextBlock>("MusicValue");
            micValue = Find<TextBlock>("MicValue");
            masterValue = Find<TextBlock>("MasterValue");
            pushHint = Find<TextBlock>("PushHint");
            monitorHint = Find<TextBlock>("MonitorHint");
            startText = Find<TextBlock>("StartText");
            musicDb = Find<TextBlock>("MusicDb");
            micDb = Find<TextBlock>("MicDb");
            outDb = Find<TextBlock>("OutDb");
            limitText = Find<TextBlock>("LimitText");
            keyValueText = Find<TextBlock>("KeyValueText");
            keyHint = Find<TextBlock>("KeyHint");
            presetListText = Find<TextBlock>("PresetListText");
            logText = Find<TextBlock>("LogText");
            cableIcon = Find<TextBlock>("CableIcon");
            cableTitle = Find<TextBlock>("CableTitle");
            cableText = Find<TextBlock>("CableText");
            stepSeven = Find<Run>("StepSeven");
            startButton = Find<Button>("StartButton");
            stopButton = Find<Button>("StopButton");
            changeKeyButton = Find<Button>("ChangeKeyButton");
            cableDownloadButton = Find<Button>("CableDownloadButton");
            presetPicker = Find<FrameworkElement>("PresetPicker");
            presetNamer = Find<FrameworkElement>("PresetNamer");
            savedPresetRow = Find<FrameworkElement>("SavedPresetRow");
            presetName = Find<TextBox>("PresetName");
            logScroll = Find<ScrollViewer>("LogScroll");
            musicMeter = new Meter(this, "Music");
            micMeter = new Meter(this, "Mic");
            outMeter = new Meter(this, "Out");
            currentPanel = mainPanel;

            dotFill = new SolidColorBrush(Gray);
            Find<Ellipse>("Dot").Fill = dotFill;
            meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, delegate { UpdateMeters(); }, Window.Dispatcher);
            meterTimer.Stop();

            CreateTray();
            RoundCorners();
            WireWindow();
            WireMain();
            WireSettings();
            WireKeyCapture();
            Find<Button>("CableDownloadButton").Click += delegate { AppInfo.Open(AppInfo.CableDownload); };
            Find<Button>("SoundSettingsButton").Click += delegate { AppInfo.Open("ms-settings:sound"); };
            Window.SourceInitialized += delegate { SetWindowIcons(); };
            Window.IsVisibleChanged += delegate { router.Watching = Window.IsVisible; Render(); };

            router.Changed += Render;
            Logger.Written += delegate (string line) { Window.Dispatcher.BeginInvoke(new Action(() => { if (currentPanel == logPanel) AppendLog(line); })); };
            StartWithWindows.RefreshPath();
            ShowValues();
            Render();
            if (settings.RouteAtLaunch) router.Start();

            showWait = OnSignal(showSignal, ShowFromTray);
            exitWait = OnSignal(exitSignal, Quit);
            Window.Loaded += delegate { AnimateIn(); };
        }

        internal Router Router { get { return router; } }
        internal Forms.ContextMenuStrip TrayMenu { get { return trayMenu; } }

        internal Page CurrentPage
        {
            get
            {
                return currentPanel == settingsPanel ? Page.Settings : currentPanel == setupPanel ? Page.Setup
                    : currentPanel == logPanel ? Page.Log : Page.Main;
            }
        }

        internal T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        private static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void RoundCorners()
        {
            var content = Find<FrameworkElement>("ContentRoot");
            content.SizeChanged += delegate { content.Clip = new RectangleGeometry(new Rect(content.RenderSize), 9, 9); };
        }

        // ---------- window and taskbar icons ----------

        private Drawing.Icon taskbarIcon;

        /// <summary>The taskbar shows the big icon at 24 px (100% scaling); hand it the picture drawn for that size.</summary>
        private void SetWindowIcons()
        {
            IntPtr hWnd = new WindowInteropHelper(Window).Handle;
            double scale = VisualTreeHelper.GetDpi(Window).DpiScaleX;
            int big = (int)Math.Round(24 * scale), small = (int)Math.Round(16 * scale);
            IntPtr bigIcon, smallIcon;
            if (Native.SHDefExtractIcon(Assembly.GetExecutingAssembly().Location, 0, 0, out bigIcon, out smallIcon, (uint)(big | (small << 16))) != 0) return;
            taskbarIcon = Drawing.Icon.FromHandle(bigIcon);
            Native.SendMessage(hWnd, 0x0080, new IntPtr(1), bigIcon);     // WM_SETICON, big
            Native.SendMessage(hWnd, 0x0080, IntPtr.Zero, smallIcon);     // WM_SETICON, small
        }

        internal Drawing.Size TaskbarIconSize { get { return taskbarIcon == null ? Drawing.Size.Empty : taskbarIcon.Size; } }

        // ---------- window animations and the hidden icons (same as BloxNest) ----------

        private void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            windowRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        private void AnimateOut(Action then)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += delegate { then(); };
            windowRoot.BeginAnimation(UIElement.OpacityProperty, fade);
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }

        private void WireWindow()
        {
            Find<Button>("MinButton").Click += delegate { HideToTray(); };
            Find<Button>("CloseButton").Click += delegate { HideToTray(); };
            Find<Button>("SettingsButton").Click += delegate { ShowPanel(currentPanel == settingsPanel ? mainPanel : settingsPanel); };
            Find<Button>("SetupButton").Click += delegate { ShowPanel(currentPanel == setupPanel ? mainPanel : setupPanel); };
            Find<Button>("SettingsBackButton").Click += delegate { ShowPanel(mainPanel); };
            Find<Button>("SetupBackButton").Click += delegate { ShowPanel(mainPanel); };
            Find<Button>("LogBackButton").Click += delegate { ShowPanel(settingsPanel); };
            Find<Button>("ViewLogButton").Click += delegate { ShowPanel(logPanel); };
            Find<Button>("OpenLogButton").Click += delegate { if (File.Exists(Logger.FilePath)) AppInfo.Open(Logger.FilePath); };
            Window.StateChanged += delegate { if (Window.WindowState == WindowState.Normal) AnimateIn(); };
            Window.Closing += (sender, e) =>
            {
                if (quitting) return;
                e.Cancel = true;
                if (!hiding) HideToTray();
            };
        }

        /// <summary>Close or minimize: fade out, then tuck into the hidden icons. Routing keeps going.</summary>
        internal void HideToTray()
        {
            if (hiding || !Window.IsVisible) return;
            hiding = true;
            AnimateOut(delegate
            {
                Window.Hide();
                hiding = false;
                ShowPanel(mainPanel, false);
                if (!toldAboutTray)
                {
                    toldAboutTray = true;
                    tray.ShowBalloonTip(4000, AppInfo.Name + " is still running",
                        (router.Routing ? "Routing keeps going. " : "") + "It's in your hidden icons. Right-click it to quit.", Forms.ToolTipIcon.None);
                }
                Native.TrimMemory();
            });
        }

        internal void ShowFromTray()
        {
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
            router.RefreshApps();
            ShowValues();
            Render();
            AnimateIn();
        }

        internal void Quit()
        {
            if (quitting) return;
            quitting = true;
            meterTimer.Stop();
            router.Stop();
            Logger.Write(AppInfo.Name + " closed");
            router.Dispose();
            if (showWait != null) showWait.Unregister(null);
            if (exitWait != null) exitWait.Unregister(null);
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            if (Window.IsVisible) AnimateOut(delegate { Window.Close(); ShutDown(); });
            else { Window.Close(); ShutDown(); }
        }

        private RegisteredWaitHandle OnSignal(WaitHandle signal, Action action)
        {
            if (signal == null) return null;
            return ThreadPool.RegisterWaitForSingleObject(signal, delegate { Window.Dispatcher.BeginInvoke(action); }, null, -1, false);
        }

        internal void ShowPanel(Page page)
        {
            ShowPanel(page == Page.Settings ? settingsPanel : page == Page.Setup ? setupPanel : page == Page.Log ? logPanel : mainPanel);
        }

        private void ShowPanel(FrameworkElement panel, bool animate = true)
        {
            if (panel == currentPanel) return;
            if (capturing) EndKeyCapture();
            foreach (FrameworkElement p in new[] { mainPanel, settingsPanel, setupPanel, logPanel })
                p.Visibility = p == panel ? Visibility.Visible : Visibility.Hidden;
            bool back = panel == mainPanel || (panel == settingsPanel && currentPanel == logPanel);
            currentPanel = panel;
            if (panel == logPanel) FillLog();
            if (panel == settingsPanel) ShowValues();
            Render();
            if (!animate) return;
            var slide = new TranslateTransform(back ? -14 : 14, 0);
            panel.RenderTransform = slide;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        // ---------- main page ----------

        private void WireMain()
        {
            sourceBox.SelectionChanged += delegate
            {
                var c = sourceBox.SelectedItem as Choice;
                if (!loading && c != null && c.Value != null) router.SelectSource(Copy((SourceTarget)c.Value));
            };
            micBox.SelectionChanged += delegate
            {
                var c = micBox.SelectedItem as Choice;
                if (!loading && c != null && c.Value != null) router.SelectMic((AudioDevice)c.Value);
            };
            outputBox.SelectionChanged += delegate
            {
                var c = outputBox.SelectedItem as Choice;
                if (!loading && c != null && c.Value != null) router.SelectOutput((AudioDevice)c.Value);
            };
            // refresh the app list just before it opens, so a newly started app is there
            sourceBox.DropDownOpened += delegate { router.RefreshApps(); FillSources(); };
            for (int i = 0; i < modeSegments.Length; i++)
            {
                var mode = (RouteMode)i;
                modeSegments[i].Checked += delegate { if (!loading) router.Mode = mode; };
            }
            RoutedPropertyChangedEventHandler<double> volume = delegate
            {
                ShowVolumeText();
                if (loading) return;
                router.SetVolumes((float)(musicSlider.Value / 100), (float)(micSlider.Value / 100), (float)(masterSlider.Value / 100));
                MatchPreset();
            };
            musicSlider.ValueChanged += volume;
            micSlider.ValueChanged += volume;
            masterSlider.ValueChanged += volume;
            muteMusicButton.Click += delegate { router.MuteMusic = muteMusicButton.IsChecked == true; };
            muteMicButton.Click += delegate { router.MuteMic = muteMicButton.IsChecked == true; };
            muteAllButton.Click += delegate { router.MuteAll = muteAllButton.IsChecked == true; };
            pushToggle.Click += delegate { router.PushToMusic = pushToggle.IsChecked == true; };
            monitorToggle.Click += delegate { router.SetMonitor(monitorToggle.IsChecked == true); };
            startButton.Click += delegate { router.Start(); };
            stopButton.Click += delegate { router.Stop(); };

            presetBox.SelectionChanged += delegate
            {
                var c = presetBox.SelectedItem as Choice;
                if (loading || c == null || c.Value == null) return;
                router.ApplyPreset((Preset)c.Value);
                ShowValues();
            };
            Find<Button>("SavePresetButton").Click += delegate
            {
                presetPicker.Visibility = Visibility.Collapsed;
                presetNamer.Visibility = Visibility.Visible;
                presetName.Text = "";
                presetName.Focus();
            };
            Find<Button>("PresetOkButton").Click += delegate { SavePreset(); };
            Find<Button>("PresetCancelButton").Click += delegate { ClosePresetNamer(); };
            presetName.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter) SavePreset();
                else if (e.Key == Key.Escape) ClosePresetNamer();
            };
        }

        private static SourceTarget Copy(SourceTarget t)
        {
            return new SourceTarget { Exe = t.Exe, Name = t.Name, ProcessId = t.ProcessId, EverythingElse = t.EverythingElse, WholeDevice = t.WholeDevice };
        }

        private void SavePreset()
        {
            string name = presetName.Text.Trim();
            if (name.Length == 0) { ClosePresetNamer(); return; }
            foreach (Preset b in Preset.BuiltIns)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) name += " (mine)";
            settings.Custom.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            settings.Custom.Add(new Preset { Name = name, Music = settings.MusicVolume, Mic = settings.MicVolume, Master = settings.MasterVolume });
            settings.Save();
            Logger.Write("Preset saved: " + name);
            ClosePresetNamer();
            ShowValues();
        }

        private void ClosePresetNamer()
        {
            presetNamer.Visibility = Visibility.Collapsed;
            presetPicker.Visibility = Visibility.Visible;
        }

        private void ShowVolumeText()
        {
            musicValue.Text = Math.Round(musicSlider.Value) + "%";
            micValue.Text = Math.Round(micSlider.Value) + "%";
            masterValue.Text = Math.Round(masterSlider.Value) + "%";
        }

        /// <summary>Puts the saved values on every control without triggering their handlers.</summary>
        private void ShowValues()
        {
            loading = true;
            FillSources();
            FillDevices();
            modeSegments[(int)router.Mode].IsChecked = true;
            musicSlider.Value = settings.MusicVolume * 100;
            micSlider.Value = settings.MicVolume * 100;
            masterSlider.Value = settings.MasterVolume * 100;
            ShowVolumeText();
            muteMusicButton.IsChecked = router.MuteMusic;
            muteMicButton.IsChecked = router.MuteMic;
            muteAllButton.IsChecked = router.MuteAll;
            pushToggle.IsChecked = settings.PushToMusic;
            monitorToggle.IsChecked = router.Monitor;
            FillPresets();

            optStartMinimized.IsChecked = settings.StartMinimized;
            optWindows.IsChecked = StartWithWindows.IsOn;
            optRouteAtLaunch.IsChecked = settings.RouteAtLaunch;
            optAutoReconnect.IsChecked = settings.AutoReconnect;
            optRememberVolumes.IsChecked = settings.RememberVolumes;
            optRememberMode.IsChecked = settings.RememberMode;
            if (!capturing) keyValueText.Text = KeyNames.Name(settings.PushKey);
            FillDefaults();
            loading = false;
        }

        private void FillSources()
        {
            var items = new List<Choice>();
            bool lockedListed = false;
            foreach (AudioApp app in router.Apps)
            {
                if (router.Source != null && router.Source.Exe == app.Exe) lockedListed = true;
                items.Add(new Choice { Label = app.Name, Note = app.Playing ? "playing" : "", Value = app.ToTarget() });
            }
            if (router.Source != null && router.Source.Exe != null && !lockedListed)
                items.Insert(0, new Choice { Label = router.Source.Name, Note = "not playing", Value = router.Source });
            SourceTarget everything = Router.EverythingTarget();
            items.Add(new Choice { Label = everything.Name, Note = "", Value = everything });
            Choice selected = null;
            foreach (Choice c in items)
            {
                var t = (SourceTarget)c.Value;
                if (router.Source != null && ((t.Exe != null && t.Exe == router.Source.Exe)
                    || (t.Exe == null && t.EverythingElse == router.Source.EverythingElse && t.WholeDevice == router.Source.WholeDevice && router.Source.Exe == null)))
                    selected = c;
            }
            if (router.Source == null) items.Insert(0, selected = new Choice { Label = "Choose an app…", Note = "", Value = null });
            Fill(sourceBox, items, selected, ref sourceSignature);
        }

        private void FillDevices()
        {
            var cableMics = new HashSet<string>(router.Outputs.Where(o => o.IsVirtualCable).Select(o => o.CableMicName));
            var mics = new List<Choice>();
            Choice micSelected = null;
            foreach (AudioDevice d in router.Mics)
            {
                if (cableMics.Contains(d.Name)) continue;   // the cable's own mic side would loop back
                var c = new Choice { Label = d.Name, Note = d.IsDefault ? "default" : "", Value = d };
                mics.Add(c);
                if (router.Mic != null && d.Id == router.Mic.Id) micSelected = c;
            }
            if (router.Mic != null && micSelected == null)
                mics.Insert(0, micSelected = new Choice { Label = router.Mic.Name, Note = "disconnected", Value = router.Mic });
            if (router.Mic == null) mics.Insert(0, micSelected = new Choice { Label = "Choose a microphone…", Note = "", Value = null });
            Fill(micBox, mics, micSelected, ref micSignature);

            var outputs = new List<Choice>();
            Choice outSelected = null;
            foreach (AudioDevice d in router.Outputs.OrderBy(o => o.IsVirtualCable ? 0 : 1))
            {
                var c = new Choice { Label = d.Name, Note = d.IsVirtualCable ? "virtual mic" : "", Value = d };
                outputs.Add(c);
                if (router.Output != null && d.Id == router.Output.Id) outSelected = c;
            }
            if (router.Output != null && outSelected == null)
                outputs.Insert(0, outSelected = new Choice { Label = router.Output.Name, Note = "disconnected", Value = router.Output });
            if (router.Output == null)
                outputs.Insert(0, outSelected = new Choice { Label = router.CableInstalled ? "Choose an output…" : "No virtual cable found", Note = "", Value = null });
            Fill(outputBox, outputs, outSelected, ref outputSignature);
        }

        /// <summary>Refills a dropdown only if its entries changed (never while it's open), keeping the selection.</summary>
        private void Fill(ComboBox box, List<Choice> items, Choice selected, ref string signature)
        {
            string sig = string.Join("\n", items.Select(c => c.Label + "|" + c.Note));
            bool was = loading;
            loading = true;
            if (sig != signature && !box.IsDropDownOpen)
            {
                signature = sig;
                box.ItemsSource = items;
                box.SelectedItem = selected;
            }
            else if (box.ItemsSource != null && selected != null)
            {
                foreach (Choice c in (List<Choice>)box.ItemsSource)
                    if (c.Label == selected.Label && c.Note == selected.Note) { if (box.SelectedItem != c) box.SelectedItem = c; break; }
            }
            loading = was;
        }

        private void FillPresets()
        {
            var items = new List<Choice>();
            foreach (Preset p in Preset.BuiltIns) items.Add(new Choice { Label = p.Name, Note = Describe(p), Value = p });
            foreach (Preset p in settings.Custom) items.Add(new Choice { Label = p.Name, Note = Describe(p), Value = p });
            items.Add(new Choice { Label = "Custom", Note = "your own levels", Value = null });
            presetBox.ItemsSource = items;
            MatchPreset();

            var saved = settings.Custom.Select(p => new Choice { Label = p.Name, Note = Describe(p), Value = p }).ToList();
            savedPresetBox.ItemsSource = saved;
            if (saved.Count > 0) savedPresetBox.SelectedIndex = 0;
            savedPresetRow.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            presetListText.Text = saved.Count > 0 ? "Saved: " + string.Join(", ", settings.Custom.Select(p => p.Name)) + "."
                : "None yet. Set the volumes, then press Save next to Preset.";
        }

        private static string Describe(Preset p)
        {
            return "music " + Math.Round(p.Music * 100) + "% · mic " + Math.Round(p.Mic * 100) + "%";
        }

        /// <summary>Shows which preset the current volumes match, or Custom.</summary>
        private void MatchPreset()
        {
            var items = presetBox.ItemsSource as List<Choice>;
            if (items == null) return;
            Choice match = items[items.Count - 1];
            foreach (Choice c in items)
            {
                var p = c.Value as Preset;
                if (p != null && Near(p.Music, settings.MusicVolume) && Near(p.Mic, settings.MicVolume) && Near(p.Master, settings.MasterVolume))
                {
                    match = c;
                    break;
                }
            }
            bool was = loading;
            loading = true;
            presetBox.SelectedItem = match;
            loading = was;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.006f; }

        // ---------- settings page ----------

        private void WireSettings()
        {
            Option(optStartMinimized, on => settings.StartMinimized = on);
            Option(optRouteAtLaunch, on => settings.RouteAtLaunch = on);
            Option(optAutoReconnect, on => settings.AutoReconnect = on);
            Option(optRememberVolumes, on => settings.RememberVolumes = on);
            Option(optRememberMode, on => settings.RememberMode = on);
            Option(optWindows, on =>
            {
                if (!StartWithWindows.Set(on)) Logger.Write("Couldn't change Start with Windows");
                else Logger.Write("Start with Windows: " + (on ? "on" : "off"));
            });
            defaultSourceBox.SelectionChanged += delegate
            {
                var c = defaultSourceBox.SelectedItem as Choice;
                if (loading || c == null) return;
                var t = c.Value as SourceTarget;
                settings.DefaultSourceExe = t != null && t.Exe != null ? t.Exe : "";
                settings.DefaultSourceName = t != null && t.Exe != null ? t.Name : "";
                settings.Save();
                Logger.Write("Default source: " + (t != null ? t.Name : "automatic"));
            };
            defaultMicBox.SelectionChanged += delegate
            {
                var c = defaultMicBox.SelectedItem as Choice;
                if (loading || c == null) return;
                var d = c.Value as AudioDevice;
                settings.DefaultMicId = d != null ? d.Id : "";
                settings.DefaultMicName = d != null ? d.Name : "";
                settings.Save();
                Logger.Write("Default microphone: " + (d != null ? d.Name : "Windows default"));
            };
            defaultOutputBox.SelectionChanged += delegate
            {
                var c = defaultOutputBox.SelectedItem as Choice;
                if (loading || c == null) return;
                var d = c.Value as AudioDevice;
                settings.DefaultOutputId = d != null ? d.Id : "";
                settings.DefaultOutputName = d != null ? d.Name : "";
                settings.Save();
                Logger.Write("Default output: " + (d != null ? d.Name : "first virtual cable"));
            };
            Find<Button>("DeletePresetButton").Click += delegate
            {
                var c = savedPresetBox.SelectedItem as Choice;
                if (c == null) return;
                settings.Custom.Remove((Preset)c.Value);
                settings.Save();
                Logger.Write("Preset deleted: " + c.Label);
                ShowValues();
            };
        }

        private void Option(CheckBox box, Action<bool> apply)
        {
            RoutedEventHandler changed = delegate
            {
                if (loading) return;
                apply(box.IsChecked == true);
                settings.Save();
            };
            box.Checked += changed;
            box.Unchecked += changed;
        }

        private void FillDefaults()
        {
            var sources = new List<Choice> { new Choice { Label = "Whatever is playing", Note = "automatic", Value = null } };
            Choice srcSel = sources[0];
            bool listed = false;
            foreach (AudioApp app in router.Apps)
            {
                var c = new Choice { Label = app.Name, Note = "", Value = app.ToTarget() };
                sources.Add(c);
                if (app.Exe == settings.DefaultSourceExe) { srcSel = c; listed = true; }
            }
            if (!listed && settings.DefaultSourceExe.Length > 0)
            {
                var c = new Choice { Label = settings.DefaultSourceName, Note = "not running", Value = new SourceTarget { Exe = settings.DefaultSourceExe, Name = settings.DefaultSourceName } };
                sources.Insert(1, c);
                srcSel = c;
            }
            defaultSourceBox.ItemsSource = sources;
            defaultSourceBox.SelectedItem = srcSel;

            var mics = new List<Choice> { new Choice { Label = "Windows default microphone", Note = "automatic", Value = null } };
            Choice micSel = mics[0];
            foreach (AudioDevice d in router.Mics)
            {
                if (Devices.LooksLikeCable(d.Name)) continue;
                var c = new Choice { Label = d.Name, Note = "", Value = d };
                mics.Add(c);
                if (d.Id == settings.DefaultMicId) micSel = c;
            }
            defaultMicBox.ItemsSource = mics;
            defaultMicBox.SelectedItem = micSel;

            var outs = new List<Choice> { new Choice { Label = "First virtual cable", Note = "automatic", Value = null } };
            Choice outSel = outs[0];
            foreach (AudioDevice d in router.Outputs)
            {
                var c = new Choice { Label = d.Name, Note = d.IsVirtualCable ? "virtual mic" : "", Value = d };
                outs.Add(c);
                if (d.Id == settings.DefaultOutputId) outSel = c;
            }
            defaultOutputBox.ItemsSource = outs;
            defaultOutputBox.SelectedItem = outSel;
        }

        // ---------- push-to-music key: picked on the settings page ----------

        private void WireKeyCapture()
        {
            changeKeyButton.Click += delegate
            {
                if (capturing) EndKeyCapture();
                else StartKeyCapture();
            };
            Window.PreviewKeyDown += (sender, e) =>
            {
                if (!capturing)
                {
                    if (e.Key == Key.Escape && currentPanel != mainPanel && presetNamer.Visibility != Visibility.Visible)
                    {
                        ShowPanel(currentPanel == logPanel ? settingsPanel : mainPanel);
                        e.Handled = true;
                    }
                    return;
                }
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { EndKeyCapture(); return; }
                OfferKey(KeyInterop.VirtualKeyFromKey(key));
            };
            Window.PreviewMouseDown += (sender, e) =>
            {
                if (!capturing) return;
                int vk = e.ChangedButton == MouseButton.XButton1 ? 0x05 : e.ChangedButton == MouseButton.XButton2 ? 0x06
                    : e.ChangedButton == MouseButton.Middle ? 0x04 : 0;
                if (vk == 0) return;
                e.Handled = true;
                OfferKey(vk);
            };
        }

        internal void StartKeyCapture()
        {
            capturing = true;
            keyValueText.Text = "Press a key…";
            keyValueText.Foreground = Accent;
            keyHint.Text = "Press the key or mouse button to hold. Esc cancels.";
            keyHint.Foreground = Muted;
            changeKeyButton.Content = "Cancel";
            Window.Activate();
            Keyboard.Focus(Window);
        }

        private void EndKeyCapture()
        {
            capturing = false;
            keyValueText.Foreground = Light;
            keyValueText.Text = KeyNames.Name(settings.PushKey);
            keyHint.Text = "Keys and mouse buttons 4/5 work. Nothing is blocked: Roblox still gets the key.";
            keyHint.Foreground = Muted;
            changeKeyButton.Content = "Change";
        }

        /// <summary>Takes the key, or explains (with a little shake) why it can't be used.</summary>
        internal bool OfferKey(int vk)
        {
            string problem = KeyNames.Problem(vk);
            if (problem != null)
            {
                keyHint.Text = KeyNames.Name(vk) + " can't be used: " + problem + ".";
                keyHint.Foreground = Danger;
                Shake(keyHint);
                return false;
            }
            router.SetPushKey(vk);
            EndKeyCapture();
            Render();
            return true;
        }

        internal string KeyMessage { get { return keyHint.Text; } }

        private static void Shake(UIElement element)
        {
            var move = (TranslateTransform)element.RenderTransform;
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
            double[] at = { 0, -6, 6, -4, 4, -2, 0 };
            for (int i = 0; i < at.Length; i++)
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(at[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 60))));
            move.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        // ---------- status ----------

        private void Render()
        {
            if (quitting) return;
            statusText.Text = router.Status;
            statusDetail.Text = router.Detail;
            statusText.Foreground = router.Health == Health.Error ? Danger : Light;
            if (router.Health != shownHealth)
            {
                shownHealth = router.Health;
                Color c = router.Health == Health.Ok ? Green : router.Health == Health.Warning ? Amber : router.Health == Health.Error ? Red : Gray;
                dotFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(300)));
                tray.Icon = router.Routing && router.Health == Health.Ok ? onIcon : router.Health == Health.Warning || router.Health == Health.Error ? warnIcon : offIcon;
            }
            startButton.IsEnabled = !router.Routing;
            stopButton.IsEnabled = router.Routing;
            startText.Text = router.Routing ? "Routing…" : router.LostDevice != null ? "Reconnect" : "Start routing";

            AudioDevice output = router.Output;
            outputHint.Text = output == null ? (router.CableInstalled ? "Pick the virtual cable." : "No virtual cable installed. See Roblox Setup (the ? button).")
                : output.IsVirtualCable ? "Roblox mic: “" + output.CableMicName + "”"
                : "This isn't a virtual cable, so Roblox can't use it as a microphone.";
            outputHint.Foreground = output != null && output.IsVirtualCable ? (Brush)Muted : Freeze(Amber);

            string key = KeyNames.Name(settings.PushKey);
            pushHint.Text = "Music only goes out while you hold " + key + ".";
            monitorHint.Text = router.MonitorCouldEcho
                ? "Use headphones: speakers can echo into your mic."
                : "Hear exactly what Roblox gets, on your speakers.";
            monitorHint.Foreground = router.MonitorCouldEcho ? Danger : Muted;
            bool was = loading;
            loading = true;
            monitorToggle.IsChecked = router.Monitor;
            modeSegments[(int)router.Mode].IsChecked = true;
            loading = was;

            // the source list changes as apps start and stop playing
            if (currentPanel == mainPanel && Window.IsVisible)
            {
                loading = true;
                FillSources();
                FillDevices();
                loading = was;
            }

            RenderSetup();
            RenderTray();

            bool metering = router.Routing && Window.IsVisible && currentPanel == mainPanel;
            if (metering && !meterTimer.IsEnabled) meterTimer.Start();
            else if (!metering && meterTimer.IsEnabled)
            {
                meterTimer.Stop();
                musicMeter.Reset();
                micMeter.Reset();
                outMeter.Reset();
            }
        }

        private void RenderSetup()
        {
            AudioDevice cable = router.FirstCable;
            if (cable != null)
            {
                cableIcon.Text = "";
                cableIcon.Foreground = Freeze(Green);
                cableTitle.Text = "Virtual microphone found";
                cableText.Text = "Music to Mic plays into “" + cable.Name + "”. In Roblox, pick “" + cable.CableMicName
                    + "” as your microphone.";
                cableDownloadButton.Visibility = Visibility.Collapsed;
                stepSeven.Text = "7.  Pick “" + cable.CableMicName + "” as the microphone.";
            }
            else
            {
                cableIcon.Text = "";
                cableIcon.Foreground = Freeze(Amber);
                cableTitle.Text = "Virtual microphone not detected";
                cableText.Text = "Windows can't make a new microphone without a driver, so Music to Mic sends the music into VB-CABLE, "
                    + "a free virtual audio cable by VB-Audio. Download it, run VBCABLE_Setup_x64.exe as administrator, "
                    + "restart the PC, then come back: it shows up here by itself.";
                cableDownloadButton.Visibility = Visibility.Visible;
                stepSeven.Text = "7.  Pick “CABLE Output (VB-Audio Virtual Cable)” as the microphone.";
            }
        }

        // ---------- meters ----------

        private void UpdateMeters()
        {
            float music, mic, output;
            bool clipped;
            router.Mixer.TakePeaks(out music, out mic, out output, out clipped);
            musicMeter.Show(music, router.MuteMusic || router.MuteAll);
            micMeter.Show(mic, router.MuteMic || router.MuteAll);
            outMeter.Show(output, router.MuteAll);
            if (clipped) limitUntil = DateTime.UtcNow.AddSeconds(1.5);
            bool limiting = DateTime.UtcNow < limitUntil;
            limitText.Opacity = limiting ? 1 : 0;
            outDb.Foreground = limiting ? Danger : Muted;
        }

        /// <summary>One level meter: falls smoothly, holds its peak for a second.</summary>
        private sealed class Meter
        {
            private readonly FrameworkElement track, cover, peak;
            private readonly TextBlock db;
            private double level, peakLevel;
            private DateTime peakAt;

            public Meter(MainWindow w, string prefix)
            {
                track = w.Find<FrameworkElement>(prefix + "Meter");
                cover = w.Find<FrameworkElement>(prefix + "MeterCover");
                peak = w.Find<FrameworkElement>(prefix + "MeterPeak");
                db = w.Find<TextBlock>(prefix + "Db");
                Reset();
            }

            /// <summary>0..1 position for a sample peak: -60 dB at the left, 0 dB at the right.</summary>
            public static double Position(float sample)
            {
                if (sample <= 0.001f) return 0;
                double dB = 20 * Math.Log10(sample);
                return Math.Max(0, Math.Min(1, (dB + 60) / 60));
            }

            public void Show(float sample, bool muted)
            {
                double target = Position(sample);
                level = target > level ? target : Math.Max(target, level - 0.012);   // falls ~22 dB a second
                if (level >= peakLevel || DateTime.UtcNow - peakAt > TimeSpan.FromSeconds(1))
                {
                    peakLevel = level;
                    peakAt = DateTime.UtcNow;
                }
                double width = track.ActualWidth;
                cover.HorizontalAlignment = HorizontalAlignment.Right;
                cover.Width = Math.Max(0, width * (1 - level));
                peak.Margin = new Thickness(Math.Max(0, width * peakLevel - 2), 0, 0, 0);
                peak.Opacity = peakLevel > 0.02 ? 0.8 : 0;
                track.Opacity = muted ? 0.35 : 1;
                db.Text = sample <= 0.001f ? "-∞ dB" : (20 * Math.Log10(sample)).ToString("0") + " dB";
            }

            public void Reset()
            {
                level = peakLevel = 0;
                cover.ClearValue(FrameworkElement.WidthProperty);          // covers the whole bar: nothing lit
                cover.HorizontalAlignment = HorizontalAlignment.Stretch;
                peak.Opacity = 0;
                db.Text = "-∞ dB";
                track.Opacity = 1;
            }
        }

        // ---------- activity log page ----------

        private void FillLog()
        {
            logText.Text = string.Join("\n", Logger.Recent);
            logScroll.ScrollToEnd();
        }

        private void AppendLog(string line)
        {
            logText.Text = logText.Text.Length == 0 ? line : logText.Text + "\n" + line;
            logScroll.ScrollToEnd();
        }

        // ---------- the hidden-icons icon and its menu ----------

        private void CreateTray()
        {
            int size = Forms.SystemInformation.SmallIconSize.Width;
            onIcon = TrayIcon("on", size);
            offIcon = TrayIcon("off", size);
            warnIcon = TrayIcon("warn", size);
            tray = new Forms.NotifyIcon { Text = AppInfo.Name, Icon = offIcon };
            trayMenu = new Forms.ContextMenuStrip
            {
                Renderer = new TrayMenuRenderer(),
                Font = new Drawing.Font("Segoe UI", 9.5f),
                Padding = new Forms.Padding(2, 4, 2, 4),
            };
            routingItem = TrayItem("Routing On", false, delegate { if (router.Routing) router.Stop(); else router.Start(); });
            muteMusicItem = TrayItem("Mute Music", false, delegate { router.MuteMusic = !router.MuteMusic; ShowValues(); });
            muteMicItem = TrayItem("Mute Mic", false, delegate { router.MuteMic = !router.MuteMic; ShowValues(); });
            trayMenu.Items.AddRange(new Forms.ToolStripItem[]
            {
                routingItem,
                muteMusicItem,
                muteMicItem,
                new Forms.ToolStripSeparator(),
                TrayItem("Open " + AppInfo.Name, true, delegate { ShowFromTray(); }),
                new Forms.ToolStripSeparator(),
                TrayItem("Exit", false, delegate { Quit(); }),
            });
            tray.ContextMenuStrip = trayMenu;
            tray.MouseClick += (sender, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
            tray.Visible = true;
            router.Changed += delegate
            {
                // tell someone who isn't looking that a device dropped out
                if (!Window.IsVisible && router.Health == Health.Error && router.LostDevice != null && lastWarned != router.Status)
                {
                    lastWarned = router.Status;
                    tray.ShowBalloonTip(5000, AppInfo.Name + ": routing stopped", router.Status + ". " + router.Detail, Forms.ToolTipIcon.Warning);
                }
                if (router.Health != Health.Error) lastWarned = null;
            };
        }

        private string lastWarned;

        private static Drawing.Icon TrayIcon(string state, int size)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MusicToMic.tray-" + state + ".ico"))
                return new Drawing.Icon(s, size, size);
        }

        private void RenderTray()
        {
            routingItem.Checked = router.Routing;
            muteMusicItem.Checked = router.MuteMusic;
            muteMicItem.Checked = router.MuteMic;
            string tip = AppInfo.Name + ": " + (router.Routing ? router.Status.ToLowerInvariant() + (router.Source != null && router.Mode != RouteMode.MicOnly ? " " + router.Source.Name : "") : router.Status.ToLowerInvariant());
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tray.Text != tip) tray.Text = tip;
        }

        private static Forms.ToolStripMenuItem TrayItem(string text, bool bold, EventHandler onClick)
        {
            var item = new Forms.ToolStripMenuItem(text)
            {
                ForeColor = Drawing.Color.FromArgb(0xEE, 0xEE, 0xF4),
                Padding = new Forms.Padding(6, 5, 18, 5),
            };
            if (bold) item.Font = new Drawing.Font("Segoe UI Semibold", 9.5f);
            if (onClick != null) item.Click += onClick;
            return item;
        }
    }

    /// <summary>The dark right-click menu of the hidden-icons icon: light arrows and a violet tick.</summary>
    internal sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Light = Drawing.Color.FromArgb(0xEE, 0xEE, 0xF4);
        private static readonly Drawing.Color Tick = Drawing.Color.FromArgb(0xA7, 0x8B, 0xFA);

        public TrayMenuRenderer() : base(new TrayMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Light;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            Drawing.Rectangle r = e.ImageRectangle;
            float s = r.Height / 16f, x = r.Left + r.Width / 2f, y = r.Top + r.Height / 2f;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Drawing.Pen(Tick, 1.8f * s))
            {
                pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Drawing.PointF(x - 4.5f * s, y + 0.2f * s),
                    new Drawing.PointF(x - 1.5f * s, y + 3.2f * s),
                    new Drawing.PointF(x + 4.5f * s, y - 3.3f * s),
                });
            }
        }
    }

    internal sealed class TrayMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x1F, 0x1F, 0x26);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2D, 0x2D, 0x38);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x30, 0x30, 0x3A);

        public override Drawing.Color ToolStripDropDownBackground { get { return Back; } }
        public override Drawing.Color ImageMarginGradientBegin { get { return Back; } }
        public override Drawing.Color ImageMarginGradientMiddle { get { return Back; } }
        public override Drawing.Color ImageMarginGradientEnd { get { return Back; } }
        public override Drawing.Color MenuBorder { get { return Line; } }
        public override Drawing.Color MenuItemBorder { get { return Hover; } }
        public override Drawing.Color MenuItemSelected { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Drawing.Color SeparatorDark { get { return Line; } }
        public override Drawing.Color SeparatorLight { get { return Back; } }
    }
}

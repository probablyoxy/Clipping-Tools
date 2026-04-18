using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading;
using NHotkey;
using NHotkey.Wpf;
using WindowsInput;
using WindowsInput.Native;
using System.Diagnostics;
using System.Collections.Generic;

namespace ClippingTools.app
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [DllImport("winmm.dll")]
        private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public int dwSize;
            public int dwFlags;
            public int dwXpos;
            public int dwYpos;
            public int dwZpos;
            public int dwRpos;
            public int dwUpos;
            public int dwVpos;
            public int dwButtons;
            public int dwButtonNumber;
            public int dwPOV;
            public int dwReserved1;
            public int dwReserved2;
        }
        private const int JOY_RETURNBUTTONS = 0x00000080;
        private const int JOY_RETURNPOV = 0x00000040;
        private const int JOY_RETURNPOVCTS = 0x00000200;
        private const int JOYERR_NOERROR = 0;

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out int pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        }

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            StringBuilder buff = new StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();

            if (GetWindowText(handle, buff, nChars) > 0)
            {
                return buff.ToString();
            }
            return null;
        }

        private EventWaitHandle singleInstanceWatcher;
        private bool isSingleInstance;

        private CancellationTokenSource controllerCts;
        private bool isCapturingControllerGlobal = false;
        private bool isCapturingControllerLocal = false;
        private string lastControllerStateStr = "";
        private bool wasControllerTriggerPressed = false;
        private bool wasControllerLocalTriggerPressed = false;
        private string preCaptureGlobalText = "";
        private string preCaptureLocalText = "";

        private CancellationTokenSource wheelCts;
        private bool isCapturingWheelGlobal = false;
        private bool isCapturingWheelLocal = false;
        private bool wasWheelTriggerPressed = false;
        private bool wasWheelLocalTriggerPressed = false;
        private string preCaptureWheelGlobalText = "";
        private string preCaptureWheelLocalText = "";
        private string currentCapturedWheelCombo = "";
        private int currentCapturedWheelComboCount = 0;

        private string currentCapturedCombo = "";
        private int currentCapturedComboCount = 0;

        private string appUuid = "";
        private string activeAppUuid = "";
        private InputSimulator simulator = new InputSimulator();
        private CancellationTokenSource obsWatchdogCts;
        private CancellationTokenSource renamerStatusCts;
        private MediaPlayer customAudioPlayer = new MediaPlayer();
        private System.Windows.Forms.NotifyIcon trayIcon;
        private bool forceExit = false;
        private CancellationTokenSource windowSaveCts;
        private bool needsRenamerUpdate = false;

        public ObservableCollection<DiscordItem> ApprovedUsers { get; set; } = new ObservableCollection<DiscordItem>();
        public ObservableCollection<DiscordItem> ApprovedChannels { get; set; } = new ObservableCollection<DiscordItem>();

        public ObservableCollection<DiscordItem> AllVisibleUsers { get; set; } = new ObservableCollection<DiscordItem>();
        private List<DiscordItem> RawFetchedUsers = new List<DiscordItem>();

        public List<string> ClipKeysList { get; set; } = new List<string>();

        private readonly string configFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
        private string configFilePath => System.IO.Path.Combine(configFolder, "settings.json");
        private string tokensFolder => System.IO.Path.Combine(configFolder, ".tokens");
        private string appTokensFolder => System.IO.Path.Combine(configFolder, ".tokens", "app");
        private string customTokensFolder => System.IO.Path.Combine(configFolder, ".tokens", "custom");
        private string soundsFolder => System.IO.Path.Combine(configFolder, "sounds");
        private string systemsSoundsFolder => System.IO.Path.Combine(soundsFolder, "system");
        private string customSoundsFolder => System.IO.Path.Combine(soundsFolder, "custom");

        private bool isLoaded = false;
        private DateTime lastClipTime = DateTime.MinValue;

        private TotalClipsStat totalClipsStats = new TotalClipsStat();
        private Dictionary<string, UserStatCount> userSentStats = new Dictionary<string, UserStatCount>();
        private Dictionary<string, UserStatCount> userReceivedStats = new Dictionary<string, UserStatCount>();

        // CHANGE WHEN UPDATE :)
        //
        //
        private const string AppVersion = "v0.1.9";
        //
        //
        // CHANGE WHEN UPDATE :)
        private string downloadUrlForUpdate = "";

        private ClientWebSocket webSocket;
        private CancellationTokenSource wsCts;
        private bool isSyncActive = false;
        private bool isReconnecting = false;
        private List<string> currentActiveVcFriends = new List<string>();
        private string currentVcName = "";
        private string currentVcId = "";
        private string previousVcId = "";

        private List<string> currentActivePoolFriends = new List<string>();
        private string activePoolCode = "";
        private string previousPoolCode = "";
        private bool amIPoolOwner = false;
        private string currentPoolName = "";
        private string currentServerName = "";
        private string currentPerspectiveName = "";
        private string myGlobalDiscordName = "";

        private bool isConnectPoolEnabled = false;
        private bool isConnectVCEnabled = false;
        private bool isDisconnectPoolEnabled = false;
        private bool isDisconnectVCEnabled = false;

        public MainWindow()
        {
            singleInstanceWatcher = new EventWaitHandle(false, EventResetMode.AutoReset, "ClippingTools_SingleInstanceEvent", out isSingleInstance);
            if (!isSingleInstance)
            {
                singleInstanceWatcher.Set();
                Application.Current.Shutdown();
                return;
            }

            Task.Run(() => {
                while (true)
                {
                    singleInstanceWatcher.WaitOne();
                    Dispatcher.Invoke(() => {
                        this.Show();
                        if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal;
                        this.Activate();
                        this.Topmost = true;
                        this.Topmost = false;
                        this.Focus();
                    });
                }
            });

            InitializeComponent();

            UserListBox.ItemsSource = ApprovedUsers;
            ChannelListBox.ItemsSource = ApprovedChannels;
            AllUsersListBox.ItemsSource = AllVisibleUsers;

            var userView = (ListCollectionView)CollectionViewSource.GetDefaultView(ApprovedUsers);
            userView.IsLiveSorting = true;
            userView.LiveSortingProperties.Add("DisplayName");
            userView.SortDescriptions.Add(new SortDescription("DisplayName", ListSortDirection.Ascending));

            var channelView = (ListCollectionView)CollectionViewSource.GetDefaultView(ApprovedChannels);
            channelView.IsLiveSorting = true;
            channelView.LiveSortingProperties.Add("DisplayName");
            channelView.SortDescriptions.Add(new SortDescription("DisplayName", ListSortDirection.Ascending));

            var allUserView = (ListCollectionView)CollectionViewSource.GetDefaultView(AllVisibleUsers);
            allUserView.IsLiveSorting = true;
            allUserView.LiveSortingProperties.Add("DisplayName");
            allUserView.SortDescriptions.Add(new SortDescription("DisplayName", ListSortDirection.Ascending));

            isLoaded = true;
            CheckAndUpdateVersion();
            isLoaded = false;

            PopulateMonitorCombos();

            LoadSettings();
            LoadStats();
            isLoaded = true;
            WriteLog("Clipping Tools Started");

            CurrentVersionText.Text = AppVersion;

            SetupTrayIcon();

            this.Loaded += (s, e) =>
            {
                if (Environment.GetCommandLineArgs().Contains("--minimized"))
                {
                    this.Hide();
                }
            };

            VerifyStartWithWindowsTask();
            VerifyShortcuts();

            if (AutoSyncCheck.IsChecked == true)
            {
                StartListening();
            }

            _ = CheckForUpdatesAsync(true);
            _ = EnforceObsStartOnLaunch();
            EnforceTimeSync();
            ToggleRenamerService();

            micWatcherCts = new CancellationTokenSource();
            _ = WatchMicVolume(micWatcherCts.Token);

            controllerCts = new CancellationTokenSource();
            _ = ControllerPollingLoop(controllerCts.Token);

            wheelCts = new CancellationTokenSource();
            _ = WheelPollingLoop(wheelCts.Token);
        }

        private void PopulateMonitorCombos()
        {
            ClipNotifMonitorCombo.Items.Clear();
            ConnectNotifMonitorCombo.Items.Clear();
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                string name = $"Monitor {i + 1}" + (screens[i].Primary ? " (Primary)" : "");
                ClipNotifMonitorCombo.Items.Add(new ComboBoxItem { Content = name });
                ConnectNotifMonitorCombo.Items.Add(new ComboBoxItem { Content = name });
            }

            if (screens.Length <= 1)
            {
                if (ClipNotifMonitorLabel != null) ClipNotifMonitorLabel.Visibility = Visibility.Collapsed;
                if (ClipNotifMonitorCombo != null) ClipNotifMonitorCombo.Visibility = Visibility.Collapsed;
                if (ConnectNotifMonitorLabel != null) ConnectNotifMonitorLabel.Visibility = Visibility.Collapsed;
                if (ConnectNotifMonitorCombo != null) ConnectNotifMonitorCombo.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (ClipNotifMonitorLabel != null) ClipNotifMonitorLabel.Visibility = Visibility.Visible;
                if (ClipNotifMonitorCombo != null) ClipNotifMonitorCombo.Visibility = Visibility.Visible;
                if (ConnectNotifMonitorLabel != null) ConnectNotifMonitorLabel.Visibility = Visibility.Visible;
                if (ConnectNotifMonitorCombo != null) ConnectNotifMonitorCombo.Visibility = Visibility.Visible;
            }
        }

        private void SetupTrayIcon()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            trayIcon.Visible = true;
            trayIcon.Text = "Clipping Tools";
            trayIcon.MouseClick += (s, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                }
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            var toggleSyncItem = new System.Windows.Forms.ToolStripMenuItem();
            toggleSyncItem.Click += (s, args) =>
            {
                Dispatcher.Invoke(() => ConnectButton_Click(null, null));
            };
            contextMenu.Opening += (s, args) =>
            {
                toggleSyncItem.Text = isSyncActive ? "Connected" : "Disconnected";
                toggleSyncItem.Checked = isSyncActive;
            };

            contextMenu.Items.Add(toggleSyncItem);
            contextMenu.Items.Add("Open", null, (s, args) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); });
            contextMenu.Items.Add("Exit", null, (s, args) => { forceExit = true; System.Windows.Application.Current.Shutdown(); });
            trayIcon.ContextMenuStrip = contextMenu;
        }

        // ==============================================================================
        // INSTANT SAVE / LOAD SYSTEM
        // ==============================================================================

        private bool IsKeybindCollision(string triggerStr, List<string> clipKeys)
        {
            if (string.IsNullOrWhiteSpace(triggerStr) || clipKeys == null || clipKeys.Count == 0) return false;

            bool tCtrl = triggerStr.Contains("Ctrl");
            bool tAlt = triggerStr.Contains("Alt");
            bool tShift = triggerStr.Contains("Shift");

            string[] tParts = triggerStr.Split('+');
            string tKey = tParts.LastOrDefault() ?? "";
            if (tKey == "Ctrl" || tKey == "Alt" || tKey == "Shift") tKey = "";

            bool cCtrl = clipKeys.Any(k => k.Contains("Ctrl"));
            bool cAlt = clipKeys.Any(k => k.Contains("Alt") || k == "System");
            bool cShift = clipKeys.Any(k => k.Contains("Shift"));

            var cMainKeys = clipKeys.Where(k => !k.Contains("Ctrl") && !k.Contains("Alt") && !k.Contains("Shift") && k != "System" && k != "None").ToList();

            if (tCtrl == cCtrl && tAlt == cAlt && tShift == cShift)
            {
                if (cMainKeys.Count == 0 && string.IsNullOrEmpty(tKey)) return true;
                if (cMainKeys.Any(k => string.Equals(k, tKey, StringComparison.OrdinalIgnoreCase))) return true;
            }

            return false;
        }

        private void EnsureDotFilesHidden(string directoryPath)
        {
            if (!Directory.Exists(directoryPath)) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(directoryPath, ".*", SearchOption.AllDirectories))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        di.Attributes |= FileAttributes.Hidden;
                }
                foreach (var file in Directory.GetFiles(directoryPath, ".*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    if ((fi.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        fi.Attributes |= FileAttributes.Hidden;
                }
            }
            catch { }
        }

        private void LoadSettings()
        {
            if (!Directory.Exists(tokensFolder)) Directory.CreateDirectory(tokensFolder);
            if (!Directory.Exists(appTokensFolder)) Directory.CreateDirectory(appTokensFolder);

            string appUuidPath = System.IO.Path.Combine(appTokensFolder, "app_id.json");
            string discordIdPath = System.IO.Path.Combine(appTokensFolder, "discord_id.json");

            if (File.Exists(appUuidPath)) appUuid = File.ReadAllText(appUuidPath).Trim('"', ' ', '\n', '\r');
            else { appUuid = Guid.NewGuid().ToString(); File.WriteAllText(appUuidPath, $"\"{appUuid}\""); }

            if (File.Exists(discordIdPath)) DiscordIdInput.Text = File.ReadAllText(discordIdPath).Trim('"', ' ', '\n', '\r');

            if (!Directory.Exists(soundsFolder)) Directory.CreateDirectory(soundsFolder);
            if (!Directory.Exists(systemsSoundsFolder)) Directory.CreateDirectory(systemsSoundsFolder);

            string[] builtInSounds = { "simpleyviridian-clipped.wav", "simpleyviridian-clipped2.wav", "simpleyviridian-clippedteto.wav", "confusedindividual-clipped.mp3", "confusedindividual-clipped!.wav" };
            string[] builtInSystemSounds = { "confusedindividual-server connect.wav", "confusedindividual-server disconnect.wav", "simpleyviridian-connected.wav", "simpleyviridian-disconnected.wav", "simpleyviridian-connected2.wav", "simpleyviridian-disconnected2.wav" };

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            foreach (var s in builtInSounds)
            {
                string dest = System.IO.Path.Combine(soundsFolder, s);
                if (!File.Exists(dest))
                {
                    try
                    {
                        string resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                        if (resourceName != null)
                        {
                            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                            using (FileStream fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fileStream);
                            }
                        }
                    }
                    catch { }
                }
            }

            foreach (var s in builtInSystemSounds)
            {
                string dest = System.IO.Path.Combine(systemsSoundsFolder, s);
                if (!File.Exists(dest))
                {
                    try
                    {
                        string resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                        if (resourceName != null)
                        {
                            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                            using (FileStream fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fileStream);
                            }
                        }
                    }
                    catch { }
                }
            }

            if (!File.Exists(configFilePath))
            {
                ClipKeysList.Add("LeftAlt");
                ClipKeysList.Add("F10");
                RebuildClipKeyUI();
            }

            try
            {
                string json = File.ReadAllText(configFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);

                if (settings != null)
                {
                    SendClipsCheck.IsChecked = settings.SendClips;
                    ReceiveClipsCheck.IsChecked = settings.ReceiveClips;
                    RadioAnyVC.IsChecked = settings.AnyVCRule;
                    RadioSpecificVC.IsChecked = !settings.AnyVCRule;
                    TriggerKeyInput.Text = settings.TriggerKey;
                    LocalTriggerKeyInput.Text = settings.LocalTriggerKey ?? "";

                    ControllerTriggerKeyInput.Text = settings.ControllerTriggerKey ?? "";
                    ControllerLocalTriggerKeyInput.Text = settings.ControllerLocalTriggerKey ?? "";

                    WheelTriggerKeyInput.Text = settings.WheelTriggerKey ?? "";
                    WheelLocalTriggerKeyInput.Text = settings.WheelLocalTriggerKey ?? "";

                    if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
                    {
                        this.Width = settings.WindowWidth;
                        this.Height = settings.WindowHeight;
                    }
                    if (settings.WindowLeft != -1 && settings.WindowTop != -1)
                    {
                        this.WindowStartupLocation = WindowStartupLocation.Manual;
                        this.Left = settings.WindowLeft;
                        this.Top = settings.WindowTop;
                    }

                    AutoSyncCheck.IsChecked = settings.AutoSync;
                    StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
                    RateLimitInput.Text = settings.RateLimitSeconds >= 0 ? settings.RateLimitSeconds.ToString() : "10";
                    ClipDelayInput.Text = settings.ClipDelaySeconds >= 0 ? settings.ClipDelaySeconds.ToString() : "0";
                    ObsLocationInput.Text = settings.ObsPath;
                    ClipLocationInput.Text = settings.ClipPath;
                    AutoRenameClipsCheck.IsChecked = settings.AutoRenameClips;

                    RenameGameCheck.IsChecked = settings.RenameGame;
                    RenamePerspectiveCheck.IsChecked = settings.RenamePerspective;
                    RenameClipperCheck.IsChecked = settings.RenameClipper;
                    RenameVCCheck.IsChecked = settings.RenameVC;
                    RenameServerCheck.IsChecked = settings.RenameServer;
                    RenamePoolCheck.IsChecked = settings.RenamePool;
                    RenamerOptionsPanel.Visibility = (settings.AutoRenameClips) ? Visibility.Visible : Visibility.Collapsed;

                    AutoStartObsCheck.IsChecked = settings.AutoStartObs;
                    AutoRestartObsCheck.IsChecked = settings.AutoRestartObs;
                    ObsIntervalInput.Text = settings.ObsCheckInterval > 0 ? settings.ObsCheckInterval.ToString() : "5";
                    ObsIntervalPanel.Visibility = (AutoRestartObsCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

                    AutoUpdateCheck.IsChecked = settings.AutoUpdate;
                    EnsureMicMaxCheck.IsChecked = settings.EnsureMicMax;
                    SyncTimeCheck.IsChecked = settings.SyncTimeOnLaunch;

                    StartMenuShortcutCheck.IsChecked = settings.StartMenuShortcut;
                    DesktopShortcutCheck.IsChecked = settings.DesktopShortcut;

                    EnableLoggingCheck.IsChecked = settings.EnableLogging;
                    MaxLogLinesInput.Text = settings.MaxLogLines > 0 ? settings.MaxLogLines.ToString() : "1000";
                    LogLinesPanel.Visibility = (EnableLoggingCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

                    isConnectPoolEnabled = settings.ConnectPoolActivity;
                    isConnectVCEnabled = settings.ConnectVCActivity;
                    isDisconnectPoolEnabled = settings.DisconnectPoolActivity;
                    isDisconnectVCEnabled = settings.DisconnectVCActivity;

                    UpdateActivityButton(BtnConnectPool, isConnectPoolEnabled, "Pool");
                    UpdateActivityButton(BtnConnectVC, isConnectVCEnabled, "VC");
                    UpdateActivityButton(BtnDisconnectPool, isDisconnectPoolEnabled, "Pool");
                    UpdateActivityButton(BtnDisconnectVC, isDisconnectVCEnabled, "VC");

                    ToggleObsWatchdog();
                    LoadLogFile();

                    if (settings.ClipKeys != null && settings.ClipKeys.Count > 0)
                    {
                        ClipKeysList = settings.ClipKeys;
                    }
                    else if (ClipKeysList.Count == 0)
                    {
                        ClipKeysList.Add("LeftAlt");
                        ClipKeysList.Add("F10");
                    }

                    EnableSoundCheck.IsChecked = settings.EnableSound;
                    RadioCustomSound.IsChecked = settings.UseCustomSound;
                    RadioSystemSound.IsChecked = !settings.UseCustomSound;
                    CustomSoundPathInput.Text = settings.CustomSoundFilename;
                    ClipVolumeSlider.Value = settings.ClipVolume;

                    foreach (ComboBoxItem item in SystemSoundCombo.Items)
                    {
                        if (item.Content.ToString() == settings.SystemSoundType)
                        {
                            SystemSoundCombo.SelectedItem = item;
                            break;
                        }
                    }

                    EnableConnectSoundCheck.IsChecked = settings.EnableConnectSound;
                    RadioCustomConnectSound.IsChecked = settings.UseCustomConnectSound;
                    RadioSystemConnectSound.IsChecked = !settings.UseCustomConnectSound;
                    CustomConnectSoundPathInput.Text = settings.CustomConnectSoundFilename;
                    ConnectVolumeSlider.Value = settings.ConnectVolume;

                    foreach (ComboBoxItem item in SystemConnectSoundCombo.Items)
                    {
                        if (item.Content.ToString() == settings.SystemConnectSoundType)
                        {
                            SystemConnectSoundCombo.SelectedItem = item;
                            break;
                        }
                    }

                    EnableDisconnectSoundCheck.IsChecked = settings.EnableDisconnectSound;
                    RadioCustomDisconnectSound.IsChecked = settings.UseCustomDisconnectSound;
                    RadioSystemDisconnectSound.IsChecked = !settings.UseCustomDisconnectSound;
                    CustomDisconnectSoundPathInput.Text = settings.CustomDisconnectSoundFilename;
                    DisconnectVolumeSlider.Value = settings.DisconnectVolume;

                    foreach (ComboBoxItem item in SystemDisconnectSoundCombo.Items)
                    {
                        if (item.Content.ToString() == settings.SystemDisconnectSoundType)
                        {
                            SystemDisconnectSoundCombo.SelectedItem = item;
                            break;
                        }
                    }

                    EnableClipNotifCheck.IsChecked = settings.EnableClipNotif;
                    EnableConnectNotifCheck.IsChecked = settings.EnableConnectNotif;

                    if (settings.ClipNotifMonitorIndex >= 0 && settings.ClipNotifMonitorIndex < ClipNotifMonitorCombo.Items.Count)
                        ClipNotifMonitorCombo.SelectedIndex = settings.ClipNotifMonitorIndex;
                    else if (ClipNotifMonitorCombo.Items.Count > 0)
                        ClipNotifMonitorCombo.SelectedIndex = 0;

                    if (settings.ConnectNotifMonitorIndex >= 0 && settings.ConnectNotifMonitorIndex < ConnectNotifMonitorCombo.Items.Count)
                        ConnectNotifMonitorCombo.SelectedIndex = settings.ConnectNotifMonitorIndex;
                    else if (ConnectNotifMonitorCombo.Items.Count > 0)
                        ConnectNotifMonitorCombo.SelectedIndex = 0;

                    foreach (ComboBoxItem item in ClipNotifLocationCombo.Items)
                        if (item.Content.ToString() == settings.ClipNotifLocation) { ClipNotifLocationCombo.SelectedItem = item; break; }

                    foreach (ComboBoxItem item in ConnectNotifLocationCombo.Items)
                        if (item.Content.ToString() == settings.ConnectNotifLocation) { ConnectNotifLocationCombo.SelectedItem = item; break; }

                    try { ClipNotifColorBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.ClipNotifColor)); } catch { }

                    ClipNotifFlipAccentCheck.IsChecked = settings.ClipNotifFlipAccent;
                    ConnectNotifFlipAccentCheck.IsChecked = settings.ConnectNotifFlipAccent;
                    ClipNotifTimeLimitInput.Text = settings.ClipNotifTimeLimit.ToString();
                    ConnectNotifTimeLimitInput.Text = settings.ConnectNotifTimeLimit.ToString();

                    if (ClipNotifSettingsPanel != null) ClipNotifSettingsPanel.Visibility = settings.EnableClipNotif ? Visibility.Visible : Visibility.Collapsed;
                    if (ConnectNotifSettingsPanel != null) ConnectNotifSettingsPanel.Visibility = settings.EnableConnectNotif ? Visibility.Visible : Visibility.Collapsed;

                    CustomServerCheck.IsChecked = settings.UseCustomServer;
                    CustomServerInput.Text = settings.CustomServerUrl;
                    if (CustomServerPanel != null) CustomServerPanel.Visibility = settings.UseCustomServer ? Visibility.Visible : Visibility.Collapsed;

                    ApprovedChannels.Clear();
                    foreach (var c in settings.Channels) ApprovedChannels.Add(new DiscordItem { Id = c, DisplayName = c });

                    ApprovedUsers.Clear();
                    foreach (var u in settings.Users) ApprovedUsers.Add(new DiscordItem { Id = u, DisplayName = u });
                }
            }
            catch { }

            if (IsKeybindCollision(TriggerKeyInput.Text, ClipKeysList))
            {
                TriggerKeyInput.Text = "";
                WriteLog("Trigger keybind wiped on startup due to collision with Software Clip keybind.");
            }

            if (IsKeybindCollision(LocalTriggerKeyInput.Text, ClipKeysList))
            {
                LocalTriggerKeyInput.Text = "";
                WriteLog("Local Trigger keybind wiped on startup due to collision with Software Clip keybind.");
            }

            RebuildClipKeyUI();

            if (string.IsNullOrEmpty(ObsLocationInput.Text))
            {
                Task.Run(() => GetObsPath());
            }

            EnsureDotFilesHidden(configFolder);
        }

        private void SaveSettings()
        {
            if (!isLoaded) return;

            if (IsKeybindCollision(TriggerKeyInput.Text, ClipKeysList))
            {
                TriggerKeyInput.Text = "";
            }
            if (IsKeybindCollision(LocalTriggerKeyInput.Text, ClipKeysList))
            {
                LocalTriggerKeyInput.Text = "";
            }

            if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);
            if (!Directory.Exists(appTokensFolder)) Directory.CreateDirectory(appTokensFolder);

            File.WriteAllText(System.IO.Path.Combine(appTokensFolder, "discord_id.json"), $"\"{DiscordIdInput.Text}\"");

            var settings = new AppSettings
            {
                SendClips = SendClipsCheck.IsChecked ?? true,
                ReceiveClips = ReceiveClipsCheck.IsChecked ?? true,
                AnyVCRule = RadioAnyVC.IsChecked ?? true,
                TriggerKey = TriggerKeyInput.Text,
                LocalTriggerKey = LocalTriggerKeyInput.Text,
                ControllerTriggerKey = ControllerTriggerKeyInput?.Text ?? "",
                ControllerLocalTriggerKey = ControllerLocalTriggerKeyInput?.Text ?? "",
                WheelTriggerKey = WheelTriggerKeyInput?.Text ?? "",
                WheelLocalTriggerKey = WheelLocalTriggerKeyInput?.Text ?? "",
                ClipKeys = ClipKeysList,

                WindowLeft = double.IsNaN(this.Left) ? -1 : this.Left,
                WindowTop = double.IsNaN(this.Top) ? -1 : this.Top,
                WindowWidth = double.IsNaN(this.Width) ? 780 : this.Width,
                WindowHeight = double.IsNaN(this.Height) ? 640 : this.Height,

                AutoSync = AutoSyncCheck.IsChecked ?? false,
                StartWithWindows = StartWithWindowsCheck.IsChecked ?? false,
                RateLimitSeconds = int.TryParse(RateLimitInput.Text, out int parsedLimit) && parsedLimit >= 0 ? parsedLimit : 10,
                ClipDelaySeconds = int.TryParse(ClipDelayInput.Text, out int parsedDelay) && parsedDelay >= 0 ? parsedDelay : 0,
                ObsPath = ObsLocationInput.Text,
                ClipPath = ClipLocationInput.Text,
                AutoRenameClips = AutoRenameClipsCheck.IsChecked ?? false,
                RenameGame = RenameGameCheck.IsChecked ?? true,
                RenamePerspective = RenamePerspectiveCheck.IsChecked ?? true,
                RenameClipper = RenameClipperCheck.IsChecked ?? true,
                RenameVC = RenameVCCheck.IsChecked ?? true,
                RenameServer = RenameServerCheck.IsChecked ?? true,
                RenamePool = RenamePoolCheck.IsChecked ?? true,
                AutoStartObs = AutoStartObsCheck.IsChecked ?? false,
                AutoRestartObs = AutoRestartObsCheck.IsChecked ?? false,
                ObsCheckInterval = int.TryParse(ObsIntervalInput.Text, out int parsedInterval) && parsedInterval > 0 ? parsedInterval : 5,

                AutoUpdate = AutoUpdateCheck.IsChecked ?? false,
                EnsureMicMax = EnsureMicMaxCheck.IsChecked ?? false,
                SyncTimeOnLaunch = SyncTimeCheck.IsChecked ?? false,
                StartMenuShortcut = StartMenuShortcutCheck.IsChecked ?? false,
                DesktopShortcut = DesktopShortcutCheck.IsChecked ?? false,

                EnableLogging = EnableLoggingCheck.IsChecked ?? true,
                MaxLogLines = int.TryParse(MaxLogLinesInput.Text, out int parsedMax) && parsedMax > 0 ? parsedMax : 1000,

                EnableSound = EnableSoundCheck.IsChecked ?? true,
                UseCustomSound = RadioCustomSound.IsChecked ?? false,
                SystemSoundType = (SystemSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "SimpleyViridian - Clipped",
                CustomSoundFilename = CustomSoundPathInput.Text,
                ClipVolume = ClipVolumeSlider.Value,

                EnableConnectSound = EnableConnectSoundCheck.IsChecked ?? true,
                UseCustomConnectSound = RadioCustomConnectSound.IsChecked ?? false,
                SystemConnectSoundType = (SystemConnectSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "ConfusedIndividual - Server Connect",
                CustomConnectSoundFilename = CustomConnectSoundPathInput.Text,
                ConnectVolume = ConnectVolumeSlider.Value,

                EnableDisconnectSound = EnableDisconnectSoundCheck.IsChecked ?? true,
                UseCustomDisconnectSound = RadioCustomDisconnectSound.IsChecked ?? false,
                SystemDisconnectSoundType = (SystemDisconnectSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "ConfusedIndividual - Server Disconnect",
                CustomDisconnectSoundFilename = CustomDisconnectSoundPathInput.Text,
                DisconnectVolume = DisconnectVolumeSlider.Value,

                EnableClipNotif = EnableClipNotifCheck.IsChecked ?? false,
                EnableConnectNotif = EnableConnectNotifCheck.IsChecked ?? false,
                ClipNotifLocation = (ClipNotifLocationCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Right",
                ConnectNotifLocation = (ConnectNotifLocationCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Right",
                ClipNotifColor = ClipNotifColorBox.Background is SolidColorBrush scb ? scb.Color.ToString() : "#5865F2",
                ClipNotifFlipAccent = ClipNotifFlipAccentCheck.IsChecked ?? false,
                ConnectNotifFlipAccent = ConnectNotifFlipAccentCheck.IsChecked ?? false,
                ClipNotifTimeLimit = double.TryParse(ClipNotifTimeLimitInput.Text, out double ctl) ? ctl : 3,
                ConnectNotifTimeLimit = double.TryParse(ConnectNotifTimeLimitInput.Text, out double cnlt) ? cnlt : 3,
                ClipNotifMonitorIndex = ClipNotifMonitorCombo.SelectedIndex >= 0 ? ClipNotifMonitorCombo.SelectedIndex : 0,
                ConnectNotifMonitorIndex = ConnectNotifMonitorCombo.SelectedIndex >= 0 ? ConnectNotifMonitorCombo.SelectedIndex : 0,

                UseCustomServer = CustomServerCheck.IsChecked ?? false,
                CustomServerUrl = CustomServerInput.Text,

                ConnectPoolActivity = isConnectPoolEnabled,
                ConnectVCActivity = isConnectVCEnabled,
                DisconnectPoolActivity = isDisconnectPoolEnabled,
                DisconnectVCActivity = isDisconnectVCEnabled,

                Channels = ApprovedChannels.Select(c => c.Id).ToList(),
                Users = ApprovedUsers.Select(u => u.Id).ToList()
            };

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFilePath, json);
        }

        private void LoadStats()
        {
            string statsFolder = System.IO.Path.Combine(configFolder, "statistics");
            if (!Directory.Exists(statsFolder)) Directory.CreateDirectory(statsFolder);

            string clipsFile = System.IO.Path.Combine(statsFolder, "clips.json");
            string sentFile = System.IO.Path.Combine(statsFolder, "users_sent.json");
            string receivedFile = System.IO.Path.Combine(statsFolder, "users_received.json");

            try { if (File.Exists(clipsFile)) totalClipsStats = JsonSerializer.Deserialize<TotalClipsStat>(File.ReadAllText(clipsFile)) ?? new TotalClipsStat(); } catch { }
            try { if (File.Exists(sentFile)) userSentStats = JsonSerializer.Deserialize<Dictionary<string, UserStatCount>>(File.ReadAllText(sentFile)) ?? new Dictionary<string, UserStatCount>(); } catch { }
            try { if (File.Exists(receivedFile)) userReceivedStats = JsonSerializer.Deserialize<Dictionary<string, UserStatCount>>(File.ReadAllText(receivedFile)) ?? new Dictionary<string, UserStatCount>(); } catch { }

            UpdateStatsUI();
        }

        private void SaveStats()
        {
            string statsFolder = System.IO.Path.Combine(configFolder, "statistics");
            if (!Directory.Exists(statsFolder)) Directory.CreateDirectory(statsFolder);

            File.WriteAllText(System.IO.Path.Combine(statsFolder, "clips.json"), JsonSerializer.Serialize(totalClipsStats, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(System.IO.Path.Combine(statsFolder, "users_sent.json"), JsonSerializer.Serialize(userSentStats, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(System.IO.Path.Combine(statsFolder, "users_received.json"), JsonSerializer.Serialize(userReceivedStats, new JsonSerializerOptions { WriteIndented = true }));

            UpdateStatsUI();
        }

        private void UpdateStatsUI()
        {
            Dispatcher.Invoke(() =>
            {
                StatsTotalSentText.Text = $"Total Clips Sent: {totalClipsStats.Sent}";
                StatsTotalReceivedText.Text = $"Total Clips Received: {totalClipsStats.Received}";

                StatsSentList.Items.Clear();
                foreach (var stat in userSentStats.Values.OrderByDescending(s => s.Count))
                {
                    StatsSentList.Items.Add($"{stat.Count}  -  {stat.Name}");
                }

                StatsReceivedList.Items.Clear();
                foreach (var stat in userReceivedStats.Values.OrderByDescending(s => s.Count))
                {
                    StatsReceivedList.Items.Add($"{stat.Count}  -  {stat.Name}");
                }
            });
        }

        private void CheckAndUpdateVersion()
        {
            string statsFolder = System.IO.Path.Combine(configFolder, "statistics");
            if (!Directory.Exists(statsFolder)) Directory.CreateDirectory(statsFolder);
            string versionFile = System.IO.Path.Combine(statsFolder, "version.json");

            AppVersionStat currentStat = new AppVersionStat();
            if (File.Exists(versionFile))
            {
                try { currentStat = JsonSerializer.Deserialize<AppVersionStat>(File.ReadAllText(versionFile)) ?? new AppVersionStat(); }
                catch { }
            }

            Version storedVersion = GetVersion(currentStat.Version);
            Version appVersion = GetVersion(AppVersion);



            // ==========================================
            // APP MIGRATION :)
            // ==========================================


            // --- Migration: v0.1.7  ---
            Version v017 = new Version(0, 1, 7);
            if (storedVersion < v017)
            {
                try
                {
                    string clipMgmtFolder = System.IO.Path.Combine(configFolder, "clip-management");
                    if (Directory.Exists(clipMgmtFolder))
                    {
                        Directory.Delete(clipMgmtFolder, true);
                        WriteLog("Migration v0.1.7: Deleted legacy clip-management folder.");
                    }

                    if (Directory.Exists(soundsFolder))
                    {
                        Directory.Delete(soundsFolder, true);
                        WriteLog("Migration v0.1.7: Deleted legacy sounds folder.");
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"Migration v0.1.7 folder cleanup failed: {ex.Message}");
                }

                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (exePath.EndsWith(".app.exe", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog("Migration v0.1.7: Detected app running as old ClippingTools.app.exe. Renaming to ClippingTools.exe...");
                    string targetExe = exePath.Replace(".app.exe", ".exe");
                    string batPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClippingToolsRename.cmd");

                    string batContent = $@"@echo off
timeout /t 4 /nobreak >nul
taskkill /f /im ""{System.IO.Path.GetFileName(exePath)}"" >nul 2>&1
move /y ""{exePath}"" ""{targetExe}""
start """" ""{targetExe}""
(goto) 2>nul & del ""%~f0""";

                    File.WriteAllText(batPath, batContent);
                    Process.Start(new ProcessStartInfo { FileName = batPath, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });

                    Environment.Exit(0);
                    return;
                }
                else
                {
                    UpdateShortcuts(exePath);

                    needsRenamerUpdate = true;
                    WriteLog("Migration v0.1.7: Flagged ClipRenamer for re-extraction.");

                    currentStat.Version = "v0.1.7";
                    storedVersion = v017;
                    WriteVersionFile(currentStat);
                }
            }



            // --- Future Migrations Go Here ---





            currentStat.Version = AppVersion;
            WriteVersionFile(currentStat);
        }

        private void UpdateShortcuts(string newExePath)
        {
            VerifyShortcuts();
            WriteLog("Verified and migrated shortcuts.");
        }

        private void VerifyShortcuts()
        {
            string exePath = Environment.ProcessPath;
            bool wantStartMenu = StartMenuShortcutCheck.IsChecked == true;
            bool wantDesktop = DesktopShortcutCheck.IsChecked == true;

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                string startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string startShortcutPath = System.IO.Path.Combine(startMenuPath, "Clipping Tools.lnk");

                if (wantStartMenu)
                {
                    dynamic shortcut = shell.CreateShortcut(startShortcutPath);
                    shortcut.TargetPath = exePath;
                    shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                    shortcut.Save();
                }
                else if (File.Exists(startShortcutPath))
                {
                    File.Delete(startShortcutPath);
                }

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopShortcutPath = System.IO.Path.Combine(desktopPath, "Clipping Tools.lnk");

                if (wantDesktop)
                {
                    dynamic shortcut = shell.CreateShortcut(desktopShortcutPath);
                    shortcut.TargetPath = exePath;
                    shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                    shortcut.Save();
                }
                else if (File.Exists(desktopShortcutPath))
                {
                    File.Delete(desktopShortcutPath);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to manage shortcuts: {ex.Message}");
            }
        }

        private void ShortcutCheck_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            VerifyShortcuts();
        }

        private Version GetVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return new Version(0, 0, 0, 0);
            v = v.TrimStart('v', 'V', '≤', '<');
            if (Version.TryParse(v, out Version ver)) return ver;
            return new Version(0, 0, 0, 0);
        }

        private void WriteVersionFile(AppVersionStat stat = null)
        {
            try
            {
                string statsFolder = System.IO.Path.Combine(configFolder, "statistics");
                if (!Directory.Exists(statsFolder)) Directory.CreateDirectory(statsFolder);
                string versionFile = System.IO.Path.Combine(statsFolder, "version.json");

                if (stat == null)
                {
                    stat = new AppVersionStat { Version = AppVersion, LastUpdated = DateTime.Now };
                    if (File.Exists(versionFile))
                    {
                        try
                        {
                            var existing = JsonSerializer.Deserialize<AppVersionStat>(File.ReadAllText(versionFile));
                            if (existing != null) stat.Version = existing.Version;
                        }
                        catch { }
                    }
                }
                else
                {
                    stat.LastUpdated = DateTime.Now;
                }

                File.WriteAllText(versionFile, JsonSerializer.Serialize(stat, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void VolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider slider && slider.IsMouseCaptured)
            {
                slider.ReleaseMouseCapture();
            }
        }

        private void VolumeSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Slider slider && e.LeftButton == MouseButtonState.Pressed)
            {
                if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb ||
                    e.OriginalSource is System.Windows.Shapes.Ellipse ||
                    Mouse.Captured is System.Windows.Controls.Primitives.Thumb)
                {
                    return;
                }

                if (!slider.IsMouseCaptured)
                {
                    slider.CaptureMouse();
                }

                if (slider.ActualWidth > 0)
                {
                    Point p = e.GetPosition(slider);
                    double ratio = p.X / slider.ActualWidth;

                    if (slider.IsDirectionReversed)
                        ratio = 1.0 - ratio;

                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, slider.Minimum + (ratio * (slider.Maximum - slider.Minimum))));
                }
            }
        }

        private async void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (sender == CustomServerCheck && isSyncActive)
            {
                CustomServerCheck.IsChecked = !(CustomServerCheck.IsChecked ?? false);
                await ShowCustomDialog("Sync Active", "You cannot change the server destination while actively syncing. Please disconnect first.");
                return;
            }

            if (sender == AutoRenameClipsCheck && AutoRenameClipsCheck.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ClipLocationInput.Text) || ClipLocationInput.Text == "Waiting for selection...")
                {
                    await ShowCustomDialog("Missing Location", "Please set your Clip Recording Location first before enabling the Clip Renaming feature.");
                    AutoRenameClipsCheck.IsChecked = false;
                    return;
                }
            }

            if (sender == AutoStartObsCheck && AutoStartObsCheck.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ObsLocationInput.Text) || ObsLocationInput.Text == "Waiting for selection...")
                {
                    await ShowCustomDialog("Missing OBS Location", "Please set your OBS Location first before enabling the OBS Auto Start feature.");
                    AutoStartObsCheck.IsChecked = false;
                    return;
                }
            }

            if (RenamerOptionsPanel != null)
            {
                RenamerOptionsPanel.Visibility = (AutoRenameClipsCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ClipNotifSettingsPanel != null) ClipNotifSettingsPanel.Visibility = EnableClipNotifCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (ConnectNotifSettingsPanel != null) ConnectNotifSettingsPanel.Visibility = EnableConnectNotifCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (CustomServerPanel != null) CustomServerPanel.Visibility = CustomServerCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (isLoaded)
            {
                if (sender == EnableClipNotifCheck && EnableClipNotifCheck.IsChecked == true)
                {
                    ShowNotification("Notification Enabled", "Example Notification", "ExampleClip");
                }
                else if (sender == EnableConnectNotifCheck && EnableConnectNotifCheck.IsChecked == true)
                {
                    ShowNotification("Notification Enabled", "Example Notification", "ExampleConnect");
                }
            }

            SaveSettings();
            if (sender == AutoRenameClipsCheck) ToggleRenamerService();
            if (sender == SyncTimeCheck && SyncTimeCheck.IsChecked == true && isLoaded) EnforceTimeSync();
        }

        private void ClipNotifColorBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var colorDialog = new System.Windows.Forms.ColorDialog();
            if (ClipNotifColorBox.Background is SolidColorBrush scb)
            {
                colorDialog.Color = System.Drawing.Color.FromArgb(scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);
            }

            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var newColor = Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B);
                ClipNotifColorBox.Background = new SolidColorBrush(newColor);
                SaveSettings();
            }
        }
        private void Setting_TextChanged(object sender, TextChangedEventArgs e) { SaveSettings(); }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
        }

        private async void Window_LayoutChanged(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            windowSaveCts?.Cancel();
            windowSaveCts = new CancellationTokenSource();

            try
            {
                await Task.Delay(500, windowSaveCts.Token);
                SaveSettings();
            }
            catch (TaskCanceledException) { }
        }

        private CancellationTokenSource micWatcherCts;

        private async Task WatchMicVolume(CancellationToken token)
        {
            IMMDeviceEnumerator enumerator = null;
            try { enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator(); } catch { return; }
            Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool shouldEnforce = false;
                    Dispatcher.Invoke(() => shouldEnforce = EnsureMicMaxCheck.IsChecked == true);

                    if (shouldEnforce)
                    {
                        if (enumerator.GetDefaultAudioEndpoint(1, 1, out IMMDevice mic) == 0 && mic != null)
                        {
                            object endpointVolumeObj;
                            if (mic.Activate(ref IID_IAudioEndpointVolume, 23, IntPtr.Zero, out endpointVolumeObj) == 0)
                            {
                                IAudioEndpointVolume endpointVolume = (IAudioEndpointVolume)endpointVolumeObj;
                                if (endpointVolume.GetMasterVolumeLevelScalar(out float volume) == 0)
                                {
                                    if (volume < 1.0f)
                                    {
                                        endpointVolume.SetMasterVolumeLevelScalar(1.0f, Guid.Empty);
                                    }
                                }
                                Marshal.ReleaseComObject(endpointVolume);
                            }
                            Marshal.ReleaseComObject(mic);
                        }
                    }
                }
                catch { }

                await Task.Delay(2000, token);
            }
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        private void EnforceTimeSync()
        {
            if (SyncTimeCheck.IsChecked != true) return;

            Task.Run(() =>
            {
                try
                {
                    WriteLog("Attempting to synchronize Windows time...");
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"sc config w32time start= auto & net start w32time & w32tm /resync\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (Process process = Process.Start(psi))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        string errorOutput = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0 || output.Contains("The command completed successfully"))
                        {
                            WriteLog("Windows time successfully synchronized.");
                        }
                        else
                        {
                            string combinedOutput = string.IsNullOrWhiteSpace(errorOutput) ? output : errorOutput;
                            WriteLog($"Windows time synchronization failed: {combinedOutput.Trim()}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"Time sync error: {ex.Message}");
                }
            });
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!forceExit)
            {
                e.Cancel = true;
                this.Hide();
                SaveSettings();
            }
            else
            {
                WriteVersionFile();
                controllerCts?.Cancel();
                wheelCts?.Cancel();
                micWatcherCts?.Cancel();
                string renamerFolder = System.IO.Path.Combine(configFolder, "clip-management", "clip-renamer");
                string triggerPath = System.IO.Path.Combine(renamerFolder, "exit_trigger.txt");
                if (File.Exists(triggerPath)) File.WriteAllText(triggerPath, "EXIT");

                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                SaveSettings();
            }
        }

        private void StartWithWindowsCheck_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            try
            {
                string exePath = Environment.ProcessPath;
                bool enable = StartWithWindowsCheck.IsChecked ?? false;

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                if (enable)
                {
                    psi.Arguments = $"/create /tn \"ClippingTools\" /tr \"\\\"{exePath}\\\" --minimized\" /sc onlogon /rl highest /f";
                }
                else
                {
                    psi.Arguments = $"/delete /tn \"ClippingTools\" /f";
                }

                Process.Start(psi);
            }
            catch
            {
                StartWithWindowsCheck.IsChecked = !StartWithWindowsCheck.IsChecked;
                SaveSettings();
            }
        }

        private void VerifyStartWithWindowsTask()
        {
            if (StartWithWindowsCheck.IsChecked != true) return;

            string exePath = Environment.ProcessPath;
            bool needsUpdate = true;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/query /tn \"ClippingTools\" /v /fo list",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0 && output.Contains(exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        needsUpdate = false;
                    }
                }
            }
            catch { }

            if (needsUpdate)
            {
                StartWithWindowsCheck_Click(null, null);
                WriteLog("Start with Windows task path was incorrect or missing. Automatically updated to current executable path.");
            }
        }

        // ==============================================================================
        // IMPORT, EXPORT, AND UUID MANAGEMENT
        // ==============================================================================

        private async void ImportSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSyncActive)
            {
                await ShowCustomDialog("Sync Active", "You cannot import settings while actively syncing. Please disconnect first.");
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "JSON Files (*.json)|*.json";

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(openFileDialog.FileName);
                    var testSettings = JsonSerializer.Deserialize<AppSettings>(json);

                    if (testSettings == null)
                    {
                        await ShowCustomDialog("Import Failed", "Invalid settings file format.");
                        return;
                    }

                    File.Copy(openFileDialog.FileName, configFilePath, true);
                    LoadSettings();
                    WriteLog("Successfully imported new settings from file.");
                    await ShowCustomDialog("Success", "Settings imported successfully!");
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Import Error", "Failed to read the settings file: " + ex.Message);
                }
            }
        }

        private async void ExportSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            bool result = await ShowCustomDialog(
                "Confirm Export",
                "Do you want to proceed with exporting your current settings?",
                true);

            if (result)
            {
                SaveSettings();

                SaveFileDialog saveFileDialog = new SaveFileDialog();
                saveFileDialog.Filter = "JSON Files (*.json)|*.json";
                saveFileDialog.FileName = "ClippingTools_Settings.json";

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        File.Copy(configFilePath, saveFileDialog.FileName, true);
                        await ShowCustomDialog("Success", "Settings exported successfully!");
                    }
                    catch (Exception ex)
                    {
                        await ShowCustomDialog("Export Error", "Failed to export settings: " + ex.Message);
                    }
                }
            }
        }

        private async void ResetSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSyncActive)
            {
                await ShowCustomDialog("Sync Active", "You cannot reset settings while actively syncing. Please disconnect first.");
                return;
            }

            bool result = await ShowCustomDialog(
                "Confirm Reset",
                "Are you sure you want to reset all settings to their default values? This cannot be undone.",
                true);

            if (result)
            {
                try
                {
                    var defaultSettings = new AppSettings();
                    string json = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(configFilePath, json);

                    ClipKeysList.Clear();
                    LoadSettings();
                    await ShowCustomDialog("Success", "Settings have been reset to default.");
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Error", "Failed to reset settings: " + ex.Message);
                }
            }
        }

        private async void ResetUuidBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSyncActive)
            {
                await ShowCustomDialog("Sync Active", "You cannot reset your UUID while actively syncing. Please disconnect first.");
                return;
            }

            List<Tuple<string, string>> availableServers = new List<Tuple<string, string>>();
            if (Directory.Exists(appTokensFolder) && File.Exists(System.IO.Path.Combine(appTokensFolder, "app_id.json")))
            {
                availableServers.Add(new Tuple<string, string>("Central Server", appTokensFolder));
            }

            if (Directory.Exists(customTokensFolder))
            {
                foreach (var dir in Directory.GetDirectories(customTokensFolder))
                {
                    if (File.Exists(System.IO.Path.Combine(dir, "app_id.json")))
                    {
                        availableServers.Add(new Tuple<string, string>($"{new DirectoryInfo(dir).Name}", dir));
                    }
                }
            }

            if (availableServers.Count > 1)
            {
                if (!(this.Content is Grid rootGrid)) return;

                Grid overlay = new Grid
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0))
                };
                Panel.SetZIndex(overlay, 9999);
                Grid.SetColumnSpan(overlay, 99);
                Grid.SetRowSpan(overlay, 99);

                Border roundedBorder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#36393f")),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = 350,
                    Padding = new Thickness(15)
                };

                StackPanel sp = new StackPanel();

                TextBlock title = new TextBlock
                {
                    Text = "Select which server's UUID you want to reset:",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(0, 0, 0, 15),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                };
                sp.Children.Add(title);

                ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 250 };
                StackPanel btnPanel = new StackPanel();

                foreach (var srv in availableServers)
                {
                    var currentSrv = srv;
                    Button btn = new Button
                    {
                        Content = currentSrv.Item1,
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(10),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c")),
                        Foreground = Brushes.White,
                        Cursor = Cursors.Hand,
                        BorderThickness = new Thickness(0)
                    };
                    btn.Click += async (s, args) =>
                    {
                        rootGrid.Children.Remove(overlay);
                        await PerformUuidReset(currentSrv.Item2, currentSrv.Item1);
                    };
                    btnPanel.Children.Add(btn);
                }

                sv.Content = btnPanel;
                sp.Children.Add(sv);

                Button cancelBtn = new Button
                {
                    Content = "Cancel",
                    Margin = new Thickness(0, 10, 0, 0),
                    Padding = new Thickness(10),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f04747")),
                    Foreground = Brushes.White,
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(0)
                };
                cancelBtn.Click += (s, args) =>
                {
                    rootGrid.Children.Remove(overlay);
                };
                sp.Children.Add(cancelBtn);

                roundedBorder.Child = sp;
                overlay.Children.Add(roundedBorder);
                rootGrid.Children.Add(overlay);
            }
            else
            {
                bool isCustom = CustomServerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CustomServerInput.Text);
                string host = "unknown_host";
                if (isCustom && Uri.TryCreate(CustomServerInput.Text.Trim(), UriKind.Absolute, out Uri uri)) host = uri.Host;
                string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
                await PerformUuidReset(targetFolder, "the network");
            }
        }

        private async Task PerformUuidReset(string targetFolder, string serverName)
        {
            bool result = await ShowCustomDialog(
                "Confirm Reset",
                $"Are you sure you want to reset your App UUID for {serverName}? This will unlink your app from the network. You will need to reverify it via Discord DM on your next connection.",
                true);

            if (result)
            {
                string appIdPath = System.IO.Path.Combine(targetFolder, "app_id.json");

                if (File.Exists(appIdPath)) File.Delete(appIdPath);

                if (targetFolder == appTokensFolder)
                {
                    appUuid = "";
                }

                WriteLog($"App UUID reset for {serverName}.");
                await ShowCustomDialog("UUID Reset", $"UUID has been reset for {serverName}. Please re-authenticate the next time you connect.");
            }
        }

        // ==============================================================================
        // DISCORD OAUTH2 SIGN-IN LOGIC
        // ==============================================================================

        private async void DiscordSignInBtn_Click(object sender, RoutedEventArgs e)
        {
            bool autoConnect = sender == null;
            DiscordSignInBtn.Content = "Awaiting Browser...";
            DiscordSignInBtn.IsEnabled = false;

            _ = Task.Run(async () =>
            {
                await Task.Delay(10000);
                Dispatcher.Invoke(() =>
                {
                    if (DiscordSignInBtn.Content.ToString() == "Awaiting Browser...")
                    {
                        ResetDiscordButton();
                    }
                });
            });

            string state = Guid.NewGuid().ToString("N");
            string serverUrl = GetServerUrl();

            if (CustomServerCheck.IsChecked == true && !Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            {
                await ShowCustomDialog("Invalid Server URL", "Please enter a valid custom server URL starting with ws:// or wss://");
                ResetDiscordButton();
                return;
            }

            await ListenForAuthWebSocket(state, serverUrl, autoConnect);
        }

        private async Task ListenForAuthWebSocket(string state, string serverUrl, bool autoConnect)
        {
            using (ClientWebSocket authSocket = new ClientWebSocket())
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                try
                {
                    await authSocket.ConnectAsync(new Uri(serverUrl), cts.Token);
                    string initMsg = JsonSerializer.Serialize(new { action = "auth_listen", state = state });
                    await authSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(initMsg)), WebSocketMessageType.Text, true, cts.Token);

                    var buffer = new byte[8192];
                    while (authSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        using (var ms = new MemoryStream())
                        {
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await authSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                                if (result.MessageType == WebSocketMessageType.Close) break;
                                ms.Write(buffer, 0, result.Count);
                            } while (!result.EndOfMessage);

                            if (result.MessageType == WebSocketMessageType.Close) break;

                            string message = Encoding.UTF8.GetString(ms.ToArray());
                            using (JsonDocument doc = JsonDocument.Parse(message))
                            {
                                string action = doc.RootElement.GetProperty("action").GetString();

                                if (action == "auth_url")
                                {
                                    string url = doc.RootElement.GetProperty("url").GetString();
                                    Dispatcher.Invoke(() => {
                                        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                                    });
                                }
                                else if (action == "auth_success")
                                {
                                    string discordId = doc.RootElement.GetProperty("discord_id").GetString();
                                    string tokenId = doc.RootElement.GetProperty("token_id").GetString();

                                    Dispatcher.Invoke(() => {
                                        DiscordIdInput.Text = discordId;

                                        bool isCustom = CustomServerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CustomServerInput.Text);
                                        string host = "unknown_host";
                                        if (isCustom && Uri.TryCreate(CustomServerInput.Text.Trim(), UriKind.Absolute, out Uri uri)) host = uri.Host;
                                        string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;

                                        if (!Directory.Exists(tokensFolder)) Directory.CreateDirectory(tokensFolder);
                                        if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                                        string discordIdPath = System.IO.Path.Combine(targetFolder, "discord_id.json");
                                        string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");

                                        if (File.Exists(discordIdPath)) { new FileInfo(discordIdPath).Attributes &= ~FileAttributes.Hidden; }
                                        File.WriteAllText(discordIdPath, $"\"{discordId}\"");

                                        if (File.Exists(tokenPath)) { new FileInfo(tokenPath).Attributes &= ~FileAttributes.Hidden; }
                                        File.WriteAllText(tokenPath, $"\"{tokenId}\"");

                                        string appIdPath = System.IO.Path.Combine(targetFolder, "app_id.json");
                                        if (!File.Exists(appIdPath)) File.WriteAllText(appIdPath, $"\"{Guid.NewGuid().ToString()}\"");
                                        if (!isCustom) appUuid = File.ReadAllText(appIdPath).Trim('"', ' ', '\n', '\r');

                                        EnsureDotFilesHidden(configFolder);
                                        SaveSettings();
                                        ResetDiscordButton();
                                        WriteLog($"Successfully authenticated via server. ID: {discordId}");

                                        if (autoConnect && ConnectButton.Content.ToString() != "Listening...")
                                        {
                                            StartListening();
                                        }
                                    });
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(async () => {
                        await ShowCustomDialog("Auth Error", "Failed to connect to the authentication server: " + ex.Message);
                    });
                }
                finally
                {
                    Dispatcher.Invoke(() => ResetDiscordButton());
                }
            }
        }

        private void ResetDiscordButton()
        {
            DiscordSignInBtn.Content = "Sign In w/ Discord";
            DiscordSignInBtn.IsEnabled = true;
        }

        // ==============================================================================
        // AUDIO AND BROWSE LOGIC
        // ==============================================================================

        private void SystemSoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoaded)
            {
                RadioSystemSound.IsChecked = true;
                SaveSettings();
                PlayAlertSound(forcePlay: true);
            }
        }

        private void BrowseSoundBtn_Click(object sender, RoutedEventArgs e)
        {
            RadioCustomSound.IsChecked = true;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3";

            if (openFileDialog.ShowDialog() == true)
            {
                if (!Directory.Exists(customSoundsFolder)) Directory.CreateDirectory(customSoundsFolder);

                if (!string.IsNullOrEmpty(CustomSoundPathInput.Text))
                {
                    string oldPathRoot = System.IO.Path.Combine(soundsFolder, CustomSoundPathInput.Text);
                    string oldPathCustom = System.IO.Path.Combine(customSoundsFolder, CustomSoundPathInput.Text);
                    if (File.Exists(oldPathRoot)) { try { File.Delete(oldPathRoot); } catch { } }
                    if (File.Exists(oldPathCustom)) { try { File.Delete(oldPathCustom); } catch { } }
                }

                string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                string destPath = System.IO.Path.Combine(customSoundsFolder, fileName);
                File.Copy(openFileDialog.FileName, destPath, true);
                CustomSoundPathInput.Text = fileName;
                SaveSettings();
                PlayAlertSound(forcePlay: true);
            }
        }

        private void TestSoundBtn_Click(object sender, RoutedEventArgs e) { PlayAlertSound(forcePlay: true); }

        private async void PlayAlertSound(bool forcePlay = false)
        {
            if (!forcePlay && EnableSoundCheck.IsChecked != true) return;

            customAudioPlayer.Volume = ClipVolumeSlider.Value;

            if (RadioCustomSound.IsChecked == true && !string.IsNullOrEmpty(CustomSoundPathInput.Text))
            {
                string soundPath = System.IO.Path.Combine(customSoundsFolder, CustomSoundPathInput.Text);
                if (!File.Exists(soundPath)) soundPath = System.IO.Path.Combine(soundsFolder, CustomSoundPathInput.Text);
                if (File.Exists(soundPath))
                {
                    customAudioPlayer.Open(new Uri(soundPath));
                    customAudioPlayer.Play();
                }
                else if (forcePlay)
                {
                    await ShowCustomDialog("File Missing", "Custom audio file not found. Please browse for it again.");
                }
            }
            else
            {
                string sysSound = (SystemSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                switch (sysSound)
                {
                    case "SimpleyViridian - Clipped":
                        PlayIncludedSound("simpleyviridian-clipped.wav");
                        break;
                    case "SimpleyViridian - Clipped 2.0":
                        PlayIncludedSound("simpleyviridian-clipped2.wav");
                        break;
                    case "SimpleyViridian - Clipped Teto":
                        PlayIncludedSound("simpleyviridian-clippedteto.wav");
                        break;
                    case "ConfusedIndividual - Clipped":
                        PlayIncludedSound("confusedindividual-clipped.mp3");
                        break;
                    case "ConfusedIndividual - Clipped!":
                        PlayIncludedSound("confusedindividual-clipped!.wav");
                        break;
                    case "Windows - Hand": System.Media.SystemSounds.Hand.Play(); break;
                    case "Windows - Exclamation": System.Media.SystemSounds.Exclamation.Play(); break;
                }
            }
        }

        private void PlayIncludedSound(string fileName)
        {
            string path = System.IO.Path.Combine(soundsFolder, fileName);
            if (File.Exists(path))
            {
                customAudioPlayer.Open(new Uri(path));
                customAudioPlayer.Play();
            }
        }

        private void PlaySystemSound(string fileName)
        {
            string path = System.IO.Path.Combine(systemsSoundsFolder, fileName);
            if (File.Exists(path))
            {
                customAudioPlayer.Open(new Uri(path));
                customAudioPlayer.Play();
            }
        }

        private void SystemConnectSoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoaded)
            {
                RadioSystemConnectSound.IsChecked = true;
                SaveSettings();
                PlayConnectSound(forcePlay: true);
            }
        }

        private void BrowseConnectSoundBtn_Click(object sender, RoutedEventArgs e)
        {
            RadioCustomConnectSound.IsChecked = true;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3";

            if (openFileDialog.ShowDialog() == true)
            {
                if (!Directory.Exists(customSoundsFolder)) Directory.CreateDirectory(customSoundsFolder);

                if (!string.IsNullOrEmpty(CustomConnectSoundPathInput.Text))
                {
                    string oldPathSystems = System.IO.Path.Combine(systemsSoundsFolder, CustomConnectSoundPathInput.Text);
                    string oldPathCustom = System.IO.Path.Combine(customSoundsFolder, CustomConnectSoundPathInput.Text);
                    if (File.Exists(oldPathSystems)) { try { File.Delete(oldPathSystems); } catch { } }
                    if (File.Exists(oldPathCustom)) { try { File.Delete(oldPathCustom); } catch { } }
                }

                string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                string destPath = System.IO.Path.Combine(customSoundsFolder, fileName);
                File.Copy(openFileDialog.FileName, destPath, true);
                CustomConnectSoundPathInput.Text = fileName;
                SaveSettings();
                PlayConnectSound(forcePlay: true);
            }
        }

        private void TestConnectSoundBtn_Click(object sender, RoutedEventArgs e) { PlayConnectSound(forcePlay: true); }

        private async void PlayConnectSound(bool forcePlay = false, bool isActivity = false)
        {
            if (!forcePlay && !isActivity && EnableConnectSoundCheck.IsChecked != true) return;

            customAudioPlayer.Volume = ConnectVolumeSlider.Value;

            if (RadioCustomConnectSound.IsChecked == true && !string.IsNullOrEmpty(CustomConnectSoundPathInput.Text))
            {
                string soundPath = System.IO.Path.Combine(customSoundsFolder, CustomConnectSoundPathInput.Text);
                if (!File.Exists(soundPath)) soundPath = System.IO.Path.Combine(systemsSoundsFolder, CustomConnectSoundPathInput.Text);

                if (File.Exists(soundPath))
                {
                    customAudioPlayer.Open(new Uri(soundPath));
                    customAudioPlayer.Play();
                }
                else if (forcePlay && !isActivity)
                {
                    await ShowCustomDialog("File Missing", "Custom audio file not found. Please browse for it again.");
                }
            }
            else
            {
                string sysSound = (SystemConnectSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                switch (sysSound)
                {
                    case "ConfusedIndividual - Server Connect":
                        PlaySystemSound("confusedindividual-server connect.wav");
                        break;
                    case "SimpleyViridian - Connected":
                        PlaySystemSound("simpleyviridian-connected.wav");
                        break;
                    case "SimpleyViridian - Connected 2.0":
                        PlaySystemSound("simpleyviridian-connected2.wav");
                        break;
                }
            }
        }

        private void SystemDisconnectSoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoaded)
            {
                RadioSystemDisconnectSound.IsChecked = true;
                SaveSettings();
                PlayDisconnectSound(forcePlay: true);
            }
        }

        private void BrowseDisconnectSoundBtn_Click(object sender, RoutedEventArgs e)
        {
            RadioCustomDisconnectSound.IsChecked = true;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3";

            if (openFileDialog.ShowDialog() == true)
            {
                if (!Directory.Exists(customSoundsFolder)) Directory.CreateDirectory(customSoundsFolder);

                if (!string.IsNullOrEmpty(CustomDisconnectSoundPathInput.Text))
                {
                    string oldPathSystems = System.IO.Path.Combine(systemsSoundsFolder, CustomDisconnectSoundPathInput.Text);
                    string oldPathCustom = System.IO.Path.Combine(customSoundsFolder, CustomDisconnectSoundPathInput.Text);
                    if (File.Exists(oldPathSystems)) { try { File.Delete(oldPathSystems); } catch { } }
                    if (File.Exists(oldPathCustom)) { try { File.Delete(oldPathCustom); } catch { } }
                }

                string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                string destPath = System.IO.Path.Combine(customSoundsFolder, fileName);
                File.Copy(openFileDialog.FileName, destPath, true);
                CustomDisconnectSoundPathInput.Text = fileName;
                SaveSettings();
                PlayDisconnectSound(forcePlay: true);
            }
        }

        private void TestDisconnectSoundBtn_Click(object sender, RoutedEventArgs e) { PlayDisconnectSound(forcePlay: true); }

        private void UpdateActivityButton(Button btn, bool isEnabled, string baseText)
        {
            if (btn == null) return;
            btn.Content = $"{baseText}: {(isEnabled ? "ON" : "OFF")}";
            btn.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isEnabled ? "#457b54" : "#8a3f3f"));
        }

        private void BtnConnectPool_Click(object sender, RoutedEventArgs e)
        {
            isConnectPoolEnabled = !isConnectPoolEnabled;
            UpdateActivityButton(BtnConnectPool, isConnectPoolEnabled, "Pool");
            SaveSettings();
        }

        private void BtnConnectVC_Click(object sender, RoutedEventArgs e)
        {
            isConnectVCEnabled = !isConnectVCEnabled;
            UpdateActivityButton(BtnConnectVC, isConnectVCEnabled, "VC");
            SaveSettings();
        }

        private void BtnDisconnectPool_Click(object sender, RoutedEventArgs e)
        {
            isDisconnectPoolEnabled = !isDisconnectPoolEnabled;
            UpdateActivityButton(BtnDisconnectPool, isDisconnectPoolEnabled, "Pool");
            SaveSettings();
        }

        private void BtnDisconnectVC_Click(object sender, RoutedEventArgs e)
        {
            isDisconnectVCEnabled = !isDisconnectVCEnabled;
            UpdateActivityButton(BtnDisconnectVC, isDisconnectVCEnabled, "VC");
            SaveSettings();
        }

        private async void PlayDisconnectSound(bool forcePlay = false, bool isActivity = false)
        {
            if (!forcePlay && !isActivity && EnableDisconnectSoundCheck.IsChecked != true) return;

            customAudioPlayer.Volume = DisconnectVolumeSlider.Value;

            if (RadioCustomDisconnectSound.IsChecked == true && !string.IsNullOrEmpty(CustomDisconnectSoundPathInput.Text))
            {
                string soundPath = System.IO.Path.Combine(customSoundsFolder, CustomDisconnectSoundPathInput.Text);
                if (!File.Exists(soundPath)) soundPath = System.IO.Path.Combine(systemsSoundsFolder, CustomDisconnectSoundPathInput.Text);

                if (File.Exists(soundPath))
                {
                    customAudioPlayer.Open(new Uri(soundPath));
                    customAudioPlayer.Play();
                }
                else if (forcePlay && !isActivity)
                {
                    await ShowCustomDialog("File Missing", "Custom audio file not found. Please browse for it again.");
                }
            }
            else
            {
                string sysSound = (SystemDisconnectSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
                switch (sysSound)
                {
                    case "ConfusedIndividual - Server Disconnect":
                        PlaySystemSound("confusedindividual-server disconnect.wav");
                        break;
                    case "SimpleyViridian - Disconnected":
                        PlaySystemSound("simpleyviridian-disconnected.wav");
                        break;
                    case "SimpleyViridian - Disconnected 2.0":
                        PlaySystemSound("simpleyviridian-disconnected2.wav");
                        break;
                }
            }
        }

        // ==============================================================================
        // DYNAMIC HARDWARE CLIP KEY UI LOGIC
        // ==============================================================================

        private void RebuildClipKeyUI()
        {
            if (ClipKeyPanel == null) return;
            ClipKeyPanel.Children.Clear();

            for (int i = 0; i < ClipKeysList.Count; i++)
            {
                TextBox tb = CreateSingleKeyTextBox(ClipKeysList[i], i);
                ClipKeyPanel.Children.Add(tb);

                TextBlock plus = new TextBlock { Text = "+", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), FontSize = 16 };
                ClipKeyPanel.Children.Add(plus);
            }

            TextBox tbEmpty = CreateSingleKeyTextBox("", ClipKeysList.Count);
            ClipKeyPanel.Children.Add(tbEmpty);
        }

        private TextBox CreateSingleKeyTextBox(string text, int index)
        {
            TextBox tb = new TextBox
            {
                Text = text,
                Width = 80,
                Height = 30,
                IsReadOnly = true,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202225")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202225")),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = index
            };
            tb.PreviewKeyDown += SingleClipKeyBox_PreviewKeyDown;
            tb.PreviewKeyUp += (s, e) => e.Handled = true;
            return tb;
        }

        private async void SingleClipKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            TextBox tb = sender as TextBox;
            int index = (int)tb.Tag;

            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            if (key == Key.Escape)
            {
                if (index < ClipKeysList.Count)
                {
                    ClipKeysList.RemoveAt(index);
                    SaveSettings();
                    RebuildClipKeyUI();
                }
                Keyboard.ClearFocus();
                return;
            }

            if (key == Key.None) return;

            string keyStr = key.ToString();

            List<string> testList = new List<string>(ClipKeysList);
            if (index < testList.Count) testList[index] = keyStr;
            else testList.Add(keyStr);

            if (IsKeybindCollision(TriggerKeyInput.Text, testList))
            {
                await ShowCustomDialog("Keybind Collision", "You cannot set the Software Clip Keybind to be the exact same as the Trigger Keybind!");
                Keyboard.ClearFocus();
                return;
            }

            if (index < ClipKeysList.Count)
            {
                ClipKeysList[index] = keyStr;
            }
            else
            {
                ClipKeysList.Add(keyStr);
            }

            SaveSettings();
            RebuildClipKeyUI();
            Keyboard.ClearFocus();
        }

        // ==============================================================================
        // CORE CLIPPING LOGIC & INJECTION
        // ==============================================================================

        private async Task ControllerPollingLoop(CancellationToken token)
        {
            bool wasConnected = false;

            while (!token.IsCancellationRequested)
            {
                int result = -1;
                XINPUT_STATE state = new XINPUT_STATE();

                try { result = XInputGetState(0, out state); }
                catch (DllNotFoundException) { await Task.Delay(5000, token); continue; }
                catch { }

                bool isConnected = (result == 0);

                if (isConnected != wasConnected)
                {
                    Dispatcher.Invoke(() => {
                        ControllerTriggersPanel.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
                    });
                    wasConnected = isConnected;
                }

                if (isConnected)
                {
                    string currentButtons = GetControllerButtonsString(state.Gamepad);
                    int currentButtonCount = string.IsNullOrEmpty(currentButtons) ? 0 : currentButtons.Split('+').Length;

                    if (isCapturingControllerGlobal || isCapturingControllerLocal)
                    {
                        if (currentButtonCount > currentCapturedComboCount)
                        {
                            currentCapturedCombo = currentButtons;
                            currentCapturedComboCount = currentButtonCount;

                            Dispatcher.Invoke(() => {
                                if (isCapturingControllerGlobal) ControllerTriggerKeyInput.Text = currentCapturedCombo;
                                else if (isCapturingControllerLocal) ControllerLocalTriggerKeyInput.Text = currentCapturedCombo;
                            });
                        }

                        if (currentCapturedComboCount > 0 && currentButtonCount == 0)
                        {
                            Dispatcher.Invoke(() => {
                                if (isCapturingControllerGlobal)
                                {
                                    ControllerTriggerKeyInput.Text = currentCapturedCombo;
                                    isCapturingControllerGlobal = false;
                                }
                                else if (isCapturingControllerLocal)
                                {
                                    ControllerLocalTriggerKeyInput.Text = currentCapturedCombo;
                                    isCapturingControllerLocal = false;
                                }
                                currentCapturedCombo = "";
                                currentCapturedComboCount = 0;
                                Keyboard.ClearFocus();
                                SaveSettings();
                            });
                        }
                    }
                    else
                    {
                        string globalBind = "";
                        string localBind = "";
                        Dispatcher.Invoke(() => {
                            globalBind = ControllerTriggerKeyInput.Text;
                            localBind = ControllerLocalTriggerKeyInput.Text;
                        });

                        bool isGlobalPressed = !string.IsNullOrEmpty(globalBind) && currentButtons == globalBind;
                        bool isLocalPressed = !string.IsNullOrEmpty(localBind) && currentButtons == localBind;

                        if (isGlobalPressed && !wasControllerTriggerPressed)
                        {
                            Dispatcher.Invoke(() => OnClipTriggered(null, null));
                        }
                        if (isLocalPressed && !wasControllerLocalTriggerPressed)
                        {
                            Dispatcher.Invoke(() => OnLocalClipTriggered(null, null));
                        }

                        wasControllerTriggerPressed = isGlobalPressed;
                        wasControllerLocalTriggerPressed = isLocalPressed;
                    }

                    lastControllerStateStr = currentButtons;
                    await Task.Delay(20, token);
                }
                else
                {
                    await Task.Delay(2000, token);
                }
            }
        }

        private string GetControllerButtonsString(XINPUT_GAMEPAD gamepad)
        {
            List<string> buttons = new List<string>();

            if ((gamepad.wButtons & 0x1000) != 0) buttons.Add("A");
            if ((gamepad.wButtons & 0x2000) != 0) buttons.Add("B");
            if ((gamepad.wButtons & 0x4000) != 0) buttons.Add("X");
            if ((gamepad.wButtons & 0x8000) != 0) buttons.Add("Y");
            if ((gamepad.wButtons & 0x0100) != 0) buttons.Add("LB");
            if ((gamepad.wButtons & 0x0200) != 0) buttons.Add("RB");
            if ((gamepad.wButtons & 0x0010) != 0) buttons.Add("Start");
            if ((gamepad.wButtons & 0x0020) != 0) buttons.Add("Back");
            if ((gamepad.wButtons & 0x0040) != 0) buttons.Add("LS");
            if ((gamepad.wButtons & 0x0080) != 0) buttons.Add("RS");
            if ((gamepad.wButtons & 0x0001) != 0) buttons.Add("Up");
            if ((gamepad.wButtons & 0x0002) != 0) buttons.Add("Down");
            if ((gamepad.wButtons & 0x0004) != 0) buttons.Add("Left");
            if ((gamepad.wButtons & 0x0008) != 0) buttons.Add("Right");

            if (gamepad.bLeftTrigger > 128) buttons.Add("LT");
            if (gamepad.bRightTrigger > 128) buttons.Add("RT");

            return string.Join("+", buttons);
        }

        private void ControllerInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if ((tb == ControllerTriggerKeyInput && isCapturingControllerGlobal) ||
                (tb == ControllerLocalTriggerKeyInput && isCapturingControllerLocal))
            {
                return;
            }

            if (isCapturingControllerGlobal)
            {
                ControllerTriggerKeyInput.Text = preCaptureGlobalText;
                isCapturingControllerGlobal = false;
            }
            if (isCapturingControllerLocal)
            {
                ControllerLocalTriggerKeyInput.Text = preCaptureLocalText;
                isCapturingControllerLocal = false;
            }

            currentCapturedCombo = "";
            currentCapturedComboCount = 0;

            if (tb == ControllerTriggerKeyInput)
            {
                preCaptureGlobalText = tb.Text;
                tb.Text = "Waiting for input...";
                isCapturingControllerGlobal = true;
            }
            else if (tb == ControllerLocalTriggerKeyInput)
            {
                preCaptureLocalText = tb.Text;
                tb.Text = "Waiting for input...";
                isCapturingControllerLocal = true;
            }

            tb.Focus();
            e.Handled = true;
        }

        private void ControllerInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if (tb == ControllerTriggerKeyInput && isCapturingControllerGlobal)
            {
                isCapturingControllerGlobal = false;
                tb.Text = preCaptureGlobalText;
            }
            else if (tb == ControllerLocalTriggerKeyInput && isCapturingControllerLocal)
            {
                isCapturingControllerLocal = false;
                tb.Text = preCaptureLocalText;
            }
        }

        private void ControllerInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if (e.Key == Key.Escape)
            {
                tb.Text = (tb == ControllerTriggerKeyInput) ? preCaptureGlobalText : preCaptureLocalText;
                CancelControllerCapture();
                e.Handled = true;
            }
            else if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                tb.Text = "";
                if (tb == ControllerTriggerKeyInput) preCaptureGlobalText = "";
                if (tb == ControllerLocalTriggerKeyInput) preCaptureLocalText = "";
                CancelControllerCapture();
                e.Handled = true;
            }
        }

        private void CancelControllerCapture()
        {
            isCapturingControllerGlobal = false;
            isCapturingControllerLocal = false;
            currentCapturedCombo = "";
            currentCapturedComboCount = 0;
            Keyboard.ClearFocus();
            SaveSettings();
        }

        private async Task WheelPollingLoop(CancellationToken token)
        {
            bool wasConnected = false;
            while (!token.IsCancellationRequested)
            {
                bool anyConnected = false;
                List<string> uniqueButtons = new List<string>();

                for (int joyId = 0; joyId < 4; joyId++)
                {
                    JOYINFOEX state = new JOYINFOEX();
                    state.dwSize = Marshal.SizeOf(typeof(JOYINFOEX));
                    state.dwFlags = JOY_RETURNBUTTONS | JOY_RETURNPOV | JOY_RETURNPOVCTS;

                    if (joyGetPosEx(joyId, ref state) == JOYERR_NOERROR)
                    {
                        anyConnected = true;
                        string btns = GetWheelButtonsString(state);
                        if (!string.IsNullOrEmpty(btns))
                        {
                            foreach (string btn in btns.Split('+'))
                            {
                                if (!string.IsNullOrEmpty(btn) && !uniqueButtons.Contains(btn))
                                {
                                    uniqueButtons.Add(btn);
                                }
                            }
                        }
                    }
                }

                string currentButtons = string.Join("+", uniqueButtons);

                if (anyConnected != wasConnected)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            WheelTriggersPanel.Visibility = anyConnected ? Visibility.Visible : Visibility.Collapsed;
                        });
                        wasConnected = anyConnected;
                    }

                    if (anyConnected)
                    {
                        int currentButtonCount = string.IsNullOrEmpty(currentButtons) ? 0 : currentButtons.Split('+').Length;

                        if (isCapturingWheelGlobal || isCapturingWheelLocal)
                        {
                            if (currentButtonCount > currentCapturedWheelComboCount)
                            {
                                currentCapturedWheelCombo = currentButtons;
                                currentCapturedWheelComboCount = currentButtonCount;

                                Dispatcher.Invoke(() =>
                                {
                                    if (isCapturingWheelGlobal) WheelTriggerKeyInput.Text = currentCapturedWheelCombo;
                                    else if (isCapturingWheelLocal) WheelLocalTriggerKeyInput.Text = currentCapturedWheelCombo;
                                });
                            }

                            if (currentCapturedWheelComboCount > 0 && currentButtonCount == 0)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (isCapturingWheelGlobal)
                                    {
                                        WheelTriggerKeyInput.Text = currentCapturedWheelCombo;
                                        isCapturingWheelGlobal = false;
                                    }
                                    else if (isCapturingWheelLocal)
                                    {
                                        WheelLocalTriggerKeyInput.Text = currentCapturedWheelCombo;
                                        isCapturingWheelLocal = false;
                                    }
                                    currentCapturedWheelCombo = "";
                                    currentCapturedWheelComboCount = 0;
                                    Keyboard.ClearFocus();
                                    SaveSettings();
                                });
                            }
                        }
                        else
                        {
                            string globalBind = "";
                            string localBind = "";
                            Dispatcher.Invoke(() =>
                            {
                                globalBind = WheelTriggerKeyInput.Text;
                                localBind = WheelLocalTriggerKeyInput.Text;
                            });

                            bool isGlobalPressed = !string.IsNullOrEmpty(globalBind) && currentButtons == globalBind;
                            bool isLocalPressed = !string.IsNullOrEmpty(localBind) && currentButtons == localBind;

                            if (isGlobalPressed && !wasWheelTriggerPressed)
                            {
                                Dispatcher.Invoke(() => OnClipTriggered(null, null));
                            }
                            if (isLocalPressed && !wasWheelLocalTriggerPressed)
                            {
                                Dispatcher.Invoke(() => OnLocalClipTriggered(null, null));
                            }

                            wasWheelTriggerPressed = isGlobalPressed;
                            wasWheelLocalTriggerPressed = isLocalPressed;
                        }

                        await Task.Delay(20, token);
                    }
                    else
                    {
                        await Task.Delay(2000, token);
                    }
                }
            }

        private string GetWheelButtonsString(JOYINFOEX state)
        {
            List<string> buttons = new List<string>();
            for (int btnIndex = 0; btnIndex < 32; btnIndex++)
            {
                if ((state.dwButtons & (1 << btnIndex)) != 0)
                {
                    buttons.Add($"WBtn{btnIndex + 1}");
                }
            }

            if (state.dwPOV != 65535)
            {
                int pov = state.dwPOV;
                if (pov >= 31500 || pov <= 4500) buttons.Add("WheelUp");
                if (pov >= 4500 && pov <= 13500) buttons.Add("WheelRight");
                if (pov >= 13500 && pov <= 22500) buttons.Add("WheelDown");
                if (pov >= 22500 && pov <= 31500) buttons.Add("WheelLeft");
            }

            return string.Join("+", buttons);
        }

        private void WheelInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if ((tb == WheelTriggerKeyInput && isCapturingWheelGlobal) ||
                (tb == WheelLocalTriggerKeyInput && isCapturingWheelLocal))
            {
                return;
            }

            if (isCapturingWheelGlobal)
            {
                WheelTriggerKeyInput.Text = preCaptureWheelGlobalText;
                isCapturingWheelGlobal = false;
            }
            if (isCapturingWheelLocal)
            {
                WheelLocalTriggerKeyInput.Text = preCaptureWheelLocalText;
                isCapturingWheelLocal = false;
            }

            currentCapturedWheelCombo = "";
            currentCapturedWheelComboCount = 0;

            if (tb == WheelTriggerKeyInput)
            {
                preCaptureWheelGlobalText = tb.Text;
                tb.Text = "Waiting for input...";
                isCapturingWheelGlobal = true;
            }
            else if (tb == WheelLocalTriggerKeyInput)
            {
                preCaptureWheelLocalText = tb.Text;
                tb.Text = "Waiting for input...";
                isCapturingWheelLocal = true;
            }

            tb.Focus();
            e.Handled = true;
        }

        private void WheelInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if (tb == WheelTriggerKeyInput && isCapturingWheelGlobal)
            {
                isCapturingWheelGlobal = false;
                tb.Text = preCaptureWheelGlobalText;
            }
            else if (tb == WheelLocalTriggerKeyInput && isCapturingWheelLocal)
            {
                isCapturingWheelLocal = false;
                tb.Text = preCaptureWheelLocalText;
            }
        }

        private void WheelInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            TextBox tb = sender as TextBox;

            if (e.Key == Key.Escape)
            {
                tb.Text = (tb == WheelTriggerKeyInput) ? preCaptureWheelGlobalText : preCaptureWheelLocalText;
                CancelWheelCapture();
                e.Handled = true;
            }
            else if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                tb.Text = "";
                if (tb == WheelTriggerKeyInput) preCaptureWheelGlobalText = "";
                if (tb == WheelLocalTriggerKeyInput) preCaptureWheelLocalText = "";
                CancelWheelCapture();
                e.Handled = true;
            }
        }

        private void CancelWheelCapture()
        {
            isCapturingWheelGlobal = false;
            isCapturingWheelLocal = false;
            currentCapturedWheelCombo = "";
            currentCapturedWheelComboCount = 0;
            Keyboard.ClearFocus();
            SaveSettings();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            bool isCustom = CustomServerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CustomServerInput.Text);
            string host = "unknown_host";
            if (isCustom && Uri.TryCreate(CustomServerInput.Text.Trim(), UriKind.Absolute, out Uri uri)) host = uri.Host;
            string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
            string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");

            if (string.IsNullOrWhiteSpace(DiscordIdInput.Text) || !File.Exists(tokenPath))
            {
                DiscordSignInBtn_Click(null, null);
                return;
            }

            if (ConnectButton.Content.ToString() == "Listening...")
                StopListening();
            else
                StartListening();
        }

        private async void StartListening()
        {
            bool isCustom = false;
            string targetUrl = "";
            Dispatcher.Invoke(() => {
                isCustom = CustomServerCheck.IsChecked == true;
                targetUrl = GetServerUrl();
            });

            string host = "unknown_host";
            if (isCustom && Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri uri)) host = uri.Host;
            string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
            string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");

            string currentDiscordId = "";
            Dispatcher.Invoke(() => currentDiscordId = DiscordIdInput.Text);

            if (string.IsNullOrWhiteSpace(currentDiscordId) || !File.Exists(tokenPath))
            {
                Dispatcher.Invoke(() => {
                    DiscordSignInBtn_Click(null, null);
                });
                return;
            }

            if (isCustom)
            {
                if (string.IsNullOrWhiteSpace(CustomServerInput.Text))
                {
                    await ShowCustomDialog("Missing Server URL", "Please enter a valid custom server URL, or uncheck 'Connect to custom server'.");
                    return;
                }
                if (!Uri.TryCreate(CustomServerInput.Text.Trim(), UriKind.Absolute, out _))
                {
                    await ShowCustomDialog("Invalid Server URL", "Please enter a valid URL starting with ws:// or wss://");
                    return;
                }
            }

            try
            {
                HotkeyManager.Current.Remove("SyncClip");
                HotkeyManager.Current.Remove("LocalSyncClip");
                var converter = new KeyGestureConverter();

                if (!string.IsNullOrWhiteSpace(TriggerKeyInput.Text))
                {
                    KeyGesture triggerGesture = (KeyGesture)converter.ConvertFromString(TriggerKeyInput.Text);
                    HotkeyManager.Current.AddOrReplace("SyncClip", triggerGesture.Key, triggerGesture.Modifiers, OnClipTriggered);
                }

                if (!string.IsNullOrWhiteSpace(LocalTriggerKeyInput.Text))
                {
                    KeyGesture localTriggerGesture = (KeyGesture)converter.ConvertFromString(LocalTriggerKeyInput.Text);
                    HotkeyManager.Current.AddOrReplace("LocalSyncClip", localTriggerGesture.Key, localTriggerGesture.Modifiers, OnLocalClipTriggered);
                }

                ConnectButton.Content = "Listening...";
                ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 181, 129));

                CustomServerInput.IsReadOnly = true;
                CustomServerInput.Opacity = 0.5;
                CustomServerCheck.IsEnabled = false;

                isSyncActive = true;
                await ConnectToServer();
            }
            catch (NHotkey.HotkeyAlreadyRegisteredException)
            {
                isSyncActive = false;
                StopListening();
                ShowHotkeyTakenWarning();
            }
            catch (Exception ex)
            {
                isSyncActive = false;
                StopListening();
                await ShowCustomDialog("Error", "Error setting hotkey. Check your formatting.\n\n" + ex.Message);
            }
        }

        private async void StopListening()
        {
            isSyncActive = false;
            isReconnecting = false;
            HotkeyManager.Current.Remove("SyncClip");
            HotkeyManager.Current.Remove("LocalSyncClip");
            ConnectButton.Content = "Activate Syncing";
            ConnectButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));

            CustomServerInput.IsReadOnly = false;
            CustomServerInput.Opacity = 1.0;
            CustomServerCheck.IsEnabled = true;

            await DisconnectFromServer();
        }

        private string ExtractNameFromFriendString(string friendStr)
        {
            int spaceIdx = friendStr.IndexOf(' ');
            if (spaceIdx > 0 && friendStr.Length > spaceIdx + 3)
            {
                return friendStr.Substring(spaceIdx + 2, friendStr.Length - spaceIdx - 3);
            }
            return friendStr;
        }

        public class ActiveNotification
        {
            public Window Window { get; set; }
            public Border MainBorder { get; set; }
            public DateTime SpawnTime { get; set; }
            public double LifespanSeconds { get; set; }
            public string Location { get; set; }
            public int MonitorIndex { get; set; }
            public bool IsAnimatingOut { get; set; }
            public bool IsVisible { get; set; }
            public double CurrentTargetTop { get; set; }
        }

        private List<ActiveNotification> _activeNotifs = new List<ActiveNotification>();
        private ImageSource _cachedAppIcon = null;

        private void ShowNotification(string topText, string bottomText, string type)
        {
            Dispatcher.Invoke(() => {
                if (_cachedAppIcon == null)
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                    if (icon != null)
                    {
                        _cachedAppIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                }

                bool isClip = type == "Clip" || type == "ExampleClip";
                bool isConnect = type == "Connect" || type == "ExampleConnect";

                bool enabled = false;
                string location = "Bottom Right";
                int monitorIndex = 0;
                string colorStr = "#5865F2";
                bool flipAccent = false;
                double timeLimit = 3.5;

                if (isClip)
                {
                    enabled = EnableClipNotifCheck.IsChecked == true;
                    location = (ClipNotifLocationCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Right";
                    monitorIndex = ClipNotifMonitorCombo.SelectedIndex >= 0 ? ClipNotifMonitorCombo.SelectedIndex : 0;
                    if (ClipNotifColorBox.Background is SolidColorBrush scb) colorStr = scb.Color.ToString();
                    flipAccent = ClipNotifFlipAccentCheck.IsChecked == true;
                    double.TryParse(ClipNotifTimeLimitInput.Text, out timeLimit);
                }
                else
                {
                    enabled = EnableConnectNotifCheck.IsChecked == true;
                    location = (ConnectNotifLocationCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Bottom Right";
                    monitorIndex = ConnectNotifMonitorCombo.SelectedIndex >= 0 ? ConnectNotifMonitorCombo.SelectedIndex : 0;
                    if (type == "ExampleConnect")
                    {
                        if (ClipNotifColorBox.Background is SolidColorBrush scb) colorStr = scb.Color.ToString();
                    }
                    else
                    {
                        colorStr = isConnect ? "#43b581" : "#f04747";
                    }
                    flipAccent = ConnectNotifFlipAccentCheck.IsChecked == true;
                    double.TryParse(ConnectNotifTimeLimitInput.Text, out timeLimit);
                }

                if (!enabled) return;

                Window notif = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Width = 280,
                    Height = 70,
                    IsHitTestVisible = false
                };

                bool isRight = location.Contains("Right");
                bool putAccentOnLeft = !isRight;
                if (flipAccent) putAccentOnLeft = !putAccentOnLeft;

                Border mainBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202225")),
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136")),
                    BorderThickness = new Thickness(1),
                    Opacity = 0
                };

                Grid grid = new Grid();
                if (putAccentOnLeft)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }
                else
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                }

                Rectangle accentLine = new Rectangle
                {
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorStr)),
                    Width = 4,
                    VerticalAlignment = VerticalAlignment.Stretch
                };

                StackPanel textPanel = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(15, 0, 5, 0)
                };

                TextBlock topTb = new TextBlock { Text = topText, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold };
                TextBlock bottomTb = new TextBlock { Text = bottomText, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b9bbbe")), FontSize = 12, Margin = new Thickness(0, 5, 0, 0) };

                textPanel.Children.Add(topTb);
                textPanel.Children.Add(bottomTb);

                Image iconImage = new Image
                {
                    Source = _cachedAppIcon,
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(10),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                Grid.SetColumn(accentLine, putAccentOnLeft ? 0 : 2);
                Grid.SetColumn(textPanel, putAccentOnLeft ? 1 : 0);
                Grid.SetColumn(iconImage, putAccentOnLeft ? 2 : 1);

                grid.Children.Add(accentLine);
                grid.Children.Add(textPanel);
                grid.Children.Add(iconImage);
                mainBorder.Child = grid;
                notif.Content = mainBorder;

                var newNotif = new ActiveNotification
                {
                    Window = notif,
                    MainBorder = mainBorder,
                    SpawnTime = DateTime.Now,
                    LifespanSeconds = timeLimit > 0 ? timeLimit : 3,
                    Location = location,
                    MonitorIndex = monitorIndex,
                    IsAnimatingOut = false,
                    IsVisible = false
                };

                _activeNotifs.Add(newNotif);
                ProcessNotifications(location, monitorIndex);
            });
        }

        private void ProcessNotifications(string location, int monitorIndex)
        {
            Dispatcher.Invoke(() => {
                var now = DateTime.Now;
                var locNotifs = _activeNotifs.Where(n => n.Location == location && n.MonitorIndex == monitorIndex).ToList();
                bool isRight = location.Contains("Right");
                bool isBottom = location.Contains("Bottom");

                var screens = System.Windows.Forms.Screen.AllScreens;
                var screen = (monitorIndex >= 0 && monitorIndex < screens.Length) ? screens[monitorIndex] : System.Windows.Forms.Screen.PrimaryScreen;
                var bounds = screen.WorkingArea;

                PresentationSource source = PresentationSource.FromVisual(Application.Current.MainWindow);
                double scaleX = source != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
                double scaleY = source != null ? source.CompositionTarget.TransformToDevice.M22 : 1.0;

                Rect workArea = new Rect(bounds.Left / scaleX, bounds.Top / scaleY, bounds.Width / scaleX, bounds.Height / scaleY);

                foreach (var notif in locNotifs)
                {
                    if (!notif.IsAnimatingOut && (now - notif.SpawnTime).TotalSeconds >= notif.LifespanSeconds)
                    {
                        notif.IsAnimatingOut = true;
                        if (notif.IsVisible)
                        {
                            var outStoryboard = new System.Windows.Media.Animation.Storyboard();
                            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
                            System.Windows.Media.Animation.Storyboard.SetTarget(fadeOut, notif.MainBorder);
                            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeOut, new PropertyPath(Border.OpacityProperty));

                            double startTop = isBottom ? notif.CurrentTargetTop + 30 : notif.CurrentTargetTop - 30;

                            var slideOut = new System.Windows.Media.Animation.DoubleAnimation { From = notif.CurrentTargetTop, To = startTop, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
                            System.Windows.Media.Animation.Storyboard.SetTarget(slideOut, notif.Window);
                            System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideOut, new PropertyPath(Window.TopProperty));

                            outStoryboard.Children.Add(fadeOut);
                            outStoryboard.Children.Add(slideOut);
                            outStoryboard.Completed += (s, e) => {
                                notif.Window.Close();
                                _activeNotifs.Remove(notif);
                                ProcessNotifications(location, monitorIndex);
                            };
                            outStoryboard.Begin();
                        }
                        else
                        {
                            _activeNotifs.Remove(notif);
                        }
                    }
                }

                var activeQueue = _activeNotifs.Where(n => n.Location == location && n.MonitorIndex == monitorIndex && !n.IsAnimatingOut).OrderBy(n => n.SpawnTime).ToList();

                int visibleCount = 0;
                for (int i = 0; i < activeQueue.Count; i++)
                {
                    var notif = activeQueue[i];
                    if (visibleCount >= 3) break;

                    double targetTop = isBottom ? workArea.Bottom - (notif.Window.Height * (visibleCount + 1)) - (15 * (visibleCount + 1))
                                                : workArea.Top + (notif.Window.Height * visibleCount) + (15 * (visibleCount + 1));

                    if (!notif.IsVisible)
                    {
                        double remaining = notif.LifespanSeconds - (DateTime.Now - notif.SpawnTime).TotalSeconds;
                        if (remaining > 0)
                        {
                            notif.IsVisible = true;
                            notif.CurrentTargetTop = targetTop;

                            notif.Window.Left = isRight ? workArea.Right - notif.Window.Width - 15 : workArea.Left + 15;
                            double startTop = isBottom ? targetTop + 30 : targetTop - 30;
                            notif.Window.Top = startTop;

                            notif.Window.Show();

                            var storyboard = new System.Windows.Media.Animation.Storyboard();
                            var fadeAnimation = new System.Windows.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(300) };
                            System.Windows.Media.Animation.Storyboard.SetTarget(fadeAnimation, notif.MainBorder);
                            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeAnimation, new PropertyPath(Border.OpacityProperty));

                            var slideAnimation = new System.Windows.Media.Animation.DoubleAnimation { From = startTop, To = targetTop, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                            System.Windows.Media.Animation.Storyboard.SetTarget(slideAnimation, notif.Window);
                            System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideAnimation, new PropertyPath(Window.TopProperty));

                            storyboard.Children.Add(fadeAnimation);
                            storyboard.Children.Add(slideAnimation);
                            storyboard.Begin();

                            Task.Run(async () => {
                                await Task.Delay(TimeSpan.FromSeconds(remaining));
                                ProcessNotifications(location, monitorIndex);
                            });
                        }
                    }
                    else if (Math.Abs(notif.CurrentTargetTop - targetTop) > 1)
                    {
                        var slideAnimation = new System.Windows.Media.Animation.DoubleAnimation { From = notif.CurrentTargetTop, To = targetTop, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } };
                        System.Windows.Media.Animation.Storyboard.SetTarget(slideAnimation, notif.Window);
                        System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideAnimation, new PropertyPath(Window.TopProperty));

                        var storyboard = new System.Windows.Media.Animation.Storyboard();
                        storyboard.Children.Add(slideAnimation);
                        storyboard.Begin();

                        notif.CurrentTargetTop = targetTop;
                    }

                    visibleCount++;
                }

                var visibleNotifs = activeQueue.Where(n => n.IsVisible).OrderByDescending(n => n.SpawnTime).ToList();
                foreach (var notif in visibleNotifs)
                {
                    notif.Window.Topmost = false;
                    notif.Window.Topmost = true;
                }
            });
        }


        // --- WEBSOCKET ENGINE ---

        private string GetServerUrl()
        {
            string url = "wss://clip.oxy.pizza";
            Dispatcher.Invoke(() => {
                if (CustomServerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CustomServerInput.Text))
                {
                    url = CustomServerInput.Text.Trim();
                }
            });
            return url;
        }

        private async Task ConnectToServer()
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open) return;

            webSocket = new ClientWebSocket();
            wsCts = new CancellationTokenSource();
            try
            {
                ServerStatusDot.Fill = System.Windows.Media.Brushes.Orange;
                ServerStatusText.Text = "Connecting...";

                await webSocket.ConnectAsync(new Uri(GetServerUrl()), wsCts.Token);

                ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#43b581"));
                ServerStatusText.Text = "Connected";
                ServerStatusText.ToolTip = null;
                isReconnecting = false;
                PlayConnectSound();

                string targetUrl = GetServerUrl();
                bool isCustom = false;
                Dispatcher.Invoke(() => isCustom = CustomServerCheck.IsChecked == true);

                if (isCustom)
                {
                    ShowNotification("Custom Server", $"Connected to {targetUrl}", "Connect");
                    WriteLog($"Connected to custom server: {targetUrl} ({AppVersion})");
                }
                else
                {
                    ShowNotification("Central Server", "Connected", "Connect");
                    WriteLog($"Connected to the central server. ({AppVersion})");
                }

                string tokenId = "";
                string host = "unknown_host";
                if (isCustom && Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri uri)) host = uri.Host;
                string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
                string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");
                string appIdPath = System.IO.Path.Combine(targetFolder, "app_id.json");

                activeAppUuid = appUuid;
                if (File.Exists(tokenPath)) tokenId = File.ReadAllText(tokenPath).Trim('"', ' ', '\n', '\r');
                if (File.Exists(appIdPath)) activeAppUuid = File.ReadAllText(appIdPath).Trim('"', ' ', '\n', '\r');

                string currentDiscordId = DiscordIdInput.Text;
                if (string.IsNullOrWhiteSpace(currentDiscordId) || string.IsNullOrWhiteSpace(tokenId))
                {
                    Dispatcher.Invoke(() => StopListening());
                    return;
                }

                await SendWsMessage(new { action = "identify", discord_id = currentDiscordId, token_id = tokenId, app_uuid = activeAppUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList(), version = AppVersion });
                AskServerToResolveNames();

                _ = ReceiveMessages();
            }
            catch
            {
                ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f04747"));
                ServerStatusText.Text = "Server Offline";
                TriggerAutoReconnect();
            }
        }

        private async Task DisconnectFromServer()
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                try
                {
                    wsCts?.Cancel();
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch { }
            }
            webSocket?.Dispose();
            wsCts?.Dispose();
            webSocket = null;
            wsCts = null;

            ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f04747"));
            ServerStatusText.Text = "Disconnected";
            ServerStatusText.ToolTip = null;
            CurrentVcText.Visibility = Visibility.Collapsed;
            VcUsersPanel.Visibility = Visibility.Collapsed;
            currentActiveVcFriends.Clear();
            currentVcName = "";
            currentVcId = "";
            previousVcId = "";
            currentServerName = "";
            currentPerspectiveName = "";
            ResetPoolUI();
            PlayDisconnectSound();

            bool isCustom = false;
            Dispatcher.Invoke(() => isCustom = CustomServerCheck.IsChecked == true);

            if (isCustom)
            {
                ShowNotification("Custom Server", "Disconnected", "Disconnect");
                WriteLog("Disconnected from custom server.");
            }
            else
            {
                ShowNotification("Central Server", "Disconnected", "Disconnect");
                WriteLog("Disconnected from the central server.");
            }
        }

        private void ResetPoolUI()
        {
            Dispatcher.Invoke(() => {
                CurrentPoolText.Visibility = Visibility.Collapsed;
                PoolUsersPanel.Visibility = Visibility.Collapsed;
                currentActivePoolFriends.Clear();
                activePoolCode = "";
                previousPoolCode = "";
                currentPoolName = "";
                amIPoolOwner = false;

                PoolCodeInput.Text = "";
                PoolNameInput.Text = "";

                JoinCreatePoolPanel.Visibility = Visibility.Visible;
                ActivePoolControls.Visibility = Visibility.Collapsed;
            });
        }

        private async Task SendWsMessage(object payload)
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                string json = JsonSerializer.Serialize(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        private async Task ReceiveMessages()
        {
            var buffer = new byte[8192];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), wsCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        string message = Encoding.UTF8.GetString(ms.ToArray());
                        using (JsonDocument doc = JsonDocument.Parse(message))
                        {
                            string action = doc.RootElement.GetProperty("action").GetString();

                            if (action == "sync_clip" && ReceiveClipsCheck.IsChecked == true)
                            {
                                string senderId = doc.RootElement.GetProperty("sender_id").GetString();

                                bool anyVc = true;
                                Dispatcher.Invoke(() => { anyVc = RadioAnyVC.IsChecked ?? true; });
                                bool isVcApproved = anyVc || (!string.IsNullOrEmpty(currentVcId) && ApprovedChannels.Any(c => c.Id == currentVcId));

                                if (!isVcApproved)
                                {
                                    bool isPoolFriend = currentActivePoolFriends.Any(f => f.StartsWith(senderId + " "));
                                    if (!isPoolFriend)
                                    {
                                        WriteLog($"Ignored remote clip command from user {senderId} (Current VC is not approved).");
                                        continue;
                                    }
                                }

                                var matchedUser = ApprovedUsers.FirstOrDefault(u => u.Id == senderId);
                                if (matchedUser != null)
                                {
                                    WriteLog($"Received remote clip command from user {senderId} ({matchedUser.DisplayName}).");

                                    bool wasClipped = await ReceiveNetworkClipCommand(matchedUser.DisplayName);

                                    if (wasClipped)
                                    {
                                        totalClipsStats.Received++;
                                        if (!userReceivedStats.ContainsKey(senderId)) userReceivedStats[senderId] = new UserStatCount { Id = senderId, Name = matchedUser.DisplayName, Count = 0 };
                                        userReceivedStats[senderId].Count++;
                                        userReceivedStats[senderId].Name = matchedUser.DisplayName;
                                        SaveStats();
                                    }
                                }
                            }
                            else if (action == "resolved_ids")
                            {
                                var usersJson = doc.RootElement.GetProperty("users");
                                var channelsJson = doc.RootElement.GetProperty("channels");

                                Dispatcher.Invoke(() =>
                                {
                                    string myId = DiscordIdInput.Text;
                                    if (usersJson.TryGetProperty(myId, out JsonElement myNameElem))
                                    {
                                        myGlobalDiscordName = myNameElem.GetString();
                                    }

                                    foreach (var user in ApprovedUsers)
                                    {
                                        if (usersJson.TryGetProperty(user.Id, out JsonElement nameElement))
                                            user.DisplayName = nameElement.GetString();
                                    }
                                    foreach (var channel in ApprovedChannels)
                                    {
                                        if (channelsJson.TryGetProperty(channel.Id, out JsonElement nameElement))
                                            channel.DisplayName = nameElement.GetString();
                                    }
                                });
                            }
                            else if (action == "all_users_list")
                            {
                                var usersJson = doc.RootElement.GetProperty("users");
                                Dispatcher.Invoke(() => {
                                    RawFetchedUsers.Clear();
                                    foreach (var prop in usersJson.EnumerateObject())
                                    {
                                        RawFetchedUsers.Add(new DiscordItem { Id = prop.Name, DisplayName = prop.Value.GetString() });
                                    }
                                    FilterUserList();
                                });
                            }
                            else if (action == "concurrent_apps")
                            {
                                int count = doc.RootElement.GetProperty("count").GetInt32();
                                Dispatcher.Invoke(() => {
                                    if (count > 1)
                                    {
                                        ServerStatusText.Text = $"Connected in {count} locations at once";
                                        ServerStatusText.ToolTip = "Manage linked apps via Discord Bot";
                                    }
                                    else
                                    {
                                        ServerStatusText.Text = "Connected";
                                        ServerStatusText.ToolTip = null;
                                    }
                                });
                            }
                            else if (action == "linking_locked")
                            {
                                Dispatcher.InvokeAsync(async () => {
                                    StopListening();
                                    await ShowCustomDialog("Linking Locked", "App linking is currently locked for your Discord account.\n\nPlease DM the bot with the command '/unlock' to allow new apps to connect, then try again.");
                                    WriteLog("Connection blocked: App linking is locked by user.");
                                });
                            }
                            else if (action == "rate_limited")
                            {
                                Dispatcher.InvokeAsync(async () => {
                                    StopListening();
                                    await ShowCustomDialog("Rate Limited", "You are reconnecting too fast. Please wait a minute before trying again.");
                                    WriteLog("Connection blocked: Rate limited by server.");
                                });
                            }
                            else if (action == "dm_verification_failed")
                            {
                                Dispatcher.InvokeAsync(async () => {
                                    StopListening();
                                    await ShowCustomDialog("Verification Failed", "We could not send you a DM to verify your app!\n\nPlease ensure your DMs are open, or authorize the bot directly by going to:\nhttps://oxy.pizza/clippingtools/authorize\n\nAfter authorizing, click 'Activate Syncing' to try connecting again.");
                                    WriteLog("Discord DM Verification Error");
                                });
                            }
                            else if (action == "custom_message")
                            {
                                string title = doc.RootElement.TryGetProperty("title", out JsonElement titleElem) ? titleElem.GetString() : "Message";
                                string msg = doc.RootElement.TryGetProperty("message", out JsonElement msgElem) ? msgElem.GetString() : "";
                                Dispatcher.InvokeAsync(async () => {
                                    await ShowCustomDialog(title, msg);
                                    WriteLog($"Received custom server message: {title}");
                                });
                            }
                            else if (action == "pool_error")
                            {
                                string poolMsg = doc.RootElement.GetProperty("message").GetString();
                                Dispatcher.InvokeAsync(async () => {
                                    await ShowCustomDialog("Pool Error", poolMsg);
                                    WriteLog("The connection to the pool encountered an error.");
                                });
                            }
                            else if (action == "pool_kicked")
                            {
                                Dispatcher.InvokeAsync(async () => {
                                    ResetPoolUI();
                                    await ShowCustomDialog("Kicked", "You have been kicked from the pool.");
                                    WriteLog("You were kicked from the pool.");
                                });
                            }
                            else if (action == "pool_banned")
                            {
                                Dispatcher.InvokeAsync(async () => {
                                    ResetPoolUI();
                                    await ShowCustomDialog("Banned", "You have been banned from the pool.");
                                    WriteLog("You were banned from the pool.");
                                });
                            }
                            else if (action == "pool_closed")
                            {
                                Dispatcher.Invoke(() => {
                                    ResetPoolUI();
                                    WriteLog("The pool was closed by the owner.");
                                });
                            }
                            else if (action == "auth_failed")
                            {
                                string reason = doc.RootElement.TryGetProperty("reason", out JsonElement rElem) ? rElem.GetString() : "Unknown authentication error.";
                                Dispatcher.InvokeAsync(async () => {
                                    StopListening();
                                    WriteLog("Server rejected connection: " + reason + " - Auto-triggering re-authentication.");

                                    bool isCustom = CustomServerCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CustomServerInput.Text);
                                    string host = "unknown_host";
                                    if (isCustom && Uri.TryCreate(CustomServerInput.Text.Trim(), UriKind.Absolute, out Uri uri)) host = uri.Host;
                                    string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
                                    string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");

                                    if (File.Exists(tokenPath)) File.Delete(tokenPath);

                                    await ShowCustomDialog("Authentication Failed", "Your login token is invalid or expired.\n\nPlease log in via Discord again to continue syncing.");
                                    DiscordSignInBtn_Click(null, null);
                                });
                            }
                            else if (action == "client_pool_update")
                            {
                                Dispatcher.Invoke(() => {
                                    string myId = DiscordIdInput.Text;
                                    activePoolCode = doc.RootElement.GetProperty("pool_code").GetString();
                                    string poolName = doc.RootElement.GetProperty("name").GetString();
                                    currentPoolName = poolName;
                                    string ownerId = doc.RootElement.GetProperty("owner").GetString();
                                    bool isOpen = doc.RootElement.GetProperty("is_open").GetBoolean();
                                    var members = doc.RootElement.GetProperty("members");

                                    amIPoolOwner = (myId == ownerId);

                                    JoinCreatePoolPanel.Visibility = Visibility.Collapsed;
                                    ActivePoolControls.Visibility = Visibility.Visible;
                                    ActivePoolCodeDisplay.Text = activePoolCode;

                                    if (amIPoolOwner)
                                    {
                                        TogglePoolBtn.Visibility = Visibility.Visible;
                                        ClosePoolBtn.Visibility = Visibility.Visible;
                                        LeavePoolBtn.Visibility = Visibility.Collapsed;

                                        TogglePoolBtn.Background = isOpen ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("IndianRed"));
                                        TogglePoolBtn.Content = isOpen ? "Joinable" : "Closed";
                                    }
                                    else
                                    {
                                        TogglePoolBtn.Visibility = Visibility.Collapsed;
                                        ClosePoolBtn.Visibility = Visibility.Collapsed;
                                        LeavePoolBtn.Visibility = Visibility.Visible;
                                    }

                                    List<string> previousPoolFriends = new List<string>(currentActivePoolFriends);
                                    currentActivePoolFriends.Clear();
                                    PoolUsersPanel.Children.Clear();

                                    List<(string DisplayText, string SortName, bool IsConnected, string UserId, string RawName)> usersInPool = new List<(string, string, bool, string, string)>();

                                    foreach (var prop in members.EnumerateObject())
                                    {
                                        string uid = prop.Name;
                                        bool isConnected = prop.Value.GetProperty("is_connected").GetBoolean();

                                        string displayName = uid;

                                        if (uid == myId)
                                        {
                                            displayName = "You";
                                        }
                                        else
                                        {
                                            var matchedUser = ApprovedUsers.FirstOrDefault(u => u.Id == uid);
                                            if (matchedUser != null) displayName = matchedUser.DisplayName;
                                            else
                                            {
                                                var matchedVis = AllVisibleUsers.FirstOrDefault(u => u.Id == uid);
                                                if (matchedVis != null) displayName = matchedVis.DisplayName;
                                            }
                                        }

                                        string prefix = (uid == ownerId) ? "👑 " : "✔️ ";

                                        if (uid != myId && isConnected)
                                        {
                                            currentActivePoolFriends.Add($"{uid} ({displayName})");
                                        }

                                        usersInPool.Add((prefix + displayName, displayName, isConnected, uid, displayName));
                                    }

                                    usersInPool.Sort((a, b) => a.SortName.CompareTo(b.SortName));

                                    var addedPool = currentActivePoolFriends.Except(previousPoolFriends).ToList();
                                    var removedPool = previousPoolFriends.Except(currentActivePoolFriends).ToList();

                                    if (activePoolCode == previousPoolCode && !string.IsNullOrEmpty(activePoolCode))
                                    {
                                        if (isConnectPoolEnabled && addedPool.Count > 0) PlayConnectSound(isActivity: true);
                                        if (isDisconnectPoolEnabled && removedPool.Count > 0) PlayDisconnectSound(isActivity: true);
                                        foreach (var f in addedPool) ShowNotification(ExtractNameFromFriendString(f), "Connected", "Connect");
                                        foreach (var f in removedPool) ShowNotification(ExtractNameFromFriendString(f), "Disconnected", "Disconnect");
                                    }
                                    previousPoolCode = activePoolCode;

                                    foreach (var user in usersInPool)
                                    {
                                        StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent };
                                        TextBlock tb = new TextBlock { Text = user.DisplayText, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b9bbbe")), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };

                                        System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                                        dot.Fill = user.IsConnected ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f04747"));

                                        sp.Children.Add(tb);
                                        sp.Children.Add(dot);

                                        if (amIPoolOwner && user.UserId != myId)
                                        {
                                            sp.Cursor = Cursors.Hand;
                                            sp.ToolTip = "Manage User";
                                            sp.Tag = new Tuple<string, string>(user.UserId, user.RawName);
                                            sp.MouseLeftButtonDown += PoolUser_MouseLeftButtonDown;
                                        }

                                        PoolUsersPanel.Children.Add(sp);
                                    }

                                    CurrentPoolText.Text = $"Current Pool: {poolName}";
                                    CurrentPoolText.Visibility = Visibility.Visible;
                                    PoolUsersPanel.Visibility = Visibility.Visible;
                                });
                            }
                            else if (action == "client_vc_update")
                            {
                                var vcMapElement = doc.RootElement.GetProperty("vc_map");

                                Dispatcher.Invoke(() => {
                                    string myId = DiscordIdInput.Text;
                                    if (!vcMapElement.TryGetProperty(myId, out JsonElement myVcData))
                                    {
                                        CurrentVcText.Visibility = Visibility.Collapsed;
                                        VcUsersPanel.Visibility = Visibility.Collapsed;
                                        currentActiveVcFriends.Clear();
                                        currentVcName = "";
                                        currentVcId = "";
                                        return;
                                    }

                                    string myChannelId = myVcData.GetProperty("id").GetString();
                                    string myChannelName = myVcData.GetProperty("name").GetString();
                                    currentVcName = myChannelName;
                                    currentVcId = myChannelId;
                                    currentServerName = myVcData.TryGetProperty("server_name", out JsonElement srvElem) ? srvElem.GetString() : "";
                                    currentPerspectiveName = myVcData.TryGetProperty("user_name", out JsonElement usrElem) ? usrElem.GetString() : Environment.UserName;

                                    List<(string DisplayText, string SortName, bool IsConnected, string UserId, string RawName, bool IsApprovedByMe)> usersInMyVc = new List<(string, string, bool, string, string, bool)>();

                                    List<string> previousVcFriends = new List<string>(currentActiveVcFriends);
                                    currentActiveVcFriends.Clear();

                                    foreach (var prop in vcMapElement.EnumerateObject())
                                    {
                                        if (prop.Value.GetProperty("id").GetString() == myChannelId)
                                        {
                                            string displayName = prop.Value.TryGetProperty("user_name", out JsonElement nameElem) ? nameElem.GetString() : "Unknown User";
                                            bool isConnected = prop.Value.TryGetProperty("is_connected", out JsonElement connElem) && connElem.GetBoolean();
                                            string relationship = prop.Value.TryGetProperty("relationship", out JsonElement relElem) ? relElem.GetString() : "none";

                                            string prefix = "";
                                            bool isApprovedByMe = false;

                                            if (prop.Name != myId)
                                            {
                                                if (relationship == "mutual") { prefix = "✔️ "; isApprovedByMe = true; }
                                                else if (relationship == "outgoing") { prefix = "+ "; isApprovedByMe = true; }
                                                else if (relationship == "incoming") prefix = "- ";

                                                if (isConnected && isApprovedByMe)
                                                {
                                                    currentActiveVcFriends.Add($"{prop.Name} ({displayName})");
                                                }
                                            }

                                            usersInMyVc.Add((prefix + displayName, displayName, isConnected, prop.Name, displayName, isApprovedByMe));
                                        }
                                    }

                                    usersInMyVc.Sort((a, b) => a.SortName.CompareTo(b.SortName));

                                    var addedVc = currentActiveVcFriends.Except(previousVcFriends).ToList();
                                    var removedVc = previousVcFriends.Except(currentActiveVcFriends).ToList();

                                    if (myChannelId == previousVcId && !string.IsNullOrEmpty(myChannelId))
                                    {
                                        if (isConnectVCEnabled && addedVc.Count > 0) PlayConnectSound(isActivity: true);
                                        if (isDisconnectVCEnabled && removedVc.Count > 0) PlayDisconnectSound(isActivity: true);
                                        foreach (var f in addedVc) ShowNotification(ExtractNameFromFriendString(f), "Connected", "Connect");
                                        foreach (var f in removedVc) ShowNotification(ExtractNameFromFriendString(f), "Disconnected", "Disconnect");
                                    }
                                    previousVcId = myChannelId;

                                    CurrentVcText.Text = $"Current VC: {myChannelName}";
                                    CurrentVcText.Tag = new Tuple<string, string>(myChannelId, myChannelName);
                                    if (!ApprovedChannels.Any(c => c.Id == myChannelId))
                                    {
                                        CurrentVcText.Cursor = Cursors.Hand;
                                        CurrentVcText.ToolTip = "Click to add to approved channels";
                                    }
                                    else
                                    {
                                        CurrentVcText.Cursor = Cursors.Arrow;
                                        CurrentVcText.ToolTip = null;
                                    }
                                    VcUsersPanel.Children.Clear();

                                    foreach (var user in usersInMyVc)
                                    {
                                        StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent };
                                        TextBlock tb = new TextBlock { Text = user.DisplayText, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b9bbbe")), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };

                                        System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                                        dot.Fill = user.IsConnected ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f04747"));

                                        sp.Children.Add(tb);
                                        sp.Children.Add(dot);

                                        if (!user.IsApprovedByMe && user.UserId != myId)
                                        {
                                            sp.Cursor = Cursors.Hand;
                                            sp.ToolTip = "Click to add to approved users";
                                            sp.Tag = new Tuple<string, string>(user.UserId, user.RawName);
                                            sp.MouseLeftButtonDown += VcUser_MouseLeftButtonDown;
                                        }

                                        VcUsersPanel.Children.Add(sp);
                                    }

                                    CurrentVcText.Visibility = Visibility.Visible;
                                    VcUsersPanel.Visibility = Visibility.Visible;
                                });
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                Dispatcher.Invoke(() => {
                    ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f04747"));
                    ServerStatusText.Text = "Disconnected";
                    ServerStatusText.ToolTip = null;
                    CurrentVcText.Visibility = Visibility.Collapsed;
                    VcUsersPanel.Visibility = Visibility.Collapsed;
                    PlayDisconnectSound();
                    TriggerAutoReconnect();
                });
            }
        }

        private async void TriggerAutoReconnect()
        {
            if (!isSyncActive || isReconnecting) return;
            WriteLog("Connection lost. Attempting to reconnect...");
            isReconnecting = true;
            int currentDelay = 0;

            while (isSyncActive && isReconnecting)
            {
                if (currentDelay > 0)
                {
                    for (int i = currentDelay; i > 0; i--)
                    {
                        if (!isSyncActive || !isReconnecting) return;
                        Dispatcher.Invoke(() => { ServerStatusText.Text = $"Reconnecting in {i}s..."; });
                        await Task.Delay(1000);
                    }
                }

                if (!isSyncActive || !isReconnecting) return;

                try
                {
                    Dispatcher.Invoke(() => {
                        ServerStatusText.Text = "Connecting...";
                        ServerStatusDot.Fill = System.Windows.Media.Brushes.Orange;
                    });

                    webSocket = new ClientWebSocket();
                    wsCts = new CancellationTokenSource();
                    await webSocket.ConnectAsync(new Uri(GetServerUrl()), wsCts.Token);

                    isReconnecting = false;

                    string targetUrl = GetServerUrl();
                    bool isCustom = false;
                    Dispatcher.Invoke(() => isCustom = CustomServerCheck.IsChecked == true);

                    Dispatcher.Invoke(() => {
                        ServerStatusText.Text = "Connected";
                        ServerStatusText.ToolTip = null;
                        ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#43b581"));
                        PlayConnectSound();
                        ShowNotification(isCustom ? "Custom Server" : "Central Server", "Connected", "Connect");
                    });

                    if (isCustom)
                    {
                        WriteLog($"Successfully reconnected to custom server: {targetUrl} ({AppVersion})");
                    }
                    else
                    {
                        WriteLog($"Successfully reconnected to the central server. ({AppVersion})");
                    }

                    string tokenId = "";
                    string host = "unknown_host";
                    if (isCustom && Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri uri)) host = uri.Host;
                    string targetFolder = isCustom ? System.IO.Path.Combine(customTokensFolder, host) : appTokensFolder;
                    string tokenPath = System.IO.Path.Combine(targetFolder, ".token_id.json");
                    string appIdPath = System.IO.Path.Combine(targetFolder, "app_id.json");

                    activeAppUuid = appUuid;
                    if (File.Exists(tokenPath)) tokenId = File.ReadAllText(tokenPath).Trim('"', ' ', '\n', '\r');
                    if (File.Exists(appIdPath)) activeAppUuid = File.ReadAllText(appIdPath).Trim('"', ' ', '\n', '\r');

                    string currentDiscordId = DiscordIdInput.Text;
                    if (string.IsNullOrWhiteSpace(currentDiscordId) || string.IsNullOrWhiteSpace(tokenId))
                    {
                        Dispatcher.Invoke(() => StopListening());
                        return;
                    }

                    await SendWsMessage(new { action = "identify", discord_id = currentDiscordId, token_id = tokenId, app_uuid = activeAppUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList(), version = AppVersion });
                    AskServerToResolveNames();

                    _ = ReceiveMessages();
                    return;
                }
                catch
                {
                    Dispatcher.Invoke(() => {
                        ServerStatusText.Text = "Server Offline";
                        ServerStatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f04747"));
                    });

                    if (currentDelay == 0) currentDelay = 5;
                    else if (currentDelay < 30) currentDelay += 5;

                    WriteLog($"Reconnect attempt failed. Waiting {currentDelay} seconds before next attempt.");
                }
            }
        }

        private async void ServerStatus_Click(object sender, MouseButtonEventArgs e)
        {
            if (!isSyncActive) return;

            if (webSocket == null || webSocket.State != WebSocketState.Open)
            {
                isReconnecting = false;
                await ConnectToServer();
            }
        }

        private async void ShowHotkeyTakenWarning()
        {
            await ShowCustomDialog(
                "Hotkey Blocked by Windows",
                "Windows is blocking this Trigger Key because another app (like OBS, Medal, or Discord) is already using it!\n\n" +
                "To fix this, either:\n" +
                "1. Change your Trigger Key here to something else.\n" +
                "OR\n" +
                "2. Go into the conflicting app and change its keybind so this app can use it.");
        }

        private void TestTriggerKeyAvailability(string keyString)
        {
            if (ConnectButton.Content.ToString() == "Listening...") return;

            try
            {
                var converter = new KeyGestureConverter();
                KeyGesture triggerGesture = (KeyGesture)converter.ConvertFromString(keyString);
                HotkeyManager.Current.AddOrReplace("TestHook", triggerGesture.Key, triggerGesture.Modifiers, (s, e) => { });
                HotkeyManager.Current.Remove("TestHook");
            }
            catch (NHotkey.HotkeyAlreadyRegisteredException) { ShowHotkeyTakenWarning(); }
            catch { }
        }

        private bool CanTriggerClip()
        {
            int limit = 10;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (int.TryParse(RateLimitInput.Text, out int parsed)) limit = parsed;
            });

            if ((DateTime.Now - lastClipTime).TotalSeconds < limit)
            {
                WriteLog($"Clip ignored. Rate limit active ({limit}s cooldown).");
                return false;
            }

            lastClipTime = DateTime.Now;
            return true;
        }

        private async void OnClipTriggered(object sender, HotkeyEventArgs e)
        {
            if (!CanTriggerClip()) return;

            if (SendClipsCheck.IsChecked == true)
            {
                bool anyVc = true;
                Application.Current.Dispatcher.Invoke(() => { anyVc = RadioAnyVC.IsChecked ?? true; });
                bool isVcApproved = anyVc || (!string.IsNullOrEmpty(currentVcId) && ApprovedChannels.Any(c => c.Id == currentVcId));
                bool isInPool = !string.IsNullOrEmpty(activePoolCode);

                var combinedFriends = new HashSet<string>();
                if (isVcApproved)
                {
                    foreach (var f in currentActiveVcFriends) combinedFriends.Add(f);
                }
                if (isInPool)
                {
                    foreach (var f in currentActivePoolFriends) combinedFriends.Add(f);
                }

                if (isVcApproved || isInPool)
                {
                    string sentTo = combinedFriends.Count > 0 ? string.Join(", ", combinedFriends) : "nobody";
                    WriteLog($"Triggered a clip command. Sent to: {sentTo}");
                    await SendWsMessage(new { action = "trigger", user_id = DiscordIdInput.Text, app_uuid = activeAppUuid });

                    if (combinedFriends.Count > 0)
                    {
                        totalClipsStats.Sent++;
                        foreach (var friendStr in combinedFriends)
                        {
                            int spaceIdx = friendStr.IndexOf(' ');
                            if (spaceIdx > 0)
                            {
                                string id = friendStr.Substring(0, spaceIdx);
                                string fallbackName = friendStr.Substring(spaceIdx + 2, friendStr.Length - spaceIdx - 3);

                                var matchedUser = ApprovedUsers.FirstOrDefault(u => u.Id == id);
                                string trueName = matchedUser != null ? matchedUser.DisplayName : fallbackName;

                                if (!userSentStats.ContainsKey(id)) userSentStats[id] = new UserStatCount { Id = id, Name = trueName, Count = 0 };
                                userSentStats[id].Count++;
                                userSentStats[id].Name = trueName;
                            }
                        }
                        SaveStats();
                    }
                }
                else
                {
                    WriteLog($"Triggered a clip command. (Not in an approved VC or Pool)");
                }
            }
            else
            {
                WriteLog($"Triggered a clip command. (Network sending disabled)");
            }

            int delay = 0;
            Application.Current.Dispatcher.Invoke(() => { if (int.TryParse(ClipDelayInput.Text, out int parsed)) delay = parsed; });

            if (delay > 0)
            {
                WriteLog($"Clip delayed by {delay} seconds.");
                await Task.Delay(delay * 1000);
            }

            await PerformSafeHardwareClip();
            string myName = !string.IsNullOrEmpty(myGlobalDiscordName) ? myGlobalDiscordName : Environment.UserName;
            ShowNotification(myName, "Global Clipped", "Clip");
            SendRenamerTrigger(myName);
        }

        private async void OnLocalClipTriggered(object sender, HotkeyEventArgs e)
        {
            if (!CanTriggerClip()) return;

            WriteLog($"Triggered a local clip command.");

            int delay = 0;
            Application.Current.Dispatcher.Invoke(() => { if (int.TryParse(ClipDelayInput.Text, out int parsed)) delay = parsed; });

            if (delay > 0)
            {
                WriteLog($"Local clip delayed by {delay} seconds.");
                await Task.Delay(delay * 1000);
            }

            await PerformSafeHardwareClip();
            string myName = !string.IsNullOrEmpty(myGlobalDiscordName) ? myGlobalDiscordName : Environment.UserName;
            ShowNotification(myName, "Local Clipped", "Clip");
            SendRenamerTrigger(myName);
        }

        public async Task<bool> ReceiveNetworkClipCommand(string clipperName)
        {
            if (!CanTriggerClip()) return false;

            int delay = 0;
            Application.Current.Dispatcher.Invoke(() => { if (int.TryParse(ClipDelayInput.Text, out int parsed)) delay = parsed; });

            if (delay > 0)
            {
                WriteLog($"Network clip from {clipperName} delayed by {delay} seconds.");
                await Task.Delay(delay * 1000);
            }

            await PerformSafeHardwareClip();
            ShowNotification(clipperName, "Clipped", "Clip");
            SendRenamerTrigger(clipperName);
            return true;
        }

        private async void SendRenamerTrigger(string clipperName)
        {
            bool? isAutoRename = false;
            bool? useGame = false, usePersp = false, useClip = false, useVc = false, useServ = false, usePool = false;

            Application.Current.Dispatcher.Invoke(() => {
                isAutoRename = AutoRenameClipsCheck.IsChecked;
                useGame = RenameGameCheck.IsChecked;
                usePersp = RenamePerspectiveCheck.IsChecked;
                useClip = RenameClipperCheck.IsChecked;
                useVc = RenameVCCheck.IsChecked;
                useServ = RenameServerCheck.IsChecked;
                usePool = RenamePoolCheck.IsChecked;
            });

            if (isAutoRename != true) return;

            string renamerFolder = System.IO.Path.Combine(configFolder, "clip-management", "clip-renamer");
            if (!Directory.Exists(renamerFolder)) Directory.CreateDirectory(renamerFolder);

            string queuePath = System.IO.Path.Combine(renamerFolder, "clip_queue.txt");

            List<string> parts = new List<string>();

            if (useGame == true)
            {
                string gameName = GetActiveWindowTitle();
                if (!string.IsNullOrEmpty(gameName)) parts.Add($"g.{gameName}");
            }
            if (usePersp == true)
            {
                string pName = !string.IsNullOrEmpty(myGlobalDiscordName) ? myGlobalDiscordName :
                               (string.IsNullOrEmpty(currentPerspectiveName) ? Environment.UserName : currentPerspectiveName);
                parts.Add($"r.{pName}");
            }
            if (useClip == true && !string.IsNullOrEmpty(clipperName)) parts.Add($"c.{clipperName}");
            if (useVc == true && !string.IsNullOrEmpty(currentVcName)) parts.Add($"v.{currentVcName}");
            if (useServ == true && !string.IsNullOrEmpty(currentServerName)) parts.Add($"s.{currentServerName}");
            if (usePool == true && !string.IsNullOrEmpty(currentPoolName)) parts.Add($"p.{currentPoolName}");

            string finalPrefix1 = "Clip";
            string finalPrefix2 = "Saved";

            if (parts.Count >= 2)
            {
                finalPrefix1 = string.Join(" - ", parts.Take(parts.Count - 1));
                finalPrefix2 = parts.Last();
            }
            else if (parts.Count == 1)
            {
                finalPrefix1 = "Clip";
                finalPrefix2 = parts[0];
            }

            DateTime timeout = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < timeout)
            {
                try
                {
                    File.AppendAllText(queuePath, $"{finalPrefix1}|{finalPrefix2}{Environment.NewLine}");
                    break;
                }
                catch { await Task.Delay(500); }
            }
        }

        private async Task PerformSafeHardwareClip()
        {
            await WaitForAbsoluteZeroInput();
            PlayAlertSound();
            await InjectHardwareKeyAsync();
        }

        private async Task WaitForAbsoluteZeroInput()
        {
            bool isAnyKeyPressed = true;
            while (isAnyKeyPressed)
            {
                isAnyKeyPressed = false;
                for (int i = 0x01; i <= 0xFE; i++)
                {
                    if ((GetAsyncKeyState(i) & 0x8000) != 0)
                    {
                        isAnyKeyPressed = true;
                        break;
                    }
                }
                if (isAnyKeyPressed) { await Task.Delay(10); }
            }
        }

        private async Task InjectHardwareKeyAsync()
        {
            List<VirtualKeyCode> codesToPress = new List<VirtualKeyCode>();
            foreach (var keyStr in ClipKeysList)
            {
                if (Enum.TryParse(keyStr, out Key wpfKey))
                {
                    codesToPress.Add((VirtualKeyCode)KeyInterop.VirtualKeyFromKey(wpfKey));
                }
            }

            if (codesToPress.Count == 0) return;

            try
            {
                foreach (var vk in codesToPress) { simulator.Keyboard.KeyDown(vk); await Task.Delay(20); }
                await Task.Delay(50);
            }
            finally
            {
                for (int i = codesToPress.Count - 1; i >= 0; i--) { simulator.Keyboard.KeyUp(codesToPress[i]); await Task.Delay(20); }
            }
        }

        // ==============================================================================
        // UI & UPDATER
        // ==============================================================================

        private void ResetNavBackgrounds()
        {
            var darkBg = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavHomeBtn.Background = darkBg;
            NavDiscordBtn.Background = darkBg;
            NavSettingsBtn.Background = darkBg;
            NavSoundsBtn.Background = darkBg;
            NavNotificationsBtn.Background = darkBg;
            NavExtrasBtn.Background = darkBg;
            NavLogsBtn.Background = darkBg;
            NavStatsBtn.Background = darkBg;
            NavUpdateBtn.Background = darkBg;
            NavHelpBtn.Background = darkBg;
        }

        private void SetTabConstraints(double minWidth)
        {
            this.MinWidth = minWidth;
            if (this.Width < minWidth) this.Width = minWidth;
        }

        private void NavHomeBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 0;
            ResetNavBackgrounds();
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(570);
        }

        private void NavDiscordBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 1;
            ResetNavBackgrounds();
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(570);
        }

        private void NavSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 2;
            ResetNavBackgrounds();
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(720);
        }

        private void NavSoundsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 3;
            ResetNavBackgrounds();
            NavSoundsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(770);
        }

        private void NavNotificationsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 4;
            ResetNavBackgrounds();
            NavNotificationsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(670);
        }

        private void NavExtrasBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 5;
            ResetNavBackgrounds();
            NavExtrasBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(700);
        }

        private void NavLogsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 6;
            ResetNavBackgrounds();
            NavLogsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(570);

            LogOutputText.Dispatcher.BeginInvoke(new Action(() => {
                LogOutputText.CaretIndex = LogOutputText.Text.Length;
                LogOutputText.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void NavStatsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 7;
            ResetNavBackgrounds();
            NavStatsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(670);
            UpdateStatsUI();
        }

        private async void NavUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 8;
            ResetNavBackgrounds();
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavUpdateBtn.Content = "Update";
            SetTabConstraints(570);

            await CheckForUpdatesAsync(false);
        }

        private void NavHelpBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 9;
            ResetNavBackgrounds();
            NavHelpBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            SetTabConstraints(720);
        }

        private string logFilePath => System.IO.Path.Combine(configFolder, "app.log");

        private void WriteLog(string message)
        {
            if (!isLoaded) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] {message}";

            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (EnableLoggingCheck.IsChecked == true)
                    {
                        if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);

                        List<string> lines = new List<string>();
                        if (File.Exists(logFilePath))
                            lines = File.ReadAllLines(logFilePath).ToList();

                        lines.Add(logEntry);

                        int maxLines = 1000;
                        if (int.TryParse(MaxLogLinesInput.Text, out int parsed)) maxLines = parsed;

                        if (lines.Count > maxLines)
                            lines = lines.Skip(lines.Count - maxLines).ToList();

                        File.WriteAllLines(logFilePath, lines);
                    }

                    LogOutputText.AppendText(logEntry + Environment.NewLine);
                    LogOutputText.ScrollToEnd();
                }
                catch { }
            });
        }

        private void LoadLogFile()
        {
            try
            {
                if (File.Exists(logFilePath))
                {
                    LogOutputText.Text = File.ReadAllText(logFilePath) + (File.ReadAllText(logFilePath).EndsWith(Environment.NewLine) ? "" : Environment.NewLine);
                    LogOutputText.ScrollToEnd();
                }
            }
            catch { }
        }

        private void EnableLoggingCheck_Click(object sender, RoutedEventArgs e)
        {
            LogLinesPanel.Visibility = (EnableLoggingCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            e.Handled = true;
        }

        private async void AutoRestartObsCheck_Click(object sender, RoutedEventArgs e)
        {
            if (AutoRestartObsCheck.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ObsLocationInput.Text) || ObsLocationInput.Text == "Waiting for selection...")
                {
                    await ShowCustomDialog("Missing OBS Location", "Please set your OBS Location first before enabling the OBS Auto Restart feature.");
                    AutoRestartObsCheck.IsChecked = false;
                    return;
                }
            }

            ObsIntervalPanel.Visibility = (AutoRestartObsCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
            ToggleObsWatchdog();
        }

        private async void BrowseClipLocationBtn_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = ClipLocationInput.Text;
            string clipManagementFolder = System.IO.Path.Combine(configFolder, "clip-management");
            if (!Directory.Exists(clipManagementFolder)) Directory.CreateDirectory(clipManagementFolder);

            string psCode = @"
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = 'Select your Clip Recording Location'
$result = $dialog.ShowDialog()
if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
    Set-Content -Path '" + clipManagementFolder + @"\selected_path.txt' -Value $dialog.SelectedPath
} else {
    Set-Content -Path '" + clipManagementFolder + @"\selected_path.txt' -Value 'CANCELLED'
}
";
            string psPath = System.IO.Path.Combine(clipManagementFolder, "browse.ps1");
            string resultPath = System.IO.Path.Combine(clipManagementFolder, "selected_path.txt");
            if (File.Exists(resultPath)) File.Delete(resultPath);
            File.WriteAllText(psPath, psCode);

            string launcherVbs = System.IO.Path.Combine(clipManagementFolder, "launch_browse.vbs");
            string vbsCode = $@"CreateObject(""WScript.Shell"").Run ""powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File """"{psPath}"""""", 0, False";
            File.WriteAllText(launcherVbs, vbsCode);
            Process.Start("explorer.exe", $"\"{launcherVbs}\"");

            ClipLocationInput.Text = "Waiting for selection...";

            while (!File.Exists(resultPath)) { await Task.Delay(500); }

            string selected = File.ReadAllText(resultPath).Trim();
            if (selected != "CANCELLED" && !string.IsNullOrEmpty(selected))
            {
                ClipLocationInput.Text = selected;
                SaveSettings();
                WriteLog($"Clip Recording Location set to: {selected}");
                ToggleRenamerService();
            }
            else
            {
                ClipLocationInput.Text = currentPath;
            }
        }

        private void ToggleRenamerService()
        {
            string renamerFolder = System.IO.Path.Combine(configFolder, "clip-management", "clip-renamer");
            if (!Directory.Exists(renamerFolder)) Directory.CreateDirectory(renamerFolder);

            string triggerPath = System.IO.Path.Combine(renamerFolder, "exit_trigger.txt");
            string statusPath = System.IO.Path.Combine(renamerFolder, "renamer_status.txt");

            renamerStatusCts?.Cancel();

            if (File.Exists(triggerPath))
            {
                DateTime timeout = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < timeout)
                {
                    try
                    {
                        File.WriteAllText(triggerPath, "EXIT");
                        break;
                    }
                    catch { System.Threading.Thread.Sleep(500); }
                }
                System.Threading.Thread.Sleep(1000);
            }

            bool shouldRun = AutoRenameClipsCheck.IsChecked == true;
            string clipFolder = ClipLocationInput.Text;

            if (!shouldRun || string.IsNullOrEmpty(clipFolder)) return;

            try
            {
                var queueFiles = Directory.GetFiles(renamerFolder, "*queue*.txt");
                foreach (var file in queueFiles)
                {
                    File.Delete(file);
                }
            }
            catch { }

            File.WriteAllText(triggerPath, "");
            File.WriteAllText(statusPath, "");

            string renamerExe = System.IO.Path.Combine(renamerFolder, "ClipRenamer.exe");

            bool needsExtraction = false;
            if (!File.Exists(renamerExe) || needsRenamerUpdate)
            {
                needsExtraction = true;
                needsRenamerUpdate = false;
            }

            if (needsExtraction)
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    var resourceName = System.Linq.Enumerable.FirstOrDefault(assembly.GetManifestResourceNames(), n => n.EndsWith("ClipRenamer.exe"));

                    if (resourceName != null)
                    {
                        using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                        using (FileStream fileStream = new FileStream(renamerExe, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                        WriteLog("Successfully unpacked ClipRenamer.exe.");
                    }
                    else
                    {
                        WriteLog("Error: ClipRenamer.exe was not found in the embedded resources. Please report on Github!! (compiler error!)");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"Error unpacking ClipRenamer.exe: {ex.Message}");
                    return;
                }
            }

            string launcherVbs = System.IO.Path.Combine(renamerFolder, "launch_renamer.vbs");

            string vbsCode = $@"
Set objShell = CreateObject(""WScript.Shell"")
objShell.Run Chr(34) & ""{renamerExe}"" & Chr(34) & "" "" & Chr(34) & ""{renamerFolder}"" & Chr(34) & "" "" & Chr(34) & ""{clipFolder}"" & Chr(34), 0, False
";
            File.WriteAllText(launcherVbs, vbsCode);

            Process.Start("explorer.exe", $"\"{launcherVbs}\"");
            WriteLog("ClipRenamer.exe started.");

            renamerStatusCts = new CancellationTokenSource();
            _ = WatchRenamerStatusAsync(statusPath, renamerStatusCts.Token);
        }

        private async Task WatchRenamerStatusAsync(string statusFile, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(statusFile))
                    {
                        string content = "";
                        using (var fs = new FileStream(statusFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        using (var reader = new StreamReader(fs))
                        {
                            content = reader.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(content))
                            {
                                fs.SetLength(0);
                            }
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            if (content == "FOUND")
                                WriteLog("Found new clip. Attempting to modify...");
                            else if (content == "TIMEOUT")
                                WriteLog("Clip renaming timed out. No new accessible file found in the recording location.");
                            else if (content.StartsWith("RENAMED|"))
                            {
                                string newName = content.Substring(8);
                                WriteLog($"Successfully renamed new clip to: {newName}");
                            }
                        }
                    }
                }
                catch { }

                await Task.Delay(500, token);
            }
        }

        private async void BrowseObsBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "OBS Executable (obs64.exe)|obs64.exe";

            if (openFileDialog.ShowDialog() == true)
            {
                if (openFileDialog.FileName.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase))
                {
                    ObsLocationInput.Text = openFileDialog.FileName;
                    SaveSettings();
                    WriteLog($"OBS Location manually set to: {openFileDialog.FileName}");
                }
                else
                {
                    await ShowCustomDialog("Invalid File", "Please select a valid obs64.exe file.");
                }
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private string GetObsPath()
        {
            string savedPath = "";
            Application.Current.Dispatcher.Invoke(() => { savedPath = ObsLocationInput.Text; });

            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath) && savedPath.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase))
            {
                return savedPath;
            }

            string detectedPath = null;
            var runningObs = Process.GetProcessesByName("obs64");
            if (runningObs.Length > 0)
            {
                try { detectedPath = runningObs[0].MainModule.FileName; } catch { }
            }

            if (string.IsNullOrEmpty(detectedPath))
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio"))
                    {
                        if (key != null)
                        {
                            string path = key.GetValue("") as string;
                            if (!string.IsNullOrEmpty(path))
                            {
                                string exePath = System.IO.Path.Combine(path, @"bin\64bit\obs64.exe");
                                if (File.Exists(exePath)) detectedPath = exePath;
                            }
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(detectedPath))
            {
                string[] commonPaths = {
                    @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                    @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe",
                    @"C:\Program Files (x86)\Steam\steamapps\common\OBS Studio\bin\64bit\obs64.exe",
                    @"C:\SteamLibrary\steamapps\common\OBS Studio\bin\64bit\obs64.exe",
                    @"D:\SteamLibrary\steamapps\common\OBS Studio\bin\64bit\obs64.exe"
                };

                foreach (var p in commonPaths)
                {
                    if (File.Exists(p)) { detectedPath = p; break; }
                }
            }

            if (!string.IsNullOrEmpty(detectedPath))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ObsLocationInput.Text = detectedPath;
                    SaveSettings();
                });
                return detectedPath;
            }

            return null;
        }

        private async Task EnforceObsStartOnLaunch()
        {
            if (AutoStartObsCheck.IsChecked != true) return;

            string obsPath = GetObsPath();
            if (string.IsNullOrEmpty(obsPath) || !File.Exists(obsPath))
            {
                WriteLog("Could not locate OBS executable.");
                return;
            }

            string obsDir = System.IO.Path.GetDirectoryName(obsPath);
            var obsProcesses = Process.GetProcessesByName("obs64");

            if (obsProcesses.Length > 0)
            {
                WriteLog("OBS is already running. Skipping launch.");
                return;
            }
            else
            {
                WriteLog("Launching OBS with replay enabled.");
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string sentinelFile = System.IO.Path.Combine(appData, @"obs-studio\.sentinel");
                string safeModeFile = System.IO.Path.Combine(appData, @"obs-studio\safe_mode");

                if (Directory.Exists(sentinelFile)) { try { Directory.Delete(sentinelFile, true); } catch { } }
                if (File.Exists(safeModeFile)) { try { File.Delete(safeModeFile); } catch { } }

                string obsManagementFolder = System.IO.Path.Combine(configFolder, "obs-management");
                string vbsPath = System.IO.Path.Combine(obsManagementFolder, "launch_obs.vbs");
                string vbsCode = $@"
Set objShell = CreateObject(""WScript.Shell"")
objShell.CurrentDirectory = ""{obsDir}""
objShell.Run Chr(34) & ""{obsPath}"" & Chr(34) & "" --startreplaybuffer --minimize-to-tray --disable-shutdown-check"", 1, False
";
                try
                {
                    if (!Directory.Exists(obsManagementFolder)) Directory.CreateDirectory(obsManagementFolder);
                    File.WriteAllText(vbsPath, vbsCode);
                    Process.Start("explorer.exe", $"\"{vbsPath}\"");
                }
                catch (Exception ex) { WriteLog($"Auto Start OBS failed: {ex.Message}"); }
            }
        }

        private void ToggleObsWatchdog()
        {
            if (AutoRestartObsCheck.IsChecked == true)
            {
                if (obsWatchdogCts == null || obsWatchdogCts.IsCancellationRequested)
                {
                    obsWatchdogCts = new CancellationTokenSource();
                    _ = RunObsWatchdogAsync(obsWatchdogCts.Token);
                }
            }
            else
            {
                obsWatchdogCts?.Cancel();
            }
        }

        private async Task RunObsWatchdogAsync(CancellationToken token)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string sentinelFile = System.IO.Path.Combine(appData, @"obs-studio\.sentinel");
            string logDir = System.IO.Path.Combine(appData, @"obs-studio\logs");

            string[] crashKeywords = {
                "amf_encode_tex: SubmitInput timed out", "AMF_INPUT_FULL", "amf_encode_tex: Failed to create texture",
                "AMF_DIRECTX_FAILED", "AMF_NOT_FOUND", "Error encoding with encoder 'amd_amf",
                "DXGI_ERROR_DEVICE_REMOVED", "DXGI_ERROR_DEVICE_RESET", "DXGI_ERROR_DEVICE_HUNG",
                "Device Removed Reason", "Device Remove/Reset", "Failed to create texture: 0x887A0005",
                "Failed to create buffer", "Texture->Map failed", "GetDeviceRemovedReason failed",
                "Bad NV12 texture handling detected", "Encoder error, disconnecting", "Error encoding with encoder",
                "Video stopped, number of skipped frames due to encoding lag", "Starting the output failed",
                "nvenc failed", "nvenc_encode: Error", "Error encoding: Unknown error occurred"
            };

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var obsProcesses = Process.GetProcessesByName("obs64");
                    if (obsProcesses.Length > 0 && Directory.Exists(logDir))
                    {
                        string detectedObsPath = null;
                        string detectedObsDir = null;

                        try
                        {
                            detectedObsPath = obsProcesses[0].MainModule.FileName;
                            detectedObsDir = System.IO.Path.GetDirectoryName(detectedObsPath);
                        }
                        catch { }

                        var latestLog = new DirectoryInfo(logDir).GetFiles("*.txt")
                                        .OrderByDescending(f => f.LastWriteTime)
                                        .FirstOrDefault();

                        if (latestLog != null)
                        {
                            bool needsRestart = false;

                            using (var fs = new FileStream(latestLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var sr = new StreamReader(fs))
                            {
                                var allLines = (await sr.ReadToEndAsync()).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                var recentLines = allLines.Skip(Math.Max(0, allLines.Length - 50));

                                foreach (var line in recentLines)
                                {
                                    if (crashKeywords.Any(k => line.Contains(k)))
                                    {
                                        needsRestart = true;
                                        break;
                                    }
                                }
                            }

                            if (needsRestart)
                            {
                                if (string.IsNullOrEmpty(detectedObsPath) || !File.Exists(detectedObsPath))
                                {
                                    WriteLog("OBS crash detected, but could not reliably determine the OBS installation path. Aborting restart to prevent leaving OBS closed.");
                                }
                                else
                                {
                                    WriteLog($"OBS crash detected in log file. Attempting to restart OBS from: {detectedObsPath}");
                                    foreach (var p in Process.GetProcessesByName("obs64")) { try { p.Kill(); } catch { } }
                                    foreach (var p in Process.GetProcessesByName("obs-ffmpeg-mux")) { try { p.Kill(); } catch { } }

                                    await Task.Delay(5000, token);

                                    if (Directory.Exists(sentinelFile))
                                    {
                                        try { Directory.Delete(sentinelFile, true); } catch { }
                                    }

                                    string safeModeFile = System.IO.Path.Combine(appData, @"obs-studio\safe_mode");
                                    if (File.Exists(safeModeFile))
                                    {
                                        try { File.Delete(safeModeFile); } catch { }
                                    }

                                    try { File.Move(latestLog.FullName, latestLog.FullName + ".crashed"); } catch { }

                                    string obsManagementFolder = System.IO.Path.Combine(configFolder, "obs-management");
                                    string vbsPath = System.IO.Path.Combine(obsManagementFolder, "launch_obs.vbs");
                                    string vbsCode = $@"
Set objShell = CreateObject(""WScript.Shell"")
objShell.CurrentDirectory = ""{detectedObsDir}""
objShell.Run Chr(34) & ""{detectedObsPath}"" & Chr(34) & "" --startreplaybuffer --minimize-to-tray --disable-shutdown-check"", 1, False
";
                                    try
                                    {
                                        if (!Directory.Exists(obsManagementFolder)) Directory.CreateDirectory(obsManagementFolder);
                                        File.WriteAllText(vbsPath, vbsCode);

                                        Process.Start("explorer.exe", $"\"{vbsPath}\"");
                                        WriteLog("Successfully restarted OBS.");
                                    }
                                    catch (Exception ex)
                                    {
                                        WriteLog($"Failed to restart OBS: {ex.Message}");
                                    }

                                    await Task.Delay(10000, token);
                                }
                            }
                        }
                    }
                }
                catch { }

                int currentInterval = 5000;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (int.TryParse(ObsIntervalInput.Text, out int parsed) && parsed > 0)
                        currentInterval = parsed * 1000;
                });

                await Task.Delay(currentInterval, token);
            }
        }

        private async Task CheckForUpdatesAsync(bool isSilentStartup = false)
        {
            if (!isSilentStartup)
            {
                UpdateStatusText.Text = "Checking GitHub for updates...";
                UpdateStatusText.Foreground = Brushes.LightGray;
                PerformUpdateBtn.Visibility = Visibility.Collapsed;
                ReleaseNotesTitle.Visibility = Visibility.Collapsed;
                ReleaseNotesText.Visibility = Visibility.Collapsed;
            }

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ClipSync-Updater");
                    string url = "https://api.github.com/repos/probablyoxy/Clipping-Tools/releases/latest";

                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            string latestVersion = doc.RootElement.GetProperty("tag_name").GetString();

                            string releaseNotes = doc.RootElement.TryGetProperty("body", out JsonElement bodyElement)
                                ? bodyElement.GetString() : "No release notes provided.";

                            if (latestVersion != AppVersion)
                            {
                                downloadUrlForUpdate = doc.RootElement.GetProperty("assets")[0].GetProperty("browser_download_url").GetString();

                                if (isSilentStartup)
                                {
                                    if (AutoUpdateCheck.IsChecked == true)
                                    {
                                        PerformUpdateBtn_Click(null, null);
                                    }
                                    else
                                    {
                                        NavUpdateBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff69b4"));
                                        NavUpdateBtn.Content = "Update Available!!";
                                        NavUpdateBtn.Foreground = Brushes.White;
                                    }
                                }
                                else
                                {
                                    LatestVersionText.Text = latestVersion;
                                    UpdateStatusText.Text = "A new update is available!";
                                    UpdateStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581"));
                                    PerformUpdateBtn.Visibility = Visibility.Visible;

                                    ReleaseNotesText.Text = releaseNotes;
                                    ReleaseNotesTitle.Visibility = Visibility.Visible;
                                    ReleaseNotesText.Visibility = Visibility.Visible;
                                }
                            }
                            else if (!isSilentStartup)
                            {
                                LatestVersionText.Text = latestVersion;
                                UpdateStatusText.Text = "You are on the latest version.";

                                ReleaseNotesText.Text = releaseNotes;
                                ReleaseNotesTitle.Visibility = Visibility.Visible;
                                ReleaseNotesText.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    else if (!isSilentStartup)
                    {
                        UpdateStatusText.Text = "Could not find any releases on GitHub.";
                        LatestVersionText.Text = "Unknown";
                    }
                }
            }
            catch
            {
                if (!isSilentStartup)
                {
                    UpdateStatusText.Text = "Network error while checking for updates.";
                    UpdateStatusText.Foreground = Brushes.IndianRed;
                }
            }
        }

        private async void PerformUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            WriteLog($"Starting download for update {LatestVersionText.Text}.");
            PerformUpdateBtn.IsEnabled = false;
            PerformUpdateBtn.Content = "Downloading...";
            UpdateProgressBar.Visibility = Visibility.Visible;

            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                string tempExePath = System.IO.Path.Combine(configFolder, "update_temp.exe");
                string updaterBatPath = System.IO.Path.Combine(configFolder, "updater.bat");

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrlForUpdate, HttpCompletionOption.ResponseHeadersRead);
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = totalBytes != -1;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        var isMoreToRead = true;
                        var totalRead = 0L;

                        do
                        {
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0) { isMoreToRead = false; }
                            else
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                totalRead += read;
                                if (canReportProgress) { UpdateProgressBar.Value = (double)totalRead / totalBytes * 100; }
                            }
                        } while (isMoreToRead);
                    }
                }

                PerformUpdateBtn.Content = "Installing...";

                string batCode = $@"
@echo off
timeout /t 2 /nobreak > NUL
move /y ""{tempExePath}"" ""{currentExePath}""
start """" ""{currentExePath}""
del ""%~f0""
";
                File.WriteAllText(updaterBatPath, batCode);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = updaterBatPath,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                await ShowCustomDialog("Update Failed", "Update failed: " + ex.Message);
                PerformUpdateBtn.IsEnabled = true;
                PerformUpdateBtn.Content = "Download and Update";
                UpdateProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void HotkeyInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);
            ModifierKeys modifiers = Keyboard.Modifiers;
            List<string> parts = new List<string>();

            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");

            if (key != Key.LeftCtrl && key != Key.RightCtrl &&
                key != Key.LeftAlt && key != Key.RightAlt &&
                key != Key.LeftShift && key != Key.RightShift &&
                key != Key.System)
            {
                parts.Add(key.ToString());
            }

            TextBox tb = sender as TextBox;
            tb.Text = string.Join("+", parts);
        }

        private async void HotkeyInput_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            if (key != Key.LeftCtrl && key != Key.RightCtrl &&
                key != Key.LeftAlt && key != Key.RightAlt &&
                key != Key.LeftShift && key != Key.RightShift &&
                key != Key.System)
            {
                TextBox tb = sender as TextBox;
                if ((tb == TriggerKeyInput || tb == LocalTriggerKeyInput) && IsKeybindCollision(tb.Text, ClipKeysList))
                {
                    tb.Text = "";
                    await ShowCustomDialog("Keybind Collision", "You cannot set a Trigger Keybind to be the exact same as the Software Clip Keybind!");
                }

                Keyboard.ClearFocus();
                SaveSettings();

                if (ConnectButton.Content.ToString() == "Listening...")
                {
                    StartListening();
                }
                else
                {
                    if (tb == TriggerKeyInput && !string.IsNullOrEmpty(tb.Text)) TestTriggerKeyAvailability(tb.Text);
                }
            }
        }

        private void RadioVCRules_Changed(object sender, RoutedEventArgs e)
        {
            if (ChannelListContainer == null) return;

            if (RadioSpecificVC.IsChecked == true)
            {
                ChannelListContainer.IsEnabled = true;
                ChannelListContainer.Opacity = 1.0;
            }
            else
            {
                ChannelListContainer.IsEnabled = false;
                ChannelListContainer.Opacity = 0.5;
            }

            SaveSettings();
        }

        private void NewUserIdInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ProcessAddUsers(); }
        private void AddUserBtn_Click(object sender, RoutedEventArgs e) { ProcessAddUsers(); }

        private void ProcessAddUsers()
        {
            var newIds = NewUserIdInput.Text.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool itemsAdded = false;

            foreach (var id in newIds)
            {
                if (!ApprovedUsers.Any(u => u.Id == id))
                {
                    ApprovedUsers.Add(new DiscordItem { Id = id, DisplayName = id });
                    WriteLog($"Added user {id} to Approved Users.");
                    itemsAdded = true;
                }
            }
            NewUserIdInput.Clear();
            SaveSettings();

            if (itemsAdded)
            {
                SyncApprovedUsersToServer();
                AskServerToResolveNames();
            }
        }

        private async void RemoveUserBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            DiscordItem itemToRemove = btn.DataContext as DiscordItem;
            if (itemToRemove != null)
            {
                bool result = await ShowCustomDialog("Confirm Removal", $"Are you sure you want to remove '{itemToRemove.DisplayName}'?", true);
                if (result)
                {
                    ApprovedUsers.Remove(itemToRemove);
                    WriteLog($"Removed user {itemToRemove.DisplayName} from Approved Users.");
                    SaveSettings();
                    SyncApprovedUsersToServer();
                }
            }
        }

        private async void SyncApprovedUsersToServer()
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                var payload = new { action = "update_users", approved_users = ApprovedUsers.Select(u => u.Id).ToList() };
                await SendWsMessage(payload);
            }
        }

        private async void AskServerToResolveNames()
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                var usersToResolve = ApprovedUsers.Select(u => u.Id).ToList();
                if (!string.IsNullOrEmpty(DiscordIdInput.Text)) usersToResolve.Add(DiscordIdInput.Text);

                var payload = new
                {
                    action = "resolve_ids",
                    client_id = DiscordIdInput.Text,
                    users = usersToResolve,
                    channels = ApprovedChannels.Select(c => c.Id).ToList()
                };
                await SendWsMessage(payload);
            }
        }

        private void NewChannelIdInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ProcessAddChannels(); }
        private void AddChannelBtn_Click(object sender, RoutedEventArgs e) { ProcessAddChannels(); }

        private void ProcessAddChannels()
        {
            var newIds = NewChannelIdInput.Text.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool itemsAdded = false;

            foreach (var id in newIds)
            {
                if (!ApprovedChannels.Any(c => c.Id == id))
                {
                    ApprovedChannels.Add(new DiscordItem { Id = id, DisplayName = id });
                    WriteLog($"Added channel {id} to Approved Channels.");
                    itemsAdded = true;
                }
            }
            NewChannelIdInput.Clear();
            SaveSettings();

            if (itemsAdded) { AskServerToResolveNames(); }
        }

        private async void RemoveChannelBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            DiscordItem itemToRemove = btn.DataContext as DiscordItem;
            if (itemToRemove != null)
            {
                bool result = await ShowCustomDialog("Confirm Removal", $"Are you sure you want to remove '{itemToRemove.DisplayName}'?", true);
                if (result)
                {
                    ApprovedChannels.Remove(itemToRemove);
                    WriteLog($"Removed channel {itemToRemove.DisplayName} from Approved Channels.");
                    SaveSettings();
                }
            }
        }

        // ==============================================================================
        // OVERLAY MENU LOGIC (SEARCH & FILTER)
        // ==============================================================================

        private async void OpenSelectUsersBtn_Click(object sender, RoutedEventArgs e)
        {
            if (webSocket == null || webSocket.State != WebSocketState.Open)
            {
                await ShowCustomDialog("Not Connected", "You must be connected to the network to view and search for users.\n\nPlease go to the Home tab and click 'Activate Syncing' first.");
                return;
            }

            SelectUsersOverlay.Visibility = Visibility.Visible;
            SelectUsersTitle.Text = "Select Users (Fetching...)";
            SearchUsersInput.Text = "";
            AllVisibleUsers.Clear();
            RawFetchedUsers.Clear();

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchUsersInput.Focus();
                Keyboard.Focus(SearchUsersInput);
            }), System.Windows.Threading.DispatcherPriority.Input);

            await SendWsMessage(new { action = "get_all_users", client_id = DiscordIdInput.Text });
        }

        private void CloseSelectUsersBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectUsersOverlay.Visibility = Visibility.Collapsed;
        }

        private void SubmitSelectedUsersBtn_Click(object sender, RoutedEventArgs e)
        {
            bool itemsAdded = false;
            foreach (DiscordItem item in RawFetchedUsers.Where(u => u.IsSelected))
            {
                if (!ApprovedUsers.Any(u => u.Id == item.Id))
                {
                    ApprovedUsers.Add(new DiscordItem { Id = item.Id, DisplayName = item.DisplayName });
                    itemsAdded = true;
                }
            }

            if (itemsAdded)
            {
                SaveSettings();
                SyncApprovedUsersToServer();
                AskServerToResolveNames();
            }

            SelectUsersOverlay.Visibility = Visibility.Collapsed;
        }

        private void SearchUsersInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterUserList();
        }

        private void FilterUserList()
        {
            string searchText = SearchUsersInput.Text.ToLower();
            AllVisibleUsers.Clear();

            var approvedIds = new HashSet<string>(ApprovedUsers.Select(u => u.Id));

            foreach (var user in RawFetchedUsers)
            {
                if (approvedIds.Contains(user.Id)) continue;

                if (string.IsNullOrWhiteSpace(searchText) ||
                    user.DisplayName.ToLower().Contains(searchText) ||
                    user.Id.Contains(searchText))
                {
                    AllVisibleUsers.Add(user);
                }
            }

            SelectUsersTitle.Text = $"Select Users ({AllVisibleUsers.Count} found)";
        }

        private string pendingAddChannelId = "";
        private string pendingAddChannelName = "";

        private void CurrentVcText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (CurrentVcText.Tag is Tuple<string, string> channelData)
            {
                if (!ApprovedChannels.Any(c => c.Id == channelData.Item1))
                {
                    pendingAddChannelId = channelData.Item1;
                    pendingAddChannelName = channelData.Item2;
                    AddChannelPromptText.Text = $"Add '{pendingAddChannelName}' to your approved channels list?";
                    AddChannelOverlay.Visibility = Visibility.Visible;
                }
            }
        }

        private void CloseAddChannelOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            AddChannelOverlay.Visibility = Visibility.Collapsed;
            pendingAddChannelId = "";
            pendingAddChannelName = "";
        }

        private void ConfirmAddChannelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(pendingAddChannelId) && !ApprovedChannels.Any(c => c.Id == pendingAddChannelId))
            {
                ApprovedChannels.Add(new DiscordItem { Id = pendingAddChannelId, DisplayName = pendingAddChannelName });
                WriteLog($"Added channel {pendingAddChannelName} to Approved Channels from VC status.");
                SaveSettings();
                AskServerToResolveNames();

                CurrentVcText.Cursor = Cursors.Arrow;
                CurrentVcText.ToolTip = null;
            }
            AddChannelOverlay.Visibility = Visibility.Collapsed;
            pendingAddChannelId = "";
            pendingAddChannelName = "";
        }

        private string pendingAddUserId = "";
        private string pendingAddUserName = "";

        private void VcUser_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is StackPanel sp && sp.Tag is Tuple<string, string> userData)
            {
                pendingAddUserId = userData.Item1;
                pendingAddUserName = userData.Item2;
                AddUserPromptText.Text = $"Add {pendingAddUserName} to your approved users list?";
                AddUserOverlay.Visibility = Visibility.Visible;
            }
        }

        private void CloseAddUserOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            AddUserOverlay.Visibility = Visibility.Collapsed;
            pendingAddUserId = "";
            pendingAddUserName = "";
        }

        private void ConfirmAddUserBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(pendingAddUserId) && !ApprovedUsers.Any(u => u.Id == pendingAddUserId))
            {
                ApprovedUsers.Add(new DiscordItem { Id = pendingAddUserId, DisplayName = pendingAddUserName });
                WriteLog($"Added user {pendingAddUserName} to Approved Users from VC.");
                SaveSettings();
                SyncApprovedUsersToServer();
                AskServerToResolveNames();
            }
            AddUserOverlay.Visibility = Visibility.Collapsed;
            pendingAddUserId = "";
            pendingAddUserName = "";
        }

        private async void JoinPoolBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PoolCodeInput.Text)) return;
            await SendWsMessage(new { action = "join_pool", pool_code = PoolCodeInput.Text.Trim().ToUpper() });
        }

        private async void CreatePoolBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = string.IsNullOrWhiteSpace(PoolNameInput.Text) ? "Unnamed Pool" : PoolNameInput.Text.Trim();
            await SendWsMessage(new { action = "create_pool", name = name });
        }

        private async void LeavePoolBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendWsMessage(new { action = "leave_pool" });
            ResetPoolUI();
        }

        private void ActivePoolCodeDisplay_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(activePoolCode))
            {
                Clipboard.SetText(activePoolCode);
                WriteLog("Pool code copied to clipboard.");
            }
        }

        private async void ClosePoolBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendWsMessage(new { action = "close_pool" });
            ResetPoolUI();
        }

        private async void TogglePoolBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendWsMessage(new { action = "toggle_pool" });
        }

        private string pendingPoolUserId = "";

        private void PoolUser_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is StackPanel sp && sp.Tag is Tuple<string, string> userData)
            {
                pendingPoolUserId = userData.Item1;
                PoolActionPromptText.Text = $"Manage {userData.Item2}";
                PoolActionOverlay.Visibility = Visibility.Visible;
            }
        }

        private void ClosePoolActionOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            PoolActionOverlay.Visibility = Visibility.Collapsed;
            pendingPoolUserId = "";
        }

        private async void PoolTransferBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(pendingPoolUserId)) await SendWsMessage(new { action = "pool_manage_user", target_id = pendingPoolUserId, manage_action = "transfer" });
            ClosePoolActionOverlayBtn_Click(null, null);
        }

        private async void PoolKickBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(pendingPoolUserId)) await SendWsMessage(new { action = "pool_manage_user", target_id = pendingPoolUserId, manage_action = "kick" });
            ClosePoolActionOverlayBtn_Click(null, null);
        }

        private async void PoolBanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(pendingPoolUserId)) await SendWsMessage(new { action = "pool_manage_user", target_id = pendingPoolUserId, manage_action = "ban" });
            ClosePoolActionOverlayBtn_Click(null, null);
        }

        private TaskCompletionSource<bool> _currentDialogTcs;

        private async Task<bool> ShowCustomDialog(string title, string message, bool isConfirmDialog = false)
        {
            GenericMessageTitle.Text = title;
            GenericMessageText.Text = message;

            if (isConfirmDialog)
            {
                GenericMessageCancelBtn.Visibility = Visibility.Visible;
                GenericMessageCancelBtn.Content = "No";
                GenericMessageOkBtn.Content = "Yes";
            }
            else
            {
                GenericMessageCancelBtn.Visibility = Visibility.Collapsed;
                GenericMessageOkBtn.Content = "OK";
            }

            GenericMessageOverlay.Visibility = Visibility.Visible;

            _currentDialogTcs = new TaskCompletionSource<bool>();
            return await _currentDialogTcs.Task;
        }

        private void GenericMessageOkBtn_Click(object sender, RoutedEventArgs e)
        {
            GenericMessageOverlay.Visibility = Visibility.Collapsed;
            _currentDialogTcs?.TrySetResult(true);
        }

        private void GenericMessageCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            GenericMessageOverlay.Visibility = Visibility.Collapsed;
            _currentDialogTcs?.TrySetResult(false);
        }

        private int uninstallStage = 1;

        private void UninstallAppBtn_Click(object sender, RoutedEventArgs e)
        {
            uninstallStage = 1;
            UninstallTitle.Text = "Uninstall";
            UninstallMessage.Text = "Are you sure you want to uninstall Clipping Tools?";
            UninstallStage2Panel.Visibility = Visibility.Collapsed;
            UninstallCheck1.IsChecked = false;
            UninstallCheckUserData.IsChecked = false;
            UninstallCheckUserData.Visibility = Visibility.Collapsed;
            UninstallConfirmBtn.Content = "Confirm";
            UninstallConfirmBtn.IsEnabled = true;
            UninstallOverlay.Visibility = Visibility.Visible;
        }

        private void UninstallCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            UninstallOverlay.Visibility = Visibility.Collapsed;
        }

        private void UninstallCheck1_Click(object sender, RoutedEventArgs e)
        {
            if (UninstallCheck1.IsChecked == true)
            {
                UninstallCheckUserData.Visibility = Visibility.Visible;
                UninstallConfirmBtn.IsEnabled = true;
            }
            else
            {
                UninstallCheckUserData.Visibility = Visibility.Collapsed;
                UninstallCheckUserData.IsChecked = false;
                UninstallConfirmBtn.IsEnabled = false;
            }
        }

        private void UninstallConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (uninstallStage == 1)
            {
                uninstallStage = 2;
                UninstallMessage.Text = "Are you really sure you want to uninstall Clipping Tools?";
                UninstallStage2Panel.Visibility = Visibility.Visible;
                UninstallConfirmBtn.Content = "Uninstall";
                UninstallConfirmBtn.IsEnabled = false;
            }
            else if (uninstallStage == 2)
            {
                PerformUninstall();
            }
        }

        private void PerformUninstall()
        {
            bool deleteUserData = UninstallCheckUserData.IsChecked == true;
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string batPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClippingToolsUninstall.cmd");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/delete /tn \"ClippingTools\" /f",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi)?.WaitForExit();
            }
            catch { }

            try
            {
                string startMenuShortcut = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clipping Tools.lnk");
                string desktopShortcut = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Clipping Tools.lnk");
                if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);
                if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
            }
            catch { }

            StringBuilder batContent = new StringBuilder();
            batContent.AppendLine("@echo off");
            batContent.AppendLine("timeout /t 5 /nobreak >nul");
            batContent.AppendLine($"taskkill /f /im \"{System.IO.Path.GetFileName(exePath)}\" >nul 2>&1");
            batContent.AppendLine("taskkill /f /im \"ClipRenamer.exe\" >nul 2>&1");

            if (deleteUserData)
            {
                batContent.AppendLine($"rmdir /s /q \"{configFolder}\"");
            }

            batContent.AppendLine($"del /f /q \"{exePath}\"");
            batContent.AppendLine("(goto) 2>nul & del \"%~f0\"");

            File.WriteAllText(batPath, batContent.ToString());

            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            forceExit = true;
            Application.Current.Shutdown();
        }
    }

    public class TotalClipsStat
    {
        public int Sent { get; set; } = 0;
        public int Received { get; set; } = 0;
    }

    public class UserStatCount
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Count { get; set; } = 0;
    }

    public class AppSettings
    {
        public bool EnableLogging { get; set; } = true;
        public int MaxLogLines { get; set; } = 1000;
        public bool UseCustomServer { get; set; } = false;
        public string CustomServerUrl { get; set; } = "";
        public string ObsPath { get; set; } = "";
        public string ClipPath { get; set; } = "";
        public bool AutoRenameClips { get; set; } = false;
        public bool RenameGame { get; set; } = true;
        public bool RenamePerspective { get; set; } = true;
        public bool RenameClipper { get; set; } = true;
        public bool RenameVC { get; set; } = true;
        public bool RenameServer { get; set; } = true;
        public bool RenamePool { get; set; } = true;
        public bool AutoStartObs { get; set; } = false;
        public bool AutoRestartObs { get; set; } = false;
        public int ObsCheckInterval { get; set; } = 5;
        public bool AutoUpdate { get; set; } = false;
        public bool EnsureMicMax { get; set; } = false;
        public bool SyncTimeOnLaunch { get; set; } = false;
        public bool StartMenuShortcut { get; set; } = false;
        public bool DesktopShortcut { get; set; } = false;
        public bool SendClips { get; set; } = true;
        public bool ReceiveClips { get; set; } = true;
        public bool AnyVCRule { get; set; } = true;
        public string TriggerKey { get; set; } = "Ctrl+Alt+F10";
        public string LocalTriggerKey { get; set; } = "Ctrl+Alt+F9";
        public string ControllerTriggerKey { get; set; } = "";
        public string ControllerLocalTriggerKey { get; set; } = "";
        public string WheelTriggerKey { get; set; } = "";
        public string WheelLocalTriggerKey { get; set; } = "";
        public List<string> ClipKeys { get; set; } = new List<string>();

        public double WindowLeft { get; set; } = -1;
        public double WindowTop { get; set; } = -1;
        public double WindowWidth { get; set; } = 780;
        public double WindowHeight { get; set; } = 640;

        public bool AutoSync { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public int RateLimitSeconds { get; set; } = 10;
        public int ClipDelaySeconds { get; set; } = 0;

        public bool EnableSound { get; set; } = true;
        public bool UseCustomSound { get; set; } = false;
        public string SystemSoundType { get; set; } = "SimpleyViridian - Clipped";
        public string CustomSoundFilename { get; set; } = "";

        public bool EnableConnectSound { get; set; } = true;
        public bool UseCustomConnectSound { get; set; } = false;
        public string SystemConnectSoundType { get; set; } = "ConfusedIndividual - Server Connect";
        public string CustomConnectSoundFilename { get; set; } = "";

        public bool EnableDisconnectSound { get; set; } = true;
        public bool UseCustomDisconnectSound { get; set; } = false;
        public string SystemDisconnectSoundType { get; set; } = "ConfusedIndividual - Server Disconnect";
        public string CustomDisconnectSoundFilename { get; set; } = "";

        public double ClipVolume { get; set; } = 1.0;
        public double ConnectVolume { get; set; } = 1.0;
        public double DisconnectVolume { get; set; } = 1.0;

        public bool EnableClipNotif { get; set; } = false;
        public bool EnableConnectNotif { get; set; } = false;
        public string ClipNotifLocation { get; set; } = "Bottom Right";
        public string ConnectNotifLocation { get; set; } = "Bottom Right";
        public string ClipNotifColor { get; set; } = "#5865F2";
        public bool ClipNotifFlipAccent { get; set; } = false;
        public bool ConnectNotifFlipAccent { get; set; } = false;
        public double ClipNotifTimeLimit { get; set; } = 3;
        public double ConnectNotifTimeLimit { get; set; } = 3;
        public int ClipNotifMonitorIndex { get; set; } = 0;
        public int ConnectNotifMonitorIndex { get; set; } = 0;

        public bool ConnectPoolActivity { get; set; } = false;
        public bool ConnectVCActivity { get; set; } = false;
        public bool DisconnectPoolActivity { get; set; } = false;
        public bool DisconnectVCActivity { get; set; } = false;

        public List<string> Channels { get; set; } = new List<string>();
        public List<string> Users { get; set; } = new List<string>();
    }

    public class DiscordItem : INotifyPropertyChanged
    {
        public string Id { get; set; }

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayName"));
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected"));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class AppVersionStat
    {
        public string Version { get; set; } = "";
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
    }
}
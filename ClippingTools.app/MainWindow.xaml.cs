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

        private EventWaitHandle singleInstanceWatcher;
        private bool isSingleInstance;

        private string appUuid = "";
        private InputSimulator simulator = new InputSimulator();
        private CancellationTokenSource obsWatchdogCts;
        private CancellationTokenSource renamerStatusCts;
        private MediaPlayer customAudioPlayer = new MediaPlayer();
        private System.Windows.Forms.NotifyIcon trayIcon;
        private bool forceExit = false;

        public ObservableCollection<DiscordItem> ApprovedUsers { get; set; } = new ObservableCollection<DiscordItem>();
        public ObservableCollection<DiscordItem> ApprovedChannels { get; set; } = new ObservableCollection<DiscordItem>();

        public ObservableCollection<DiscordItem> AllVisibleUsers { get; set; } = new ObservableCollection<DiscordItem>();
        private List<DiscordItem> RawFetchedUsers = new List<DiscordItem>();

        public List<string> ClipKeysList { get; set; } = new List<string>();

        private readonly string configFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
        private string configFilePath => System.IO.Path.Combine(configFolder, "settings.json");
        private string soundsFolder => System.IO.Path.Combine(configFolder, "sounds");

        private bool isLoaded = false;
        private DateTime lastClipTime = DateTime.MinValue;

        private TotalClipsStat totalClipsStats = new TotalClipsStat();
        private Dictionary<string, UserStatCount> userSentStats = new Dictionary<string, UserStatCount>();
        private Dictionary<string, UserStatCount> userReceivedStats = new Dictionary<string, UserStatCount>();

        // CHANGE WHEN UPDATE :)
        private const string AppVersion = "v0.1.7";
        private string downloadUrlForUpdate = "";

        private ClientWebSocket webSocket;
        private CancellationTokenSource wsCts;
        private bool isSyncActive = false;
        private bool isReconnecting = false;
        private List<string> currentActiveVcFriends = new List<string>();
        private string currentVcName = "";

        private List<string> currentActivePoolFriends = new List<string>();
        private string activePoolCode = "";
        private bool amIPoolOwner = false;

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

            LoadSettings();
            LoadStats();
            isLoaded = true;

            CheckAndUpdateVersion();

            CurrentVersionText.Text = AppVersion;

            SetupTrayIcon();

            this.Loaded += (s, e) =>
            {
                if (Environment.GetCommandLineArgs().Contains("--minimized"))
                {
                    this.Hide();
                }
            };

            if (AutoSyncCheck.IsChecked == true)
            {
                StartListening();
            }

            _ = CheckForUpdatesAsync(true);
            _ = EnforceObsStartOnLaunch();
            ToggleRenamerService();
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

        private void LoadSettings()
        {
            if (!Directory.Exists(soundsFolder)) Directory.CreateDirectory(soundsFolder);

            string[] builtInSounds = { "simpleyviridian-clipped.wav", "simpleyviridian-clippedteto.wav", "confusedindividual-clipped.mp3" };
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

            if (!File.Exists(configFilePath))
            {
                appUuid = Guid.NewGuid().ToString();
                ClipKeysList.Add("LeftAlt");
                ClipKeysList.Add("F10");
                RebuildClipKeyUI();
                return;
            }

            try
            {
                string json = File.ReadAllText(configFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);

                if (settings != null)
                {
                    if (string.IsNullOrEmpty(settings.AppUuid)) { settings.AppUuid = Guid.NewGuid().ToString(); }
                    appUuid = settings.AppUuid;

                    SendClipsCheck.IsChecked = settings.SendClips;
                    ReceiveClipsCheck.IsChecked = settings.ReceiveClips;
                    DiscordIdInput.Text = settings.DiscordId;
                    RadioAnyVC.IsChecked = settings.AnyVCRule;
                    RadioSpecificVC.IsChecked = !settings.AnyVCRule;
                    TriggerKeyInput.Text = settings.TriggerKey;

                    AutoSyncCheck.IsChecked = settings.AutoSync;
                    StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
                    RateLimitInput.Text = settings.RateLimitSeconds >= 0 ? settings.RateLimitSeconds.ToString() : "10";
                    ObsLocationInput.Text = settings.ObsPath;
                    ClipLocationInput.Text = settings.ClipPath;
                    AutoRenameClipsCheck.IsChecked = settings.AutoRenameClips;
                    AutoStartObsCheck.IsChecked = settings.AutoStartObs;
                    AutoRestartObsCheck.IsChecked = settings.AutoRestartObs;
                    ObsIntervalInput.Text = settings.ObsCheckInterval > 0 ? settings.ObsCheckInterval.ToString() : "5";
                    ObsIntervalPanel.Visibility = (AutoRestartObsCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

                    EnableLoggingCheck.IsChecked = settings.EnableLogging;
                    MaxLogLinesInput.Text = settings.MaxLogLines > 0 ? settings.MaxLogLines.ToString() : "1000";
                    LogLinesPanel.Visibility = (EnableLoggingCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

                    ToggleObsWatchdog();
                    LoadLogFile();

                    if (settings.ClipKeys != null && settings.ClipKeys.Count > 0)
                    {
                        ClipKeysList = settings.ClipKeys;
                    }

                    EnableSoundCheck.IsChecked = settings.EnableSound;
                    RadioCustomSound.IsChecked = settings.UseCustomSound;
                    RadioSystemSound.IsChecked = !settings.UseCustomSound;
                    CustomSoundPathInput.Text = settings.CustomSoundFilename;

                    foreach (ComboBoxItem item in SystemSoundCombo.Items)
                    {
                        if (item.Content.ToString() == settings.SystemSoundType)
                        {
                            SystemSoundCombo.SelectedItem = item;
                            break;
                        }
                    }

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

            RebuildClipKeyUI();

            if (string.IsNullOrEmpty(ObsLocationInput.Text))
            {
                Task.Run(() => GetObsPath());
            }
        }

        private void SaveSettings()
        {
            if (!isLoaded) return;

            if (IsKeybindCollision(TriggerKeyInput.Text, ClipKeysList))
            {
                TriggerKeyInput.Text = "";
            }

            if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);

            var settings = new AppSettings
            {
                AppUuid = appUuid,
                SendClips = SendClipsCheck.IsChecked ?? true,
                ReceiveClips = ReceiveClipsCheck.IsChecked ?? true,
                DiscordId = DiscordIdInput.Text,
                AnyVCRule = RadioAnyVC.IsChecked ?? true,
                TriggerKey = TriggerKeyInput.Text,
                ClipKeys = ClipKeysList,

                AutoSync = AutoSyncCheck.IsChecked ?? false,
                StartWithWindows = StartWithWindowsCheck.IsChecked ?? false,
                RateLimitSeconds = int.TryParse(RateLimitInput.Text, out int parsedLimit) && parsedLimit >= 0 ? parsedLimit : 10,
                ObsPath = ObsLocationInput.Text,
                ClipPath = ClipLocationInput.Text,
                AutoRenameClips = AutoRenameClipsCheck.IsChecked ?? false,
                AutoStartObs = AutoStartObsCheck.IsChecked ?? false,
                AutoRestartObs = AutoRestartObsCheck.IsChecked ?? false,
                ObsCheckInterval = int.TryParse(ObsIntervalInput.Text, out int parsedInterval) && parsedInterval > 0 ? parsedInterval : 5,

                EnableLogging = EnableLoggingCheck.IsChecked ?? true,
                MaxLogLines = int.TryParse(MaxLogLinesInput.Text, out int parsedMax) && parsedMax > 0 ? parsedMax : 1000,

                EnableSound = EnableSoundCheck.IsChecked ?? true,
                UseCustomSound = RadioCustomSound.IsChecked ?? false,
                SystemSoundType = (SystemSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Exclamation",
                CustomSoundFilename = CustomSoundPathInput.Text,

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
            // MIGRATION PIPELINE
            // ==========================================



            // --- Migration: v0.1.7 (Rename Executable) ---
            Version v017 = new Version(0, 1, 7);
            if (storedVersion < v017)
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (exePath.EndsWith(".app.exe", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog("Detected app running as old ClippingTools.app.exe. Renaming to ClippingTools.exe...");
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
                    if (StartWithWindowsCheck.IsChecked == true)
                    {
                        StartWithWindowsCheck_Click(null, null);
                        WriteLog("Migrated Start with Windows task.");
                    }

                    UpdateShortcuts(exePath);

                    currentStat.Version = "v0.1.7";
                    storedVersion = v017;
                    WriteVersionFile(currentStat);
                }
            }



            // --- Future Migrations Go Here ---





            if (storedVersion < appVersion)
            {
                currentStat.Version = AppVersion;
                WriteVersionFile(currentStat);
            }
        }

        private void UpdateShortcuts(string newExePath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                string startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string startShortcutPath = System.IO.Path.Combine(startMenuPath, "Clipping Tools.lnk");
                if (System.IO.File.Exists(startShortcutPath))
                {
                    dynamic startMenuShortcut = shell.CreateShortcut(startShortcutPath);
                    startMenuShortcut.TargetPath = newExePath;
                    startMenuShortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(newExePath);
                    startMenuShortcut.Save();
                    WriteLog("Migrated Start Menu shortcut.");
                }

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopShortcutPath = System.IO.Path.Combine(desktopPath, "Clipping Tools.lnk");
                if (System.IO.File.Exists(desktopShortcutPath))
                {
                    dynamic desktopShortcut = shell.CreateShortcut(desktopShortcutPath);
                    desktopShortcut.TargetPath = newExePath;
                    desktopShortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(newExePath);
                    desktopShortcut.Save();
                    WriteLog("Migrated Desktop shortcut.");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to migrate shortcuts: {ex.Message}");
            }
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

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (sender == AutoRenameClipsCheck && AutoRenameClipsCheck.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ClipLocationInput.Text) || ClipLocationInput.Text == "Waiting for selection...")
                {
                    MessageBox.Show("Please set your Clip Recording Location first before enabling the Clip Renaming feature.", "Missing Location", MessageBoxButton.OK, MessageBoxImage.Warning);
                    AutoRenameClipsCheck.IsChecked = false;
                    return;
                }
            }

            SaveSettings();
            if (sender == AutoRenameClipsCheck) ToggleRenamerService();
        }
        private void Setting_TextChanged(object sender, TextChangedEventArgs e) { SaveSettings(); }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            string renamerFolder = System.IO.Path.Combine(configFolder, "clip-management", "clip-renamer");
            string triggerPath = System.IO.Path.Combine(renamerFolder, "exit_trigger.txt");
            if (File.Exists(triggerPath)) File.WriteAllText(triggerPath, "EXIT");

            WriteVersionFile();

            if (!forceExit)
            {
                e.Cancel = true;
                this.Hide();
                SaveSettings();
            }
            else
            {
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

        // ==============================================================================
        // IMPORT, EXPORT, AND UUID MANAGEMENT
        // ==============================================================================

        private void ImportSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSyncActive)
            {
                MessageBox.Show("You cannot import settings while actively syncing. Please disconnect first.", "Sync Active", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                    if (testSettings == null || testSettings.AppUuid == null)
                    {
                        MessageBox.Show("Invalid settings file format.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    File.Copy(openFileDialog.FileName, configFilePath, true);
                    LoadSettings();
                    WriteLog("Successfully imported new settings from file.");
                    MessageBox.Show("Settings imported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to read the settings file: " + ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ExportSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "WARNING: This settings file contains your App UUID and Discord ID. Anyone with this file can connect as you and trigger clips on your behalf. Keep it safe!\n\nDo you want to proceed with exporting?",
                "Security Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
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
                        MessageBox.Show("Settings exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to export settings: " + ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void ResetUuidBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isSyncActive)
            {
                MessageBox.Show("You cannot reset your UUID while actively syncing. Please disconnect first.", "Sync Active", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "Are you sure you want to reset your App UUID? This will unlink your app from the network. You will need to reverify it via Discord DM on your next connection.",
                "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                appUuid = Guid.NewGuid().ToString();
                SaveSettings();
                WriteLog("App UUID reset.");
                MessageBox.Show("UUID has been reset. Please re-authenticate the next time you connect.", "UUID Reset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ==============================================================================
        // DISCORD OAUTH2 SIGN-IN LOGIC
        // ==============================================================================

        private async void DiscordSignInBtn_Click(object sender, RoutedEventArgs e)
        {
            string clientId = "1480703669555957791";
            string redirectUri = "http://127.0.0.1:5050/";

            DiscordSignInBtn.Content = "Awaiting Browser...";
            DiscordSignInBtn.IsEnabled = false;

            string authUrl = $"https://discord.com/api/oauth2/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=token&scope=identify";

            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

            await ListenForDiscordToken();
        }

        private async Task ListenForDiscordToken()
        {
            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add("http://127.0.0.1:5050/");
                try { listener.Start(); }
                catch
                {
                    MessageBox.Show("Could not start local server. Port 5050 might be in use.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetDiscordButton();
                    return;
                }

                while (true)
                {
                    var context = await listener.GetContextAsync();
                    var request = context.Request;
                    var response = context.Response;

                    if (request.Url.AbsolutePath == "/")
                    {
                        string html = @"
                            <html><body style='background:#36393f; color:white; font-family:sans-serif; text-align:center; padding-top:50px;'>
                            <h2>Completing Discord Sign-In...</h2>
                            <script>
                                if (window.location.hash) { window.location.href = '/token?' + window.location.hash.substring(1); } 
                                else { document.body.innerHTML = '<h2>Error: No token provided.</h2>'; }
                            </script>
                            </body></html>";

                        byte[] buffer = Encoding.UTF8.GetBytes(html);
                        response.ContentLength64 = buffer.Length;
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                        response.Close();
                    }
                    else if (request.Url.AbsolutePath == "/token")
                    {
                        string token = request.QueryString["access_token"];

                        string html = @"
                            <html><body style='background:#36393f; color:white; font-family:sans-serif; text-align:center; padding-top:50px;'>
                            <h2 style='color:#43b581'>Success!</h2><p>You can close this window and return to the app.</p>
                            <script>window.close();</script>
                            </body></html>";

                        byte[] buffer = Encoding.UTF8.GetBytes(html);
                        response.ContentLength64 = buffer.Length;
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                        response.Close();

                        listener.Stop();
                        await FetchDiscordProfile(token);
                        break;
                    }
                    else { response.Close(); }
                }
            }
        }

        private async Task FetchDiscordProfile(string token)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var response = await client.GetAsync("https://discord.com/api/users/@me");

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            string discordId = doc.RootElement.GetProperty("id").GetString();
                            Dispatcher.Invoke(() => {
                                DiscordIdInput.Text = discordId;
                                SaveSettings();
                                ResetDiscordButton();
                                WriteLog($"Successfully authenticated with Discord. ID: {discordId}");
                            });
                        }
                    }
                }
            }
            catch
            {
                Dispatcher.Invoke(() => {
                    MessageBox.Show("Failed to connect to Discord's servers.", "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetDiscordButton();
                });
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
                if (!Directory.Exists(soundsFolder)) Directory.CreateDirectory(soundsFolder);
                string ext = System.IO.Path.GetExtension(openFileDialog.FileName);
                string newFileName = "custom_" + DateTime.Now.Ticks + ext;
                string destPath = System.IO.Path.Combine(soundsFolder, newFileName);
                File.Copy(openFileDialog.FileName, destPath, true);
                CustomSoundPathInput.Text = newFileName;
                SaveSettings();
                PlayAlertSound(forcePlay: true);
            }
        }

        private void TestSoundBtn_Click(object sender, RoutedEventArgs e) { PlayAlertSound(forcePlay: true); }

        private void PlayAlertSound(bool forcePlay = false)
        {
            if (!forcePlay && EnableSoundCheck.IsChecked != true) return;

            if (RadioCustomSound.IsChecked == true && !string.IsNullOrEmpty(CustomSoundPathInput.Text))
            {
                string soundPath = System.IO.Path.Combine(soundsFolder, CustomSoundPathInput.Text);
                if (File.Exists(soundPath))
                {
                    customAudioPlayer.Open(new Uri(soundPath));
                    customAudioPlayer.Play();
                }
                else if (forcePlay)
                {
                    MessageBox.Show("Custom audio file not found. Please browse for it again.", "File Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    case "SimpleyViridian - Clipped Teto":
                        PlayIncludedSound("simpleyviridian-clippedteto.wav");
                        break;
                    case "ConfusedIndividual - Clipped":
                        PlayIncludedSound("confusedindividual-clipped.mp3");
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

        private void SingleClipKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
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
                MessageBox.Show("You cannot set the Software Clip Keybind to be the exact same as the Trigger Keybind!", "Keybind Collision", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectButton.Content.ToString() == "Listening...")
                StopListening();
            else
                StartListening();
        }

        private async void StartListening()
        {
            if (string.IsNullOrEmpty(DiscordIdInput.Text))
            {
                MessageBox.Show("Please sign in to Discord first so the server knows your ID.", "Missing ID", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                HotkeyManager.Current.Remove("SyncClip");
                var converter = new KeyGestureConverter();
                KeyGesture triggerGesture = (KeyGesture)converter.ConvertFromString(TriggerKeyInput.Text);

                HotkeyManager.Current.AddOrReplace("SyncClip", triggerGesture.Key, triggerGesture.Modifiers, OnClipTriggered);

                ConnectButton.Content = "Listening...";
                ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 181, 129));

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
                MessageBox.Show("Error setting hotkey. Check your formatting.\n\n" + ex.Message);
            }
        }

        private async void StopListening()
        {
            isSyncActive = false;
            isReconnecting = false;
            HotkeyManager.Current.Remove("SyncClip");
            ConnectButton.Content = "Activate Syncing";
            ConnectButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));

            await DisconnectFromServer();
        }

        // --- WEBSOCKET ENGINE ---

        private async Task ConnectToServer()
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open) return;

            webSocket = new ClientWebSocket();
            wsCts = new CancellationTokenSource();
            try
            {
                ServerStatusDot.Fill = System.Windows.Media.Brushes.Orange;
                ServerStatusText.Text = "Connecting...";

                await webSocket.ConnectAsync(new Uri("wss://clip.oxy.pizza"), wsCts.Token);

                ServerStatusDot.Fill = System.Windows.Media.Brushes.LightGreen;
                ServerStatusText.Text = "Connected";
                isReconnecting = false;
                WriteLog("Connected to the central server.");

                await SendWsMessage(new { action = "identify", user_id = DiscordIdInput.Text, app_uuid = appUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList(), version = AppVersion });
                AskServerToResolveNames();

                _ = ReceiveMessages();
            }
            catch
            {
                ServerStatusDot.Fill = System.Windows.Media.Brushes.IndianRed;
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

            ServerStatusDot.Fill = System.Windows.Media.Brushes.IndianRed;
            ServerStatusText.Text = "Disconnected";
            CurrentVcText.Visibility = Visibility.Collapsed;
            VcUsersPanel.Visibility = Visibility.Collapsed;
            currentActiveVcFriends.Clear();
            currentVcName = "";
            ResetPoolUI();
            WriteLog("Disconnected from the central server.");
        }

        private void ResetPoolUI()
        {
            Dispatcher.Invoke(() => {
                CurrentPoolText.Visibility = Visibility.Collapsed;
                PoolUsersPanel.Visibility = Visibility.Collapsed;
                currentActivePoolFriends.Clear();
                activePoolCode = "";
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
                            else if (action == "dm_verification_failed")
                            {
                                Dispatcher.Invoke(() => {
                                    MessageBox.Show("We could not send you a DM to verify your app!\n\nPlease ensure your DMs are open, or authorize the bot directly by going to:\nhttps://oxy.pizza/clippingtools/authorize\n\nAfter authorizing, click 'Activate Syncing' to try connecting again.", "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                                    WriteLog("Discord DM Verification Error");
                                    StopListening();
                                });
                            }
                            else if (action == "pool_error")
                            {
                                Dispatcher.Invoke(() => {
                                    MessageBox.Show(doc.RootElement.GetProperty("message").GetString(), "Pool Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    WriteLog("The connection to the pool encountered an error.");
                                });
                            }
                            else if (action == "pool_kicked")
                            {
                                Dispatcher.Invoke(() => {
                                    ResetPoolUI();
                                    MessageBox.Show("You have been kicked from the pool.", "Kicked", MessageBoxButton.OK, MessageBoxImage.Information);
                                    WriteLog("You were kicked from the pool.");
                                });
                            }
                            else if (action == "pool_banned")
                            {
                                Dispatcher.Invoke(() => {
                                    ResetPoolUI();
                                    MessageBox.Show("You have been banned from the pool.", "Banned", MessageBoxButton.OK, MessageBoxImage.Error);
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
                            else if (action == "client_pool_update")
                            {
                                Dispatcher.Invoke(() => {
                                    string myId = DiscordIdInput.Text;
                                    activePoolCode = doc.RootElement.GetProperty("pool_code").GetString();
                                    string poolName = doc.RootElement.GetProperty("name").GetString();
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
                                        return;
                                    }

                                    string myChannelId = myVcData.GetProperty("id").GetString();
                                    string myChannelName = myVcData.GetProperty("name").GetString();
                                    currentVcName = myChannelName;

                                    List<(string DisplayText, string SortName, bool IsConnected, string UserId, string RawName, bool IsApprovedByMe)> usersInMyVc = new List<(string, string, bool, string, string, bool)>();
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

                                    CurrentVcText.Text = $"Current VC: {myChannelName}";
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
                    ServerStatusDot.Fill = System.Windows.Media.Brushes.IndianRed;
                    ServerStatusText.Text = "Disconnected";
                    CurrentVcText.Visibility = Visibility.Collapsed;
                    VcUsersPanel.Visibility = Visibility.Collapsed;
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
                    await webSocket.ConnectAsync(new Uri("wss://clip.oxy.pizza"), wsCts.Token);

                    isReconnecting = false;
                    Dispatcher.Invoke(() => {
                        ServerStatusText.Text = "Connected";
                        ServerStatusDot.Fill = System.Windows.Media.Brushes.LightGreen;
                    });

                    WriteLog("Successfully reconnected to the central server.");

                    await SendWsMessage(new { action = "identify", user_id = DiscordIdInput.Text, app_uuid = appUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList(), version = AppVersion });
                    AskServerToResolveNames();

                    _ = ReceiveMessages();
                    return;
                }
                catch
                {
                    Dispatcher.Invoke(() => {
                        ServerStatusText.Text = "Server Offline";
                        ServerStatusDot.Fill = System.Windows.Media.Brushes.IndianRed;
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

        private void ShowHotkeyTakenWarning()
        {
            MessageBox.Show(
                "Windows is blocking this Trigger Key because another app (like OBS, Medal, or Discord) is already using it!\n\n" +
                "To fix this, either:\n" +
                "1. Change your Trigger Key here to something else.\n" +
                "OR\n" +
                "2. Go into the conflicting app and change its keybind so this app can use it.",
                "Hotkey Blocked by Windows", MessageBoxButton.OK, MessageBoxImage.Error);
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
                var combinedFriends = new HashSet<string>(currentActiveVcFriends);
                foreach (var f in currentActivePoolFriends) combinedFriends.Add(f);

                string sentTo = combinedFriends.Count > 0 ? string.Join(", ", combinedFriends) : "nobody";
                WriteLog($"Triggered a local clip command. Sent to: {sentTo}");
                await SendWsMessage(new { action = "trigger", user_id = DiscordIdInput.Text, app_uuid = appUuid });

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
                WriteLog($"Triggered a local clip command. (Network sending disabled)");
            }

            await PerformSafeHardwareClip();
            SendRenamerTrigger(Environment.UserName);
        }

        public async Task<bool> ReceiveNetworkClipCommand(string clipperName)
        {
            if (!CanTriggerClip()) return false;

            await PerformSafeHardwareClip();
            SendRenamerTrigger(clipperName);
            return true;
        }

        private async void SendRenamerTrigger(string clipperName)
        {
            if (AutoRenameClipsCheck.IsChecked != true) return;

            string renamerFolder = System.IO.Path.Combine(configFolder, "clip-management", "clip-renamer");
            if (!Directory.Exists(renamerFolder)) Directory.CreateDirectory(renamerFolder);

            string queuePath = System.IO.Path.Combine(renamerFolder, "clip_queue.txt");
            string vcName = string.IsNullOrEmpty(currentVcName) ? "No VC" : currentVcName;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.AppendAllText(queuePath, $"{clipperName}|{vcName}{Environment.NewLine}");
                    break;
                }
                catch { await Task.Delay(100); }
            }
        }

        private async Task PerformSafeHardwareClip()
        {
            PlayAlertSound();
            await WaitForAbsoluteZeroInput();
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
            NavExtrasBtn.Background = darkBg;
            NavLogsBtn.Background = darkBg;
            NavStatsBtn.Background = darkBg;
            NavUpdateBtn.Background = darkBg;
            NavHelpBtn.Background = darkBg;
        }

        private void NavHomeBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 0;
            ResetNavBackgrounds();
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
        }

        private void NavDiscordBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 1;
            ResetNavBackgrounds();
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
        }

        private void NavSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 2;
            ResetNavBackgrounds();
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
        }

        private void NavExtrasBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 3;
            ResetNavBackgrounds();
            NavExtrasBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
        }

        private void NavLogsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 4;
            ResetNavBackgrounds();
            NavLogsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));

            LogOutputText.Dispatcher.BeginInvoke(new Action(() => {
                LogOutputText.CaretIndex = LogOutputText.Text.Length;
                LogOutputText.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void NavStatsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 5;
            ResetNavBackgrounds();
            NavStatsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            UpdateStatsUI();
        }

        private async void NavUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 6;
            ResetNavBackgrounds();
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavUpdateBtn.Content = "Update";

            await CheckForUpdatesAsync(false);
        }

        private void NavHelpBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 7;
            ResetNavBackgrounds();
            NavHelpBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
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

        private void AutoRestartObsCheck_Click(object sender, RoutedEventArgs e)
        {
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
                File.WriteAllText(triggerPath, "EXIT");
                System.Threading.Thread.Sleep(1000);
            }

            bool shouldRun = AutoRenameClipsCheck.IsChecked == true;
            string clipFolder = ClipLocationInput.Text;

            if (!shouldRun || string.IsNullOrEmpty(clipFolder)) return;

            File.WriteAllText(triggerPath, "");
            File.WriteAllText(statusPath, "");

            string renamerExe = System.IO.Path.Combine(renamerFolder, "ClipRenamer.exe");

            bool needsExtraction = false;
            if (!File.Exists(renamerExe))
            {
                needsExtraction = true;
            }
            else
            {
                string mainAppPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (File.GetLastWriteTime(mainAppPath) > File.GetLastWriteTime(renamerExe))
                {
                    needsExtraction = true;
                    WriteLog("Newer version of Clipping Tools detected. Updating ClipRenamer.exe...");
                }
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

        private void BrowseObsBtn_Click(object sender, RoutedEventArgs e)
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
                    MessageBox.Show("Please select a valid obs64.exe file.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Error);
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
                WriteLog("Auto Start OBS: Could not locate OBS executable.");
                return;
            }

            string obsDir = System.IO.Path.GetDirectoryName(obsPath);
            var obsProcesses = Process.GetProcessesByName("obs64");

            if (obsProcesses.Length > 0)
            {
                WriteLog("Auto Start OBS: OBS is already running. Skipping launch.");
                return;
            }
            else
            {
                WriteLog("Auto Start OBS: Launching OBS with replay enabled...");
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
                                    NavUpdateBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff69b4"));
                                    NavUpdateBtn.Content = "Update Available!!";
                                    NavUpdateBtn.Foreground = Brushes.White;
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
                MessageBox.Show("Update failed: " + ex.Message);
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

        private void HotkeyInput_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            if (key != Key.LeftCtrl && key != Key.RightCtrl &&
                key != Key.LeftAlt && key != Key.RightAlt &&
                key != Key.LeftShift && key != Key.RightShift &&
                key != Key.System)
            {
                TextBox tb = sender as TextBox;
                if (tb == TriggerKeyInput && IsKeybindCollision(tb.Text, ClipKeysList))
                {
                    tb.Text = "";
                    MessageBox.Show("You cannot set the Trigger Keybind to be the exact same as the Software Clip Keybind!", "Keybind Collision", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void RemoveUserBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            DiscordItem itemToRemove = btn.DataContext as DiscordItem;
            if (itemToRemove != null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove '{itemToRemove.DisplayName}'?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
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
                var payload = new
                {
                    action = "resolve_ids",
                    client_id = DiscordIdInput.Text,
                    users = ApprovedUsers.Select(u => u.Id).ToList(),
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

        private void RemoveChannelBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            DiscordItem itemToRemove = btn.DataContext as DiscordItem;
            if (itemToRemove != null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove '{itemToRemove.DisplayName}'?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
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
                MessageBox.Show("You must be connected to the network to view and search for users.\n\nPlease go to the Home tab and click 'Activate Syncing' first.", "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        public string AppUuid { get; set; } = "";
        public string ObsPath { get; set; } = "";
        public string ClipPath { get; set; } = "";
        public bool AutoRenameClips { get; set; } = false;
        public bool AutoStartObs { get; set; } = false;
        public bool AutoRestartObs { get; set; } = false;
        public int ObsCheckInterval { get; set; } = 5;
        public bool SendClips { get; set; } = true;
        public bool ReceiveClips { get; set; } = true;
        public string DiscordId { get; set; } = "";
        public bool AnyVCRule { get; set; } = true;
        public string TriggerKey { get; set; } = "Ctrl+Alt+F10";
        public List<string> ClipKeys { get; set; } = new List<string>();

        public bool AutoSync { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public int RateLimitSeconds { get; set; } = 10;

        public bool EnableSound { get; set; } = true;
        public bool UseCustomSound { get; set; } = false;
        public string SystemSoundType { get; set; } = "SimpleyViridian - Clipped";
        public string CustomSoundFilename { get; set; } = "";

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
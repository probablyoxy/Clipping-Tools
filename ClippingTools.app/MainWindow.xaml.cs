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

        // CHANGE WHEN UPDATE :)
        private const string AppVersion = "v0.1.6";
        private string downloadUrlForUpdate = "";

        private ClientWebSocket webSocket;
        private CancellationTokenSource wsCts;
        private bool isSyncActive = false;
        private bool isReconnecting = false;
        private List<string> currentActiveVcFriends = new List<string>();

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
            isLoaded = true;

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

            string[] builtInSounds = { "clipped.wav", "clippedteto.wav" };
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
                WriteLog("[Security] Trigger keybind wiped on startup due to collision with Software Clip keybind.");
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

        private void Setting_Changed(object sender, RoutedEventArgs e) { SaveSettings(); }
        private void Setting_TextChanged(object sender, TextChangedEventArgs e) { SaveSettings(); }
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
                        PlayIncludedSound("clipped.wav");
                        break;
                    case "SimpleyViridian - Clipped Teto":
                        PlayIncludedSound("clippedteto.wav");
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

                await SendWsMessage(new { action = "identify", user_id = DiscordIdInput.Text, app_uuid = appUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList() });
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
            WriteLog("Disconnected from the central server.");
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
                                    await ReceiveNetworkClipCommand();
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
                                    StopListening();
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
                                        return;
                                    }

                                    string myChannelId = myVcData.GetProperty("id").GetString();
                                    string myChannelName = myVcData.GetProperty("name").GetString();

                                    List<(string DisplayText, string SortName, bool IsConnected)> usersInMyVc = new List<(string, string, bool)>();
                                    currentActiveVcFriends.Clear();

                                    foreach (var prop in vcMapElement.EnumerateObject())
                                    {
                                        if (prop.Value.GetProperty("id").GetString() == myChannelId)
                                        {
                                            string displayName = prop.Value.TryGetProperty("user_name", out JsonElement nameElem) ? nameElem.GetString() : "Unknown User";
                                            bool isConnected = prop.Value.TryGetProperty("is_connected", out JsonElement connElem) && connElem.GetBoolean();
                                            string relationship = prop.Value.TryGetProperty("relationship", out JsonElement relElem) ? relElem.GetString() : "none";

                                            string prefix = "";
                                            if (prop.Name != myId)
                                            {
                                                if (relationship == "mutual") prefix = "* ";
                                                else if (relationship == "outgoing") prefix = "+ ";
                                                else if (relationship == "incoming") prefix = "- ";

                                                if (isConnected && (relationship == "mutual" || relationship == "outgoing"))
                                                {
                                                    currentActiveVcFriends.Add($"{prop.Name} ({displayName})");
                                                }
                                            }

                                            usersInMyVc.Add((prefix + displayName, displayName, isConnected));
                                        }
                                    }

                                    usersInMyVc.Sort((a, b) => a.SortName.CompareTo(b.SortName));

                                    CurrentVcText.Text = $"Current VC: {myChannelName}";
                                    VcUsersPanel.Children.Clear();

                                    foreach (var user in usersInMyVc)
                                    {
                                        StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 2) };
                                        TextBlock tb = new TextBlock { Text = user.DisplayText, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b9bbbe")), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };

                                        System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                                        dot.Fill = user.IsConnected ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f04747"));

                                        sp.Children.Add(tb);
                                        sp.Children.Add(dot);
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

                    await SendWsMessage(new { action = "identify", user_id = DiscordIdInput.Text, app_uuid = appUuid, approved_users = ApprovedUsers.Select(u => u.Id).ToList() });
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
                string sentTo = currentActiveVcFriends.Count > 0 ? string.Join(", ", currentActiveVcFriends) : "nobody";
                WriteLog($"Triggered a local clip command. Sent to: {sentTo}");
                await SendWsMessage(new { action = "trigger", user_id = DiscordIdInput.Text, app_uuid = appUuid });
            }
            else
            {
                WriteLog($"Triggered a local clip command. (Network sending disabled)");
            }

            await PerformSafeHardwareClip();
        }

        public async Task ReceiveNetworkClipCommand()
        {
            if (!CanTriggerClip()) return;

            await PerformSafeHardwareClip();
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

        private async Task PerformSafeHardwareClip()
        {
            PlayAlertSound();
            await WaitForAbsoluteZeroInput();
            await InjectHardwareKeyAsync();
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
            NavUpdateBtn.Background = darkBg;
            NavLogsBtn.Background = darkBg;
            NavHelpBtn.Background = darkBg;
        }

        private void NavLogsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 5;
            ResetNavBackgrounds();
            NavLogsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));

            LogOutputText.Dispatcher.BeginInvoke(new Action(() => {
                LogOutputText.CaretIndex = LogOutputText.Text.Length;
                LogOutputText.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void NavHelpBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 6;
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

        private async void NavUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 4;
            ResetNavBackgrounds();
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavUpdateBtn.Content = "Update";

            await CheckForUpdatesAsync(false);
        }

        private void AutoRestartObsCheck_Click(object sender, RoutedEventArgs e)
        {
            ObsIntervalPanel.Visibility = (AutoRestartObsCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            SaveSettings();
            ToggleObsWatchdog();
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
                WriteLog("Auto Start OBS: Could not locate OBS executable automatically.");
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
                WriteLog("Auto Start OBS: OBS is not running. Launching with replay enabled...");
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string sentinelFile = System.IO.Path.Combine(appData, @"obs-studio\.sentinel");
                string safeModeFile = System.IO.Path.Combine(appData, @"obs-studio\safe_mode");

                if (Directory.Exists(sentinelFile)) { try { Directory.Delete(sentinelFile, true); } catch { } }
                if (File.Exists(safeModeFile)) { try { File.Delete(safeModeFile); } catch { } }

                string vbsPath = System.IO.Path.Combine(configFolder, "launch_obs.vbs");
                string vbsCode = $@"
Set objShell = CreateObject(""WScript.Shell"")
objShell.CurrentDirectory = ""{obsDir}""
objShell.Run Chr(34) & ""{obsPath}"" & Chr(34) & "" --startreplaybuffer --minimize-to-tray --disable-shutdown-check"", 1, False
";
                try
                {
                    if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);
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

                                    string vbsPath = System.IO.Path.Combine(configFolder, "launch_obs.vbs");
                                    string vbsCode = $@"
Set objShell = CreateObject(""WScript.Shell"")
objShell.CurrentDirectory = ""{detectedObsDir}""
objShell.Run Chr(34) & ""{detectedObsPath}"" & Chr(34) & "" --startreplaybuffer --minimize-to-tray --disable-shutdown-check"", 1, False
";
                                    try
                                    {
                                        if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);
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
    }

    public class AppSettings
    {
        public bool EnableLogging { get; set; } = true;
        public int MaxLogLines { get; set; } = 1000;
        public string AppUuid { get; set; } = "";
        public string ObsPath { get; set; } = "";
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
}
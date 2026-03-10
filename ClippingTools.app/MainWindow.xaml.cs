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
using NHotkey;
using NHotkey.Wpf;
using WindowsInput;
using WindowsInput.Native;
using System.Diagnostics;

namespace ClippingTools.app
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private InputSimulator simulator = new InputSimulator();
        private MediaPlayer customAudioPlayer = new MediaPlayer();

        public ObservableCollection<string> ApprovedUsers { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> ApprovedChannels { get; set; } = new ObservableCollection<string>();

        public List<string> ClipKeysList { get; set; } = new List<string>();

        private readonly string configFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
        private string configFilePath => System.IO.Path.Combine(configFolder, "settings.json");

        private bool isLoaded = false;

        // CHANGE WHEN UPDATE :)
        private const string AppVersion = "v0.1.0";
        private string downloadUrlForUpdate = "";

        public MainWindow()
        {
            InitializeComponent();

            UserListBox.ItemsSource = ApprovedUsers;
            ChannelListBox.ItemsSource = ApprovedChannels;

            CollectionViewSource.GetDefaultView(ApprovedUsers).SortDescriptions.Add(new SortDescription(".", ListSortDirection.Ascending));
            CollectionViewSource.GetDefaultView(ApprovedChannels).SortDescriptions.Add(new SortDescription(".", ListSortDirection.Ascending));

            LoadSettings();
            isLoaded = true;

            CurrentVersionText.Text = AppVersion;

            if (AutoSyncCheck.IsChecked == true)
            {
                StartListening();
            }
        }

        // ==============================================================================
        // INSTANT SAVE / LOAD SYSTEM
        // ==============================================================================

        private void LoadSettings()
        {
            if (!File.Exists(configFilePath))
            {
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
                    SendClipsCheck.IsChecked = settings.SendClips;
                    ReceiveClipsCheck.IsChecked = settings.ReceiveClips;
                    DiscordIdInput.Text = settings.DiscordId;
                    RadioAnyVC.IsChecked = settings.AnyVCRule;
                    RadioSpecificVC.IsChecked = !settings.AnyVCRule;
                    TriggerKeyInput.Text = settings.TriggerKey;

                    AutoSyncCheck.IsChecked = settings.AutoSync;
                    StartWithWindowsCheck.IsChecked = settings.StartWithWindows;

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
                    foreach (var c in settings.Channels) ApprovedChannels.Add(c);

                    ApprovedUsers.Clear();
                    foreach (var u in settings.Users) ApprovedUsers.Add(u);
                }
            }
            catch { }

            RebuildClipKeyUI();
        }

        private void SaveSettings()
        {
            if (!isLoaded) return;

            if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);

            var settings = new AppSettings
            {
                SendClips = SendClipsCheck.IsChecked ?? true,
                ReceiveClips = ReceiveClipsCheck.IsChecked ?? true,
                DiscordId = DiscordIdInput.Text,
                AnyVCRule = RadioAnyVC.IsChecked ?? true,
                TriggerKey = TriggerKeyInput.Text,
                ClipKeys = ClipKeysList,

                AutoSync = AutoSyncCheck.IsChecked ?? false,
                StartWithWindows = StartWithWindowsCheck.IsChecked ?? false,

                EnableSound = EnableSoundCheck.IsChecked ?? true,
                UseCustomSound = RadioCustomSound.IsChecked ?? false,
                SystemSoundType = (SystemSoundCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Exclamation",
                CustomSoundFilename = CustomSoundPathInput.Text,

                Channels = ApprovedChannels.ToList(),
                Users = ApprovedUsers.ToList()
            };

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFilePath, json);
        }

        private void Setting_Changed(object sender, RoutedEventArgs e) { SaveSettings(); }
        private void Setting_TextChanged(object sender, TextChangedEventArgs e) { SaveSettings(); }
        private void Window_Closing(object sender, CancelEventArgs e) { SaveSettings(); }

        private void StartWithWindowsCheck_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            try
            {
                RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (StartWithWindowsCheck.IsChecked == true)
                    rk.SetValue("ClippingTools", System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                else
                    rk.DeleteValue("ClippingTools", false);
            }
            catch
            {
                MessageBox.Show("Could not set registry key. Try running the app as Administrator.", "Registry Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (isLoaded) { RadioSystemSound.IsChecked = true; SaveSettings(); }
        }

        private void BrowseSoundBtn_Click(object sender, RoutedEventArgs e)
        {
            RadioCustomSound.IsChecked = true;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3";

            if (openFileDialog.ShowDialog() == true)
            {
                if (!Directory.Exists(configFolder)) Directory.CreateDirectory(configFolder);
                string ext = System.IO.Path.GetExtension(openFileDialog.FileName);
                string newFileName = "sound" + ext;
                string destPath = System.IO.Path.Combine(configFolder, newFileName);
                File.Copy(openFileDialog.FileName, destPath, true);
                CustomSoundPathInput.Text = newFileName;
                SaveSettings();
            }
        }

        private void TestSoundBtn_Click(object sender, RoutedEventArgs e) { PlayAlertSound(forcePlay: true); }

        private void PlayAlertSound(bool forcePlay = false)
        {
            if (!forcePlay && EnableSoundCheck.IsChecked != true) return;

            if (RadioCustomSound.IsChecked == true && !string.IsNullOrEmpty(CustomSoundPathInput.Text))
            {
                string soundPath = System.IO.Path.Combine(configFolder, CustomSoundPathInput.Text);
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
                    case "Asterisk": System.Media.SystemSounds.Asterisk.Play(); break;
                    case "Beep": System.Media.SystemSounds.Beep.Play(); break;
                    case "Hand": System.Media.SystemSounds.Hand.Play(); break;
                    case "Question": System.Media.SystemSounds.Question.Play(); break;
                    default: System.Media.SystemSounds.Exclamation.Play(); break;
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

        private void StartListening()
        {
            try
            {
                HotkeyManager.Current.Remove("SyncClip");
                var converter = new KeyGestureConverter();
                KeyGesture triggerGesture = (KeyGesture)converter.ConvertFromString(TriggerKeyInput.Text);

                HotkeyManager.Current.AddOrReplace("SyncClip", triggerGesture.Key, triggerGesture.Modifiers, OnClipTriggered);

                ConnectButton.Content = "Listening...";
                ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 181, 129));
            }
            catch (NHotkey.HotkeyAlreadyRegisteredException)
            {
                StopListening();
                ShowHotkeyTakenWarning();
            }
            catch (Exception ex)
            {
                StopListening();
                MessageBox.Show("Error setting hotkey. Check your formatting.\n\n" + ex.Message);
            }
        }

        private void StopListening()
        {
            HotkeyManager.Current.Remove("SyncClip");
            ConnectButton.Content = "Activate Syncing";
            ConnectButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));
        }

        private void ShowHotkeyTakenWarning()
        {
            MessageBox.Show(
                "Windows is blocking this Trigger Key because another app (like Shadowplay, OBS, or Discord) is already using it!\n\n" +
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

        private async void OnClipTriggered(object sender, HotkeyEventArgs e)
        {
            await PerformSafeHardwareClip();
        }

        public async Task ReceiveNetworkClipCommand()
        {
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
        // UI NAVIGATION & UPDATER LOGIC BELOW
        // ==============================================================================

        private void NavHomeBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 0;
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
        }

        private void NavDiscordBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 1;
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
        }

        private void NavSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 2;
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
        }

        private async void NavUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            MainContent.SelectedIndex = 3;
            NavHomeBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavDiscordBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavSettingsBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#2f3136"));
            NavUpdateBtn.Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#4f545c"));

            await CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            UpdateStatusText.Text = "Checking GitHub for updates...";
            UpdateStatusText.Foreground = Brushes.LightGray;
            PerformUpdateBtn.Visibility = Visibility.Collapsed;

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
                            LatestVersionText.Text = latestVersion;

                            if (latestVersion != AppVersion)
                            {
                                UpdateStatusText.Text = "A new update is available!";
                                UpdateStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43b581"));

                                downloadUrlForUpdate = doc.RootElement.GetProperty("assets")[0].GetProperty("browser_download_url").GetString();
                                PerformUpdateBtn.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                UpdateStatusText.Text = "You are on the latest version.";
                            }
                        }
                    }
                    else
                    {
                        UpdateStatusText.Text = "Could not find any releases on GitHub.";
                        LatestVersionText.Text = "Unknown";
                    }
                }
            }
            catch
            {
                UpdateStatusText.Text = "Network error while checking for updates.";
                UpdateStatusText.Foreground = Brushes.IndianRed;
            }
        }

        private async void PerformUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
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
                Keyboard.ClearFocus();
                SaveSettings();

                if (ConnectButton.Content.ToString() == "Listening...")
                {
                    StartListening();
                }
                else
                {
                    TextBox tb = sender as TextBox;
                    if (tb == TriggerKeyInput) TestTriggerKeyAvailability(tb.Text);
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
            foreach (var id in newIds) { if (!ApprovedUsers.Contains(id)) ApprovedUsers.Add(id); }
            NewUserIdInput.Clear();
            SaveSettings();
        }

        private void RemoveUserBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            string idToRemove = btn.DataContext as string;
            if (idToRemove != null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove '{idToRemove}'?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes) { ApprovedUsers.Remove(idToRemove); SaveSettings(); }
            }
        }

        private void NewChannelIdInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ProcessAddChannels(); }
        private void AddChannelBtn_Click(object sender, RoutedEventArgs e) { ProcessAddChannels(); }

        private void ProcessAddChannels()
        {
            var newIds = NewChannelIdInput.Text.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var id in newIds) { if (!ApprovedChannels.Contains(id)) ApprovedChannels.Add(id); }
            NewChannelIdInput.Clear();
            SaveSettings();
        }

        private void RemoveChannelBtn_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            string idToRemove = btn.DataContext as string;
            if (idToRemove != null)
            {
                MessageBoxResult result = MessageBox.Show($"Are you sure you want to remove '{idToRemove}'?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes) { ApprovedChannels.Remove(idToRemove); SaveSettings(); }
            }
        }
    }

    public class AppSettings
    {
        public bool SendClips { get; set; } = true;
        public bool ReceiveClips { get; set; } = true;
        public string DiscordId { get; set; } = "";
        public bool AnyVCRule { get; set; } = true;
        public string TriggerKey { get; set; } = "Ctrl+Shift+C";
        public List<string> ClipKeys { get; set; } = new List<string>();

        public bool AutoSync { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;

        public bool EnableSound { get; set; } = true;
        public bool UseCustomSound { get; set; } = false;
        public string SystemSoundType { get; set; } = "Exclamation";
        public string CustomSoundFilename { get; set; } = "";
        public List<string> Channels { get; set; } = new List<string>();
        public List<string> Users { get; set; } = new List<string>();
    }
}
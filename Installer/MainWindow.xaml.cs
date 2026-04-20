using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ClippingToolsInstaller
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            CheckAppDataFolder();
        }

        private void CheckAppDataFolder()
        {
            string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
            if (Directory.Exists(appDataPath))
            {
                ManageAppBtn.Visibility = Visibility.Visible;
            }
            else
            {
                ManageAppBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Install Location",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() == true)
            {
                InstallPathInput.Text = Path.Combine(dialog.FolderName, "Clipping Tools");
            }
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            InstallBtn.IsEnabled = false;
            InstallProgress.Visibility = Visibility.Visible;
            StatusText.Text = "Preparing installation...";
            string installDir = InstallPathInput.Text;

            try
            {
                StatusText.Text = "Finding latest version on GitHub...";

                string downloadUrl = "";
                string latestVersion = "";
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ClippingTools-Installer");

                    string apiUrl = "https://api.github.com/repos/probablyoxy/Clipping-Tools/releases/latest";

                    var response = await client.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode) throw new Exception("Could not find GitHub release.");

                    var json = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        latestVersion = doc.RootElement.GetProperty("tag_name").GetString();
                        downloadUrl = doc.RootElement.GetProperty("assets")[0].GetProperty("browser_download_url").GetString();
                    }
                }

                StatusText.Text = "Creating directories...";
                if (!Directory.Exists(installDir))
                {
                    Directory.CreateDirectory(installDir);
                }

                StatusText.Text = "Downloading Clipping Tools...";
                string exePath = Path.Combine(installDir, "ClippingTools.exe");

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
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
                                if (totalBytes != -1)
                                {
                                    InstallProgress.Value = (double)totalRead / totalBytes * 100;
                                }
                            }
                        } while (isMoreToRead);
                    }
                }

                StatusText.Text = "Creating Windows shortcuts...";
                CreateShortcuts(exePath, StartMenuShortcutCheck.IsChecked == true, DesktopShortcutCheck.IsChecked == true);

                if (!string.IsNullOrEmpty(latestVersion))
                {
                    string statsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools", "statistics");
                    if (!Directory.Exists(statsDir))
                    {
                        Directory.CreateDirectory(statsDir);
                    }

                    string versionPath = Path.Combine(statsDir, "version.json");
                    var versionInfo = new
                    {
                        Version = latestVersion,
                        LastUpdated = DateTime.Now
                    };

                    string versionJson = JsonSerializer.Serialize(versionInfo, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(versionPath, versionJson);
                }

                StatusText.Text = "Installation Complete!";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 181, 129));
                InstallBtn.Content = "Launch App";

                InstallBtn.Click -= InstallBtn_Click;
                InstallBtn.Click += (s, ev) =>
                {
                    Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                    Application.Current.Shutdown();
                };
                InstallBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
                StatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
                InstallBtn.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
            }
        }

        private enum PendingManageAction { None, ResetSettings, DeleteData }
        private PendingManageAction _pendingAction = PendingManageAction.None;

        private void ManageAppBtn_Click(object sender, RoutedEventArgs e)
        {
            ManageOverlay.Visibility = Visibility.Visible;
        }

        private void CloseManageOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            ManageOverlay.Visibility = Visibility.Collapsed;
        }

        private void ResetSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            _pendingAction = PendingManageAction.ResetSettings;
            ConfirmManageTitle.Text = "Reset Settings";
            ConfirmManageText.Text = "Are you sure you want to delete your settings.json? This will revert all configurations to default.";
            ConfirmManageOverlay.Visibility = Visibility.Visible;
        }

        private void DeleteDataBtn_Click(object sender, RoutedEventArgs e)
        {
            _pendingAction = PendingManageAction.DeleteData;
            ConfirmManageTitle.Text = "Delete App Data";
            ConfirmManageText.Text = "Are you sure you want to completely delete the Clipping Tools AppData folder? This will wipe your settings, logs, authentication tokens, and everything else!";
            ConfirmManageOverlay.Visibility = Visibility.Visible;
        }

        private void ConfirmManageCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            ConfirmManageOverlay.Visibility = Visibility.Collapsed;
            _pendingAction = PendingManageAction.None;
        }

        private void ConfirmManageOkBtn_Click(object sender, RoutedEventArgs e)
        {
            string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");

            try
            {
                if (_pendingAction == PendingManageAction.ResetSettings)
                {
                    string settingsPath = Path.Combine(appDataPath, "settings.json");
                    if (File.Exists(settingsPath))
                    {
                        File.Delete(settingsPath);
                    }
                }
                else if (_pendingAction == PendingManageAction.DeleteData)
                {
                    if (Directory.Exists(appDataPath))
                    {
                        Directory.Delete(appDataPath, true);
                    }
                }
            }
            catch { }

            ConfirmManageOverlay.Visibility = Visibility.Collapsed;
            ManageOverlay.Visibility = Visibility.Collapsed;
            _pendingAction = PendingManageAction.None;
            CheckAppDataFolder();
        }

        private void CreateShortcuts(string exePath, bool createStartMenu, bool createDesktop)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                if (createStartMenu)
                {
                    string startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string startShortcutPath = Path.Combine(startMenuPath, "Clipping Tools.lnk");
                    dynamic startMenuShortcut = shell.CreateShortcut(startShortcutPath);
                    startMenuShortcut.TargetPath = exePath;
                    startMenuShortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                    startMenuShortcut.Description = "Clipping Tools";
                    startMenuShortcut.Save();
                }

                if (createDesktop)
                {
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string desktopShortcutPath = Path.Combine(desktopPath, "Clipping Tools.lnk");
                    dynamic desktopShortcut = shell.CreateShortcut(desktopShortcutPath);
                    desktopShortcut.TargetPath = exePath;
                    desktopShortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                    desktopShortcut.Description = "Clipping Tools";
                    desktopShortcut.Save();
                }

                string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
                if (!Directory.Exists(settingsDir))
                {
                    Directory.CreateDirectory(settingsDir);
                }

                string settingsPath = Path.Combine(settingsDir, "settings.json");
                var settings = new System.Collections.Generic.Dictionary<string, object>();

                if (File.Exists(settingsPath))
                {
                    try
                    {
                        string existingJson = File.ReadAllText(settingsPath);
                        settings = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(existingJson) ?? new System.Collections.Generic.Dictionary<string, object>();
                    }
                    catch { }
                }

                settings["StartMenuShortcut"] = createStartMenu;
                settings["DesktopShortcut"] = createDesktop;

                string jsonString = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, jsonString);
            }
            catch { }
        }
    }
}
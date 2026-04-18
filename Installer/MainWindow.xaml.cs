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
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ClippingTools-Installer");

                    string apiUrl = "https://api.github.com/repos/probablyoxy/Clipping-Tools/releases/latest";

                    var response = await client.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode) throw new Exception("Could not find GitHub release.");

                    var json = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
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

                if (createStartMenu || createDesktop)
                {
                    string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippingTools");
                    if (!Directory.Exists(settingsDir))
                    {
                        Directory.CreateDirectory(settingsDir);
                    }

                    string settingsPath = Path.Combine(settingsDir, "settings.json");
                    var settings = new System.Collections.Generic.Dictionary<string, bool>();

                    if (createStartMenu) settings.Add("StartMenuShortcut", true);
                    if (createDesktop) settings.Add("DesktopShortcut", true);

                    string jsonString = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsPath, jsonString);
                }
            }
            catch { }
        }
    }
}
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Windows.Input;
using System.Collections.Generic;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using YamlDotNet.Serialization;

namespace EQEmu_Patcher
{
    public partial class MainWindow : Window
    {
        private readonly SolidColorBrush _redBrush = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        private readonly SolidColorBrush _defaultButtonBrush = new SolidColorBrush(Color.FromRgb(251, 233, 164)); // #FBE9A4
        private bool isPatching = false;
        private bool isPatchCancelled = false;
        private bool isPendingPatch = false;
        private bool isNeedingSelfUpdate = false;
        private bool isLoading;
        private bool isAutoPatch = false;
        private bool isAutoPlay = false;
        private CancellationTokenSource cts;
        private Process process;
        private string myHash = "";
        private string patcherUrl;
        private string fileName;
        private string version;

        // Server and file configuration
        private static string serverName;
        private static string filelistUrl;

        // Supported client versions
        public static List<VersionTypes> supportedClients = new List<VersionTypes> {
            VersionTypes.Rain_Of_Fear,
            VersionTypes.Rain_Of_Fear_2
        };

        private Dictionary<VersionTypes, ClientVersion> clientVersions = new Dictionary<VersionTypes, ClientVersion>();
        private VersionTypes currentVersion;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            btnPatch.Click += BtnPatch_Click;
            btnPlay.Click += BtnPlay_Click;
            chkAutoPatch.Checked += ChkAutoPatch_CheckedChanged;
            chkAutoPlay.Checked += ChkAutoPlay_CheckedChanged;

            // Initialize server configuration
            serverName = "The Heroes Journey";
            if (string.IsNullOrEmpty(serverName))
            {
                MessageBox.Show("This patcher was built incorrectly. Please contact the distributor of this and inform them the server name is not provided or screenshot this message.");
                Close();
                return;
            }

            // Keep the remote filename as heroesjourneyeq
            fileName = "heroesjourneyeq";

            filelistUrl = "https://github.com/The-Heroes-Journey-EQEMU/eqemupatcher/releases/latest/download/";
            if (string.IsNullOrEmpty(filelistUrl))
            {
                MessageBox.Show("This patcher was built incorrectly. Please contact the distributor of this and inform them the file list url is not provided or screenshot this message.", serverName);
                Close();
                return;
            }
            if (!filelistUrl.EndsWith("/")) filelistUrl += "/";

            patcherUrl = "https://github.com/The-Heroes-Journey-EQEMU/eqemupatcher/releases/latest/download/";
            if (string.IsNullOrEmpty(patcherUrl))
            {
                MessageBox.Show("This patcher was built incorrectly. Please contact the distributor of this and inform them the patcher url is not provided or screenshot this message.", serverName);
                Close();
                return;
            }
            if (!patcherUrl.EndsWith("/")) patcherUrl += "/";

            buildClientVersions();
        }

        private void buildClientVersions()
        {
            clientVersions.Clear();
            clientVersions.Add(VersionTypes.Titanium, new ClientVersion("Titanium", "titanium"));
            clientVersions.Add(VersionTypes.Secrets_Of_Feydwer, new ClientVersion("Secrets Of Feydwer", "sof"));
            clientVersions.Add(VersionTypes.Seeds_Of_Destruction, new ClientVersion("Seeds of Destruction", "sod"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear, new ClientVersion("Rain of Fear", "rof"));
            clientVersions.Add(VersionTypes.Rain_Of_Fear_2, new ClientVersion("Rain of Fear 2", "rof2"));
            clientVersions.Add(VersionTypes.Underfoot, new ClientVersion("Underfoot", "underfoot"));
            clientVersions.Add(VersionTypes.Broken_Mirror, new ClientVersion("Broken Mirror", "brokenmirror"));
        }

        private void detectClientVersion()
        {
            // For now, we'll just set it to RoF2 as in the original code
            currentVersion = VersionTypes.Rain_Of_Fear_2;
            // You can add the hash detection logic here later if needed
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void LinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open link: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            isLoading = true;
            cts = new CancellationTokenSource();

            IniLibrary.Load();
            isAutoPlay = (IniLibrary.instance.AutoPlay.ToLower() == "true");
            isAutoPatch = (IniLibrary.instance.AutoPatch.ToLower() == "true");
            chkAutoPlay.IsChecked = isAutoPlay;
            chkAutoPatch.IsChecked = isAutoPatch;

            StatusLibrary.SubscribeProgress(new StatusLibrary.ProgressHandler((int value) =>
            {
                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = value / 100.0;
                });
            }));

            StatusLibrary.SubscribeLogAdd(new StatusLibrary.LogAddHandler((string message) =>
            {
                Dispatcher.Invoke(() =>
                {
                    txtLog.AppendText(message + Environment.NewLine);
                    txtLog.ScrollToEnd();
                });
            }));

            StatusLibrary.SubscribePatchState(new StatusLibrary.PatchStateHandler((bool isPatchGoing) =>
            {
                Dispatcher.Invoke(() =>
                {
                    btnPatch.Content = isPatchGoing ? "CANCEL" : "PATCH";
                });
            }));

            version = IniLibrary.instance.Version;

            await CheckForUpdates();
            
            if (isAutoPatch)
            {
                isPendingPatch = true;
                await Task.Delay(1000);
                StartPatch();
            }
            
            isLoading = false;
        }

        private async Task CheckForUpdates()
        {
            StatusLibrary.Log("Checking for updates...");
            await Task.Delay(2000);

            // First check if we need to update the patcher itself
            string url = $"{patcherUrl}{fileName}-hash.txt";
            try
            {
                StatusLibrary.Log("[DEBUG] Checking patcher version...");
                var data = await UtilityLibrary.Download(cts, url);
                string response = System.Text.Encoding.Default.GetString(data).ToUpper();
                
                if (response != "")
                {
                    myHash = UtilityLibrary.GetMD5(System.Windows.Forms.Application.ExecutablePath);
                    StatusLibrary.Log($"[DEBUG] Comparing patcher hashes - Remote: {response}, Local: {myHash}");
                    if (response != myHash)
                    {
                        isNeedingSelfUpdate = true;
                        if (!isPendingPatch)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                StatusLibrary.Log("[DEBUG] Patcher update needed");
                                StatusLibrary.Log("Update available! Click PATCH to begin.");
                                btnPatch.Visibility = Visibility.Visible;
                                btnPlay.Visibility = Visibility.Collapsed;
                            });
                            return;
                        }
                    }
                    else
                    {
                        StatusLibrary.Log("[DEBUG] Patcher is up to date");
                    }
                }

                // Now check if game files need updating by comparing filelist version
                string suffix = "rof"; // Since we're only supporting RoF/RoF2
                string webUrl = $"{filelistUrl}{suffix}/filelist_{suffix}.yml";
                StatusLibrary.Log($"[DEBUG] Attempting to download filelist from: {webUrl}");
                string filelistResponse = await UtilityLibrary.DownloadFile(cts, webUrl, "filelist.yml");
                if (filelistResponse != "")
                {
                    webUrl = $"{filelistUrl}/filelist_{suffix}.yml";
                    StatusLibrary.Log($"[DEBUG] First URL failed, trying alternate URL: {webUrl}");
                    filelistResponse = await UtilityLibrary.DownloadFile(cts, webUrl, "filelist.yml");
                    if (filelistResponse != "")
                    {
                        StatusLibrary.Log($"Failed to fetch filelist from {webUrl}: {filelistResponse}");
                        return;
                    }
                }

                // Read and check filelist version
                FileList filelist;
                string filelistPath = $"{Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath)}\\filelist.yml";
                StatusLibrary.Log($"[DEBUG] Reading local filelist from: {filelistPath}");
                
                using (var input = File.OpenText(filelistPath))
                {
                    var deserializerBuilder = new DeserializerBuilder()
                        .WithNamingConvention(new YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention());
                    var deserializer = deserializerBuilder.Build();
                    filelist = deserializer.Deserialize<FileList>(input);
                }

                StatusLibrary.Log($"[DEBUG] Comparing versions - Filelist version: {filelist.version}, Last patched version: {IniLibrary.instance.LastPatchedVersion}");
                if (filelist.version != IniLibrary.instance.LastPatchedVersion)
                {
                    if (!isPendingPatch)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusLibrary.Log("[DEBUG] Version mismatch detected - update needed");
                            StatusLibrary.Log("Update available! Click PATCH to begin.");
                            btnPatch.Visibility = Visibility.Visible;
                            btnPlay.Visibility = Visibility.Collapsed;
                        });
                        return;
                    }
                }
                else
                {
                    StatusLibrary.Log("[DEBUG] Versions match - no update needed");
                }

                // If we get here, no updates are needed
                await Task.Delay(1000); // 1 second pause
                Dispatcher.Invoke(() =>
                {
                    StatusLibrary.Log("Ready to play!");
                    btnPatch.Visibility = Visibility.Collapsed;
                    btnPlay.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                await Task.Delay(1000); // 1 second pause
                StatusLibrary.Log($"[DEBUG] Exception occurred: {ex.Message}");
                StatusLibrary.Log($"Failed to fetch patch from {url}: {ex.Message}");
            }
        }

        private void BtnPatch_Click(object sender, RoutedEventArgs e)
        {
            if (isLoading && !isPendingPatch)
            {
                isPendingPatch = true;
                StatusLibrary.Log("Checking for updates...");
                btnPatch.Content = "CANCEL";
                return;
            }

            if (isPatching)
            {
                isPatchCancelled = true;
                cts.Cancel();
                return;
            }
            StartPatch();
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                process = UtilityLibrary.StartEverquest();
                if (process != null)
                    this.Close();
                else
                    MessageBox.Show("The process failed to start", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception err)
            {
                MessageBox.Show($"An error occurred while trying to start Everquest: {err.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChkAutoPatch_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (isLoading) return;
            isAutoPatch = chkAutoPatch.IsChecked ?? false;
            IniLibrary.instance.AutoPatch = isAutoPatch ? "true" : "false";
            IniLibrary.Save();
        }

        private void ChkAutoPlay_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (isLoading) return;
            isAutoPlay = chkAutoPlay.IsChecked ?? false;
            IniLibrary.instance.AutoPlay = isAutoPlay ? "true" : "false";
            if (isAutoPlay)
                StatusLibrary.Log("To disable autoplay: edit eqemupatcher.yml or wait until next patch.");
            IniLibrary.Save();
        }

        private async void StartPatch()
        {
            if (isPatching) return;

            cts = new CancellationTokenSource();
            isPatchCancelled = false;
            txtLog.Clear();
            StatusLibrary.SetPatchState(true);
            isPatching = true;
            btnPatch.Background = _defaultButtonBrush;
            StatusLibrary.Log("Patching in progress...");
            await Task.Delay(1000); // 1 second pause
            btnPatch.Visibility = Visibility.Collapsed;

            try
            {
                await AsyncPatch();
                
                if (!isPatchCancelled)
                {
                    await Task.Delay(1000); // 1 second pause
                    StatusLibrary.Log("Patch complete! Ready to play!");
                    btnPlay.Visibility = Visibility.Visible;
                    
                    if (isAutoPlay)
                    {
                        await Task.Delay(2000); // 2 second pause before auto-play
                        BtnPlay_Click(null, null);
                    }
                }
            }
            catch (Exception e)
            {
                await Task.Delay(1000); // 1 second pause
                StatusLibrary.Log($"Exception during patch: {e.Message}");
                btnPatch.Visibility = Visibility.Visible;
            }

            StatusLibrary.SetPatchState(false);
            isPatching = false;
            isPatchCancelled = false;
            cts.Cancel();
        }

        private async Task AsyncPatch()
        {
            Stopwatch start = Stopwatch.StartNew();
            StatusLibrary.Log($"Patching with patcher version {version}...");
            StatusLibrary.SetProgress(0);

            // Handle self-update first if needed
            if (myHash != "" && isNeedingSelfUpdate)
            {
                StatusLibrary.Log("Downloading update...");
                string url = $"{patcherUrl}/{fileName}.exe";
                try
                {
                    var data = await UtilityLibrary.Download(cts, url);
                    string localExePath = System.Windows.Forms.Application.ExecutablePath;
                    string localExeName = Path.GetFileName(localExePath);
                    StatusLibrary.Log($"[DEBUG] Saving update as: {localExeName}");
                    
                    if (File.Exists(localExePath + ".old"))
                    {
                        File.Delete(localExePath + ".old");
                    }
                    File.Move(localExePath, localExePath + ".old");
                    using (var w = File.Create(localExePath))
                    {
                        await w.WriteAsync(data, 0, data.Length, cts.Token);
                    }
                    StatusLibrary.Log($"Self update complete. New version will be used next run.");
                }
                catch (Exception e)
                {
                    StatusLibrary.Log($"Self update failed {url}: {e.Message}");
                }
                isNeedingSelfUpdate = false;
            }

            if (isPatchCancelled)
            {
                StatusLibrary.Log("Patching cancelled.");
                return;
            }

            // Get the client version suffix
            string suffix = "rof"; // Since we're only supporting RoF/RoF2

            // Download the filelist
            string webUrl = $"{filelistUrl}{suffix}/filelist_{suffix}.yml";
            string response = await UtilityLibrary.DownloadFile(cts, webUrl, "filelist.yml");
            if (response != "")
            {
                webUrl = $"{filelistUrl}/filelist_{suffix}.yml";
                response = await UtilityLibrary.DownloadFile(cts, webUrl, "filelist.yml");
                if (response != "")
                {
                    StatusLibrary.Log($"Failed to fetch filelist from {webUrl}: {response}");
                    return;
                }
            }

            // Parse the filelist
            FileList filelist;
            using (var input = File.OpenText($"{Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath)}\\filelist.yml"))
            {
                var deserializerBuilder = new DeserializerBuilder()
                    .WithNamingConvention(new YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention());

                var deserializer = deserializerBuilder.Build();
                filelist = deserializer.Deserialize<FileList>(input);
            }

            // Calculate total patch size
            double totalBytes = 0;
            double currentBytes = 1;
            double patchedBytes = 0;

            foreach (var entry in filelist.downloads)
            {
                totalBytes += entry.size;
            }
            if (totalBytes == 0) totalBytes = 1;

            // Download and patch files
            if (!filelist.downloadprefix.EndsWith("/")) filelist.downloadprefix += "/";
            foreach (var entry in filelist.downloads)
            {
                if (isPatchCancelled)
                {
                    StatusLibrary.Log("Patching cancelled.");
                    return;
                }

                StatusLibrary.SetProgress((int)(currentBytes / totalBytes * 10000));

                var path = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\" + entry.name.Replace("/", "\\");
                if (!UtilityLibrary.IsPathChild(path))
                {
                    StatusLibrary.Log("Path " + path + " might be outside of your Everquest directory. Skipping download to this location.");
                    continue;
                }

                // Check if file exists and is already patched
                if (File.Exists(path))
                {
                    var md5 = UtilityLibrary.GetMD5(path);
                    if (md5.ToUpper() == entry.md5.ToUpper())
                    {
                        currentBytes += entry.size;
                        continue;
                    }
                }

                string url = "https://patch.heroesjourneyemu.com/rof/" + entry.name.Replace("\\", "/");
                string backupUrl = filelist.downloadprefix + entry.name.Replace("\\", "/");

                response = await UtilityLibrary.DownloadFile(cts, url, entry.name);
                if (response != "")
                {
                    response = await UtilityLibrary.DownloadFile(cts, backupUrl, entry.name);
                    if (response == "404")
                    {
                        StatusLibrary.Log($"Failed to download {entry.name} ({generateSize(entry.size)}) from {url} and {filelist.downloadprefix}, 404 error (website may be down?)");
                        return;
                    }
                }
                StatusLibrary.Log($"{entry.name} ({generateSize(entry.size)})");

                currentBytes += entry.size;
                patchedBytes += entry.size;
            }

            // Handle file deletions
            if (filelist.deletes != null && filelist.deletes.Count > 0)
            {
                foreach (var entry in filelist.deletes)
                {
                    var path = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\" + entry.name.Replace("/", "\\");
                    if (isPatchCancelled)
                    {
                        StatusLibrary.Log("Patching cancelled.");
                        return;
                    }
                    if (!UtilityLibrary.IsPathChild(path))
                    {
                        StatusLibrary.Log("Path " + entry.name + " might be outside your Everquest directory. Skipping deletion of this file.");
                        continue;
                    }
                    if (File.Exists(path))
                    {
                        StatusLibrary.Log("Deleting " + entry.name + "...");
                        File.Delete(path);
                    }
                }
            }

            StatusLibrary.SetProgress(10000);
            if (patchedBytes == 0)
            {
                string version = filelist.version;
                if (version.Length >= 8)
                {
                    version = version.Substring(0, 8);
                }
                StatusLibrary.Log($"Up to date with patch {version}.");
                return;
            }

            string elapsed = start.Elapsed.ToString("ss\\.ff");
            StatusLibrary.Log($"Complete! Patched {generateSize(patchedBytes)} in {elapsed} seconds. Press Play to begin.");
            IniLibrary.instance.LastPatchedVersion = filelist.version;
            IniLibrary.Save();
        }

        private string generateSize(double size)
        {
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} bytes";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} KB";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} MB";
            }

            size /= 1024;
            if (size < 1024)
            {
                return $"{Math.Round(size, 2)} GB";
            }

            return $"{Math.Round(size, 2)} TB";
        }
    }
} 
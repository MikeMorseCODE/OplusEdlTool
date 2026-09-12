using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OplusEdlTool.Services;

namespace OplusEdlTool
{
    public class PartitionRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _name = string.Empty;
        private int _lun;
        private ulong _firstLBA;
        private ulong _lastLBA;
        private string _sizeFormatted = string.Empty;
        private ulong _sizeBytes;
        private string _typeGuid = string.Empty;
        private string _filePath = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }
        public int Lun
        {
            get => _lun;
            set { _lun = value; OnPropertyChanged(nameof(Lun)); }
        }
        public ulong FirstLBA
        {
            get => _firstLBA;
            set { _firstLBA = value; OnPropertyChanged(nameof(FirstLBA)); }
        }
        public ulong LastLBA
        {
            get => _lastLBA;
            set { _lastLBA = value; OnPropertyChanged(nameof(LastLBA)); }
        }
        public string SizeFormatted
        {
            get => _sizeFormatted;
            set { _sizeFormatted = value; OnPropertyChanged(nameof(SizeFormatted)); }
        }
        public ulong SizeBytes
        {
            get => _sizeBytes;
            set { _sizeBytes = value; OnPropertyChanged(nameof(SizeBytes)); }
        }
        public string TypeGuid
        {
            get => _typeGuid;
            set { _typeGuid = value; OnPropertyChanged(nameof(TypeGuid)); }
        }
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(nameof(FilePath)); }
        }

        public string NumSectors { get; set; } = string.Empty;
        public string SectorSize { get; set; } = "4096";

        public PartitionEntry ToEntry() => new PartitionEntry 
        { 
            Name = Name, 
            Lun = Lun, 
            FirstLBA = FirstLBA, 
            LastLBA = LastLBA, 
            SizeBytes = SizeBytes, 
            TypeGuid = TypeGuid 
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class XmlFileItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private readonly EdlService edl;
        private readonly RawProgramXmlProcessor _xmlProcessor;
        private ObservableCollection<PartitionRow> rows = new();
        private ObservableCollection<PartitionRow> filteredRows = new();
        private ObservableCollection<XmlFileItem> xmlFileItems = new();
        private DispatcherTimer? _portDetectionTimer;
        private CancellationTokenSource? _cts;
        private string? currentPort;
        private string? currentRwMode;
        private string? romImagesPath;
        private string[]? rawProgramFiles;
        private RomPackageInfo _romPackage = RomPackageInfo.Unknown;

        public MainWindow()
        {
            InitializeComponent();
            InitializeLogFile();
            edl = new EdlService(AppendLog, UpdateProgress);
            _xmlProcessor = new RawProgramXmlProcessor(AppendLog);
            InitializeUI();
            ApplyLanguage();
            RefreshPortStatus();
            InitializePortDetectionTimer();
        }

        private void InitializeUI()
        {
            XmlFileList.ItemsSource = xmlFileItems;
        }

        private void InitializePortDetectionTimer()
        {
            _portDetectionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _portDetectionTimer.Tick += (s, e) => RefreshPortStatus();
            _portDetectionTimer.Start();
        }

        #region Language
        private void ApplyLanguage()
        {
            TitleText.Text = Lang.WindowTitle;
            BtnLanguage.Content = LanguageService.GetLanguageDisplayName();
            BtnAbout.Content = Lang.About;
            LblDevPrg.Text = Lang.DeviceProgrammer;
            LblDigest.Text = Lang.Digest;
            LblSig.Text = Lang.Sig;
            BtnEnterFirehose.Content = Lang.EnterFirehose;
            LblSelectRom.Text = Lang.SelectRomFolder;
            BtnLoad.Content = Lang.Load;
            BtnAll.Content = Lang.All;
            BtnNone.Content = Lang.None;
            BtnReadPartitions.Content = Lang.ReadPartitions;
            BtnReadSelected.Content = Lang.ReadSelected;
            BtnWriteSelected.Content = Lang.WriteSelected;
            BtnEraseSelected.Content = Lang.EraseSelected;
            BtnStopAll.Content = Lang.StopAll;
            BtnStartFlash.Content = Lang.StartFlash;
            AutoRebootCheckBox.Content = Lang.AutoReboot;
            ExportXmlCheckBox.Content = Lang.ExportXml;
            ProtectLun5CheckBox.Content = Lang.ProtectLun5;
            SkipPatchXmlCheckBox.Content = Lang.SkipPatchXml;
            ToolTip.SetTip(SkipPatchXmlCheckBox, Lang.SkipPatchXmlTooltip);
            LblLog.Text = Lang.Log;
            LblPort9008.Text = Lang.Port9008;
            BtnClear.Content = Lang.Clear;
            if (PortStatus.Text == "Not detected" || PortStatus.Text == "未检测到")
            {
                PortStatus.Text = Lang.NotDetected;
            }
            
        }

        private async void SwitchLanguage_Click(object? sender, RoutedEventArgs e)
        {
            var result = await ShowMessageBox(
                Lang.LanguageSwitchMessage,
                Lang.RestartRequired,
                MessageBoxButtons.YesNo);

            if (result == MessageBoxResult.Yes)
            {
                bool success = LanguageService.ToggleLanguage();
                if (!success)
                {
                    AppendLog("Warning: Failed to save language settings");
                    await ShowMessageBox(
                        "Failed to save language settings. Please check write permissions.",
                        "Error",
                        MessageBoxButtons.OK);
                    return;
                }

                AppendLog("Language settings saved, restarting...");
                try
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true
                        });
                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.Shutdown();
                        }
                    }
                    else
                    {
                        AppendLog("Cannot find executable path, please restart manually.");
                        await ShowMessageBox(
                            LanguageService.IsChinese ? "无法自动重启，请手动重启应用程序。" : "Cannot auto-restart, please restart the application manually.",
                            LanguageService.IsChinese ? "提示" : "Notice",
                            MessageBoxButtons.OK);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Restart failed: {ex.Message}");
                    await ShowMessageBox(
                        LanguageService.IsChinese ? $"重启失败: {ex.Message}\n请手动重启应用程序。" : $"Restart failed: {ex.Message}\nPlease restart the application manually.",
                        LanguageService.IsChinese ? "错误" : "Error",
                        MessageBoxButtons.OK);
                }
            }
        }
        #endregion

        #region Logging & Progress
        private string _logFilePath = string.Empty;
        private readonly object _logLock = new object();
        
        private void InitializeLogFile()
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OplusEdlTool");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, "app_log.txt");
            try { File.WriteAllText(_logFilePath, $"========== Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n"); }
            catch { }
        }
        
        public void AppendLog(string s)
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"{ts} {s}\n";
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                lock (_logLock)
                {
                    try { File.AppendAllText(_logFilePath, logLine); }
                    catch { }
                }
            }
            
            Dispatcher.UIThread.Post(() =>
            {
                var lines = (Log.Text ?? "").Split('\n');
                if (lines.Length > 500)
                {
                    Log.Text = string.Join("\n", lines.Skip(lines.Length - 500)) + logLine;
                }
                else
                {
                    Log.Text += logLine;
                }
                Log.CaretIndex = Log.Text?.Length ?? 0;
            });
        }

        public void UpdateProgress(int percent)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                var progressBar = this.FindControl<ProgressBar>("Progress");
                if (progressBar != null)
                    progressBar.Value = percent;
            });
        }
        #endregion

        #region Port Detection
        private void RefreshPortStatus()
        {
            try
            {
                var port = Detect9008Port();
                if (!string.IsNullOrEmpty(port))
                {
                    PortStatus.Text = port;
                    PortStatus.Foreground = Avalonia.Media.Brushes.Green;
                }
                else
                {
                    PortStatus.Text = Lang.NotDetected;
                    PortStatus.Foreground = Avalonia.Media.Brushes.Red;
                }
            }
            catch
            {
                PortStatus.Text = "Error";
                PortStatus.Foreground = Avalonia.Media.Brushes.Red;
            }
        }

        private string? Detect9008Port()
        {
            try
            {
                var toolsDir = FindToolsDir();
                if (toolsDir == null) return null;
                var lsusb = System.IO.Path.Combine(toolsDir, "lsusb.exe");
                if (!System.IO.File.Exists(lsusb)) return null;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = lsusb,
                    WorkingDirectory = toolsDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var m = Regex.Match(output, @"Qualcomm HS-USB QDLoader 9008 \(COM(?<n>\d+)\)");
                if (m.Success) return "COM" + m.Groups["n"].Value;
            }
            catch { }
            return null;
        }
        
        private string? FindToolsDir()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "Tools");
                var full = System.IO.Path.GetFullPath(candidate);
                if (System.IO.Directory.Exists(full) && System.IO.File.Exists(System.IO.Path.Combine(full, "fh_loader.exe")))
                {
                    return full;
                }
                var parent = System.IO.Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
        #endregion

        #region Search & Filter
        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var searchText = SearchBox.Text?.Trim().ToLower() ?? "";
            List<PartitionRow> filtered;
            
            if (string.IsNullOrEmpty(searchText))
            {
                filtered = rows.ToList();
            }
            else
            {
                filtered = rows.Where(r => r.Name.ToLower().Contains(searchText)).ToList();
            }
            
            Grid.ItemsSource = null;
            Grid.ItemsSource = filtered;
        }
        #endregion

        #region Window Controls
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void Minimize_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized 
                ? WindowState.Normal 
                : WindowState.Maximized;
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
        #endregion

        #region Button Handlers (Placeholders)
        private void ClearLog_Click(object? sender, RoutedEventArgs e)
        {
            Log.Text = string.Empty;
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try { File.WriteAllText(_logFilePath, $"========== Log Cleared at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n"); }
                catch { }
            }
        }

        private async void About_Click(object? sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(this);
        }


        private async void PickDevPrg_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Device Programmer",
                AllowMultiple = false
            });
            
            if (files.Count > 0)
            {
                DevPrg.Text = files[0].Path.LocalPath;
            }
        }

        private async void PickDigest_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Digest",
                AllowMultiple = false
            });
            
            if (files.Count > 0)
            {
                Digest.Text = files[0].Path.LocalPath;
            }
        }

        private async void PickSig_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Sig",
                AllowMultiple = false
            });
            
            if (files.Count > 0)
            {
                Sig.Text = files[0].Path.LocalPath;
            }
        }

        private async void PickRomFolder_Click(object? sender, RoutedEventArgs e)
        {
            var result = await ShowMessageBox(
                Lang.SelectFolderOrFile,
                Lang.SelectRomSource,
                MessageBoxButtons.YesNoCancel);

            if (result == MessageBoxResult.Cancel) return;

            if (result == MessageBoxResult.Yes)
            {
                await SelectRomFolder();
            }
            else
            {
                await SelectOfpOpsFile();
            }
        }

        private async Task SelectRomFolder()
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select ROM Folder",
                AllowMultiple = false
            });
            
            if (folders.Count == 0) return;

            _romPackage = RomPackageInfo.Unknown;
            var selectedPath = folders[0].Path.LocalPath;
            var imagesPath = FindImagesFolder(selectedPath);
            
            if (imagesPath == null)
            {
                AppendLog(Lang.ImagesFolderNotFound);
                await ShowMessageBox(Lang.ImagesFolderNotFound, Lang.Error, MessageBoxButtons.OK);
                return;
            }

            if (!await ValidateAndLoadRawProgram(imagesPath, selectedPath)) return;

            _romPackage = FirmwarePackageClassifier.ClassifyRomFolder(selectedPath);
            LogRomPackage();
        }

        private async Task SelectOfpOpsFile()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select OFP or OPS ROM File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ROM Files") { Patterns = new[] { "*.ofp", "*.ops" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                }
            });
            
            if (files.Count == 0) return;

            _romPackage = RomPackageInfo.Unknown;
            var filePath = files[0].Path.LocalPath;
            var ext = Path.GetExtension(filePath).ToLower();

            AppendLog($"Selected file: {Path.GetFileName(filePath)}");
            AppendLog("Starting extraction, please wait...");

            string? extractPath = null;
            try
            {
                if (ext == ".ops")
                {
                    var decryptor = new OpsDecryptor(AppendLog);
                    extractPath = await Task.Run(() => decryptor.Decrypt(filePath));
                }
                else if (ext == ".ofp")
                {
                    var decryptor = new OfpDecryptor(AppendLog);
                    extractPath = await Task.Run(() => decryptor.Decrypt(filePath));
                }
                else
                {
                    AppendLog("Unsupported file format");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Extraction error: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(extractPath))
            {
                AppendLog("Extraction failed");
                return;
            }

            AppendLog($"Extraction completed: {extractPath}");
            await MergeSuperImages(extractPath);
            var imagesPath = FindImagesFolder(extractPath) ?? extractPath;
            if (!await ValidateAndLoadRawProgram(imagesPath, extractPath))
            {
                _romPackage = RomPackageInfo.Unknown;
                return;
            }

            LogRomPackage();
        }

        private async Task MergeSuperImages(string extractPath)
        {
            try
            {
                var superFiles = Directory.GetFiles(extractPath, "super.*.img")
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^super\.\d+\.[a-fA-F0-9]+\.img$"))
                    .OrderBy(f =>
                    {
                        var match = Regex.Match(Path.GetFileName(f), @"^super\.(\d+)\.");
                        return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue;
                    })
                    .ToList();

                if (superFiles.Count == 0)
                {
                    AppendLog("No super segment images found");
                    return;
                }

                AppendLog($"Found {superFiles.Count} super segment images");
                var renamedFiles = new List<string>();
                for (int i = 0; i < superFiles.Count; i++)
                {
                    var newName = Path.Combine(extractPath, $"super{i}.img");
                    if (File.Exists(newName) && newName != superFiles[i])
                        File.Delete(newName);
                    
                    if (superFiles[i] != newName)
                    {
                        File.Move(superFiles[i], newName);
                        AppendLog($"Renamed {Path.GetFileName(superFiles[i])} -> super{i}.img");
                    }
                    renamedFiles.Add(newName);
                }
                var toolsDir = FindToolsDir();
                if (toolsDir == null)
                {
                    AppendLog("Tools folder not found, skipping super merge");
                    return;
                }
                
                var simg2imgPath = Path.Combine(toolsDir, "simg2img.exe");
                if (!File.Exists(simg2imgPath))
                {
                    AppendLog("simg2img.exe not found in Tools folder, skipping super merge");
                    return;
                }

                var superOutputPath = Path.Combine(extractPath, "super.img");
                if (File.Exists(superOutputPath))
                {
                    AppendLog("super.img already exists, skipping merge");
                    return;
                }

                var args = string.Join(" ", renamedFiles.Select(f => $"\"{f}\"")) + $" \"{superOutputPath}\"";
                AppendLog($"Merging super images with simg2img...");

                var result = await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = simg2imgPath,
                        Arguments = args,
                        WorkingDirectory = extractPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process == null) return (false, "Failed to start simg2img");
                    
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                        return (true, output);
                    else
                        return (false, error);
                });

                if (result.Item1)
                {
                    AppendLog("Super images merged successfully");
                    foreach (var file in renamedFiles)
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                }
                else
                {
                    AppendLog($"Failed to merge super images: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error merging super images: {ex.Message}");
            }
        }

        private string? FindImagesFolder(string basePath)
        {
            var imagesPath = Path.Combine(basePath, "IMAGES");
            if (Directory.Exists(imagesPath)) return imagesPath;
            if (Path.GetFileName(basePath).Equals("IMAGES", StringComparison.OrdinalIgnoreCase))
                return basePath;
            if (Directory.GetFiles(basePath, "rawprogram*.xml").Length > 0)
                return basePath;

            return null;
        }

        private async Task<bool> ValidateAndLoadRawProgram(string imagesPath, string displayPath)
        {
            var xmlFiles = new List<string>();
            for (int i = 0; i <= 5; i++)
            {
                var xmlFile = Path.Combine(imagesPath, $"rawprogram{i}.xml");
                if (File.Exists(xmlFile)) xmlFiles.Add(xmlFile);
            }
            if (xmlFiles.Count == 0)
            {
                xmlFiles = Directory.GetFiles(imagesPath, "rawprogram*.xml")
                    .OrderBy(f =>
                    {
                        var fileName = Path.GetFileNameWithoutExtension(f);
                        var match = Regex.Match(fileName, @"\d+");
                        return match.Success ? int.Parse(match.Value) : int.MaxValue;
                    })
                    .ToList();
            }

            if (xmlFiles.Count == 0)
            {
                AppendLog(Lang.NoRawprogramFound);
                await ShowMessageBox(Lang.NoRawprogramFound, Lang.Error, MessageBoxButtons.OK);
                return false;
            }

            romImagesPath = imagesPath;
            rawProgramFiles = xmlFiles.ToArray();
            RomPath.Text = displayPath;

            xmlFileItems.Clear();
            foreach (var xmlFile in xmlFiles)
            {
                xmlFileItems.Add(new XmlFileItem
                {
                    Name = Path.GetFileName(xmlFile),
                    FullPath = xmlFile,
                    IsSelected = false
                });
            }
            
            rows.Clear();
            filteredRows.Clear();
            Grid.ItemsSource = null;
            AppendLog(string.Format(Lang.FoundXmlFiles, xmlFiles.Count));
            _ = CheckAndMergeSuperPartitionAsync(displayPath, imagesPath);

            return true;
        }

        private async Task CheckAndMergeSuperPartitionAsync(string romBaseDir, string imagesPath)
        {
            try
            {
                var superImgPath = Path.Combine(imagesPath, "super.img");
                if (File.Exists(superImgPath))
                {
                    AppendLog("super.img already exists");
                    return;
                }

                var superMergeService = new SuperMergeService(AppendLog, UpdateProgress);
                var jsonPath = superMergeService.FindSuperDefJson(romBaseDir);
                
                if (string.IsNullOrEmpty(jsonPath))
                {
                    return;
                }

                var config = superMergeService.ParseSuperDefJson(jsonPath);
                if (config == null)
                {
                    AppendLog("Failed to parse super_def.json");
                    return;
                }

                var partitionsWithPath = config.Partitions.Where(p => !string.IsNullOrEmpty(p.Path)).ToList();
                if (partitionsWithPath.Count == 0)
                {
                    AppendLog("No partitions to merge in super_def.json");
                    return;
                }

                var result = await ShowMessageBox(
                    string.Format(Lang.MergeSuperMessage, partitionsWithPath.Count),
                    Lang.MergeSuperPartition,
                    MessageBoxButtons.YesNo);

                if (result != MessageBoxResult.Yes)
                {
                    AppendLog("Super merge skipped by user");
                    return;
                }

                AppendLog("Starting Super partition merge...");
                
                var success = await superMergeService.MergeSuperAsync(imagesPath, config, romBaseDir);
                
                if (success)
                {
                    AppendLog("Super partition merge completed!");
                    if (rawProgramFiles != null && rawProgramFiles.Length > 0 && rows.Count > 0)
                    {
                        LoadPartitionsFromXml();
                    }
                    else
                    {
                        AppendLog("Please select XML files and click 'Load' to see the updated partition list.");
                    }
                }
                else
                {
                    AppendLog("Super partition merge failed");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error checking super partition: {ex.Message}");
            }
        }

        private void LoadPartitionsFromXml()
        {
            if (rawProgramFiles == null || rawProgramFiles.Length == 0) return;
            if (string.IsNullOrEmpty(romImagesPath))
            {
                romImagesPath = Path.GetDirectoryName(rawProgramFiles[0]);
            }

            rows.Clear();
            int totalPartitions = 0;

            foreach (var file in rawProgramFiles)
            {
                var xmlDir = Path.GetDirectoryName(file) ?? romImagesPath ?? "";
                
                try
                {
                    var xmlDoc = XDocument.Load(file);
                    var programs = xmlDoc.Descendants("program");

                    foreach (var program in programs)
                    {
                        var fileName = program.Attribute("filename")?.Value ?? "";
                        var label = program.Attribute("label")?.Value ?? "";
                        var sizeKB = program.Attribute("size_in_KB")?.Value ?? "0";
                        var startSector = program.Attribute("start_sector")?.Value ?? "0";
                        var numPartitionSectors = program.Attribute("num_partition_sectors")?.Value ?? "0";
                        var physicalPartitionNumber = program.Attribute("physical_partition_number")?.Value ?? "0";
                        double sizeInKB = double.TryParse(sizeKB, out var parsedSize) ? parsedSize : 0;
                        string sizeFormatted;
                        if (sizeInKB >= 1048576) 
                            sizeFormatted = $"{sizeInKB / 1048576:F2} GB";
                        else if (sizeInKB >= 1024) 
                            sizeFormatted = $"{sizeInKB / 1024:F2} MB";
                        else
                            sizeFormatted = $"{sizeInKB:F2} KB";
                        var searchPath = !string.IsNullOrEmpty(romImagesPath) ? romImagesPath : xmlDir;
                        var filePath = string.IsNullOrEmpty(fileName) ? "" : Path.Combine(searchPath, fileName);
                        var fileExists = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
                        string displayFileName = fileName;
                        if (!fileExists && !string.IsNullOrEmpty(fileName) && 
                            Regex.IsMatch(fileName, @"^super\.\d+\.[a-fA-F0-9]+\.img$"))
                        {
                            var superImgPath = Path.Combine(searchPath, "super.img");
                            if (File.Exists(superImgPath))
                            {
                                filePath = superImgPath;
                                fileExists = true;
                                displayFileName = "super.img";
                            }
                        }
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            ulong firstLba = ulong.TryParse(startSector, out var start) ? start : 0;
                            ulong numSectors = ulong.TryParse(numPartitionSectors, out var sectors) ? sectors : 0;
                            ulong lastLba = numSectors > 0 ? firstLba + numSectors - 1 : 0;
                            ulong sizeBytes = (ulong)(sizeInKB * 1024);
                            string actualSizeFormatted = sizeFormatted;
                            if (sizeInKB == 0 && fileExists && File.Exists(filePath))
                            {
                                try
                                {
                                    var fileInfo = new FileInfo(filePath);
                                    sizeBytes = (ulong)fileInfo.Length;
                                    double fileSizeKB = sizeBytes / 1024.0;
                                    if (fileSizeKB >= 1048576) 
                                        actualSizeFormatted = $"{fileSizeKB / 1048576:F2} GB";
                                    else if (fileSizeKB >= 1024) 
                                        actualSizeFormatted = $"{fileSizeKB / 1024:F2} MB";
                                    else
                                        actualSizeFormatted = $"{fileSizeKB:F2} KB";
                                    
                                    if (numSectors == 0)
                                    {
                                        numSectors = sizeBytes / 4096;
                                        lastLba = numSectors > 0 ? firstLba + numSectors - 1 : 0;
                                    }
                                }
                                catch { }
                            }

                            rows.Add(new PartitionRow
                            {
                                IsSelected = fileExists, 
                                Name = label,
                                Lun = int.TryParse(physicalPartitionNumber, out var lun) ? lun : 0,
                                FirstLBA = firstLba,
                                LastLBA = lastLba,
                                SizeFormatted = actualSizeFormatted,
                                SizeBytes = sizeBytes,
                                FilePath = fileExists ? filePath : $"[NOT FOUND] {filePath}",
                                NumSectors = numSectors > 0 ? numSectors.ToString() : numPartitionSectors,
                                SectorSize = "4096"
                            });
                            totalPartitions++;
                        }
                    }

                    AppendLog($"Parsed: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    AppendLog($"Error parsing {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            Dispatcher.UIThread.Post(() =>
            {
                filteredRows.Clear();
                foreach (var r in rows) filteredRows.Add(r);
                Grid.ItemsSource = rows.ToList();
                
                var existingFiles = rows.Count(r => !r.FilePath.StartsWith("[NOT FOUND]"));
                RomInfo.Text = $"Found {totalPartitions} partitions, {existingFiles} files available | {GetRomPackageKindName(_romPackage.Kind)}";
            });
            
            AppendLog($"Loaded {totalPartitions} partitions from {rawProgramFiles.Length} XML files");
        }

        private void SelectAllXml_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var item in xmlFileItems)
            {
                item.IsSelected = true;
            }
        }

        private void SelectNoneXml_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var item in xmlFileItems)
            {
                item.IsSelected = false;
            }
        }

        private void SelectAllPartitions_Checked(object? sender, RoutedEventArgs e)
        {
            if (Grid.ItemsSource is IEnumerable<PartitionRow> items)
            {
                foreach (var row in items)
                {
                    row.IsSelected = true;
                }
            }
        }

        private void SelectAllPartitions_Unchecked(object? sender, RoutedEventArgs e)
        {
            if (Grid.ItemsSource is IEnumerable<PartitionRow> items)
            {
                foreach (var row in items)
                {
                    row.IsSelected = false;
                }
            }
        }

        private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is DataGrid dataGrid && dataGrid.SelectedItem != null)
            {
                dataGrid.SelectedItem = null;
            }
        }

        private async void Grid_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not DataGrid dataGrid) return;
            var point = e.GetPosition(dataGrid);
            var row = GetRowAtPoint(dataGrid, point);
            if (row == null) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Select file for partition: {row.Name}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files") { Patterns = new[] { "*.img", "*.bin", "*.mbn" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                }
            });

            if (files.Count == 0) return;

            var selectedFile = files[0].Path.LocalPath;
            row.FilePath = selectedFile;
            row.IsSelected = true; 
            
            try
            {
                var fileInfo = new FileInfo(selectedFile);
                var fileSizeBytes = (ulong)fileInfo.Length;
                row.SizeBytes = fileSizeBytes;
                row.SizeFormatted = FormatSize(fileSizeBytes);
                var sectorSize = edl.StorageType == "emmc" ? 512 : 4096;
                var numSectors = fileSizeBytes / (ulong)sectorSize;
                row.NumSectors = numSectors.ToString();
                if (row.FirstLBA > 0)
                {
                    row.LastLBA = row.FirstLBA + numSectors - 1;
                }
                
                AppendLog($"Selected file for {row.Name}: {Path.GetFileName(selectedFile)} ({FormatSize(fileSizeBytes)})");
            }
            catch (Exception ex)
            {
                AppendLog($"Warning: Could not read file info: {ex.Message}");
            }
            RefreshDataGrid();
        }

        private PartitionRow? GetRowAtPoint(DataGrid dataGrid, Point point)
        {
            var element = dataGrid.InputHitTest(point) as Avalonia.Visual;
            while (element != null)
            {
                if (element is DataGridRow dgRow && dgRow.DataContext is PartitionRow partitionRow)
                {
                    return partitionRow;
                }
                element = element.GetVisualParent() as Avalonia.Visual;
            }
            
            return null;
        }

        private void RefreshDataGrid()
        {
            Grid.InvalidateVisual();
        }

        private async void LoadSelectedXml_Click(object? sender, RoutedEventArgs e)
        {
            var selectedXmls = xmlFileItems.Where(x => x.IsSelected).ToList();
            
            if (selectedXmls.Count == 0)
            {
                AppendLog(Lang.NoXmlSelected);
                await ShowMessageBox(Lang.PleaseSelectXml, Lang.NoSelection, MessageBoxButtons.OK);
                return;
            }

            rawProgramFiles = selectedXmls.Select(x => x.FullPath).ToArray();
            
            AppendLog(string.Format(Lang.LoadingXmlFiles, selectedXmls.Count, string.Join(", ", selectedXmls.Select(x => x.Name))));
            LoadPartitionsFromXml();
        }

        private async void EnterFirehose_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DevPrg.Text))
            {
                AppendLog("Please select Device Programmer file");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            
            BtnEnterFirehose.IsEnabled = false;
            try
            {
                AppendLog("Waiting for EDL port (9008)...");
                var port = await edl.WaitForEdlPortAsync(token);
                
                if (token.IsCancellationRequested)
                {
                    AppendLog("Enter Firehose cancelled.");
                    return;
                }
                
                AppendLog("Device connected: " + port);

                AppendLog("Sending device programmer...");
                var ok = await edl.SendProgrammerAsync(port, DevPrg.Text);
                if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                if (!ok) { AppendLog("Failed to send programmer"); return; }

                if (!string.IsNullOrWhiteSpace(Digest.Text))
                {
                    AppendLog("Sending digest...");
                    ok = await edl.SendDigestsAsync(port, Digest.Text);
                    if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                    if (!ok) { AppendLog("Failed to send digest"); return; }
                }

                AppendLog("Sending verify command...");
                ok = await edl.SendVerifyAsync(port);
                if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                if (!ok) { AppendLog("Failed to verify"); return; }

                if (!string.IsNullOrWhiteSpace(Sig.Text))
                {
                    AppendLog("Sending sig...");
                    ok = await edl.SendDigestsAsync(port, Sig.Text);
                    if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                    if (!ok) { AppendLog("Failed to send sig"); return; }
                }

                AppendLog("Sending sha256init command...");
                ok = await edl.SendSha256InitAsync(port);
                if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                if (!ok) { AppendLog("Failed sha256init"); return; }

                AppendLog("Configuring...");
                ok = await edl.ConfigureAsync(port);
                if (token.IsCancellationRequested) { AppendLog("Enter Firehose cancelled."); return; }
                if (!ok) { AppendLog("Failed to configure"); return; }

                currentPort = port;
                AppendLog("Firehose mode entered successfully!");
            }
            catch (OperationCanceledException)
            {
                AppendLog("Enter Firehose cancelled by user.");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnEnterFirehose.IsEnabled = true;
            }
        }

        private async void ReadPartitions_Click(object? sender, RoutedEventArgs e)
        {
            BtnReadPartitions.IsEnabled = false;
            try
            {
                AppendLog("Waiting for EDL port (9008)...");
                var port = await edl.WaitForEdlPortAsync();
                AppendLog("Device connected: " + port);

                AppendLog("Configuring...");
                var ok = await edl.ConfigureAsync(port);
                if (!ok) { AppendLog("Failed to configure"); return; }

                AppendLog("Testing RW mode...");
                var mode = await edl.TestRwModeAsync(port);
                AppendLog("RW mode: " + mode.Item1 + (string.IsNullOrEmpty(mode.Item2) ? "" : (" " + mode.Item2)));

                currentPort = port;
                currentRwMode = mode.Item1;

                AppendLog("Reading partition table...");
                var parts = await edl.ReadPartitionTableAsync(port, mode.Item1);
                if (parts == null || parts.Count == 0) { AppendLog("No partitions found"); return; }

                var newRows = new ObservableCollection<PartitionRow>();
                foreach (var p in parts)
                {
                    newRows.Add(new PartitionRow
                    {
                        Name = p.Name,
                        Lun = p.Lun,
                        FirstLBA = p.FirstLBA,
                        LastLBA = p.LastLBA,
                        SizeBytes = p.SizeBytes,
                        SizeFormatted = FormatSize(p.SizeBytes),
                        TypeGuid = p.TypeGuid
                    });
                }
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    rows.Clear();
                    foreach (var r in newRows) rows.Add(r);
                    
                    filteredRows.Clear();
                    foreach (var r in newRows) filteredRows.Add(r);
                    
                    Grid.ItemsSource = newRows.ToList();
                });
                
                AppendLog($"Found {parts.Count} partitions");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                BtnReadPartitions.IsEnabled = true;
            }
        }
        
        private static string FormatSize(ulong bytes)
        {
            const double KB = 1024;
            const double MB = KB * 1024;
            const double GB = MB * 1024;
            
            if (bytes >= GB)
                return Math.Round(bytes / GB, 2) + " GB";
            if (bytes >= MB)
                return Math.Round(bytes / MB, 2) + " MB";
            if (bytes >= KB)
                return Math.Round(bytes / KB, 2) + " KB";
            return bytes + " B";
        }

        private async void ReadSelected_Click(object? sender, RoutedEventArgs e)
        {
            var result = await ShowMessageBox(
                Lang.SelectReadMethodMessage,
                Lang.SelectReadMethod,
                MessageBoxButtons.YesNoCancel);

            if (result == MessageBoxResult.Cancel) return;

            if (result == MessageBoxResult.Yes)
            {
                await ReadSelectedPartitionsAuto();
            }
            else
            {
                await ReadByCustomXml();
            }
        }

        private async Task ReadSelectedPartitionsAuto()
        {
            var selected = rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) { AppendLog(Lang.NoPartitionsSelected); return; }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder for backup",
                AllowMultiple = false
            });
            
            if (folders.Count == 0) return;
            var destBase = folders[0].Path.LocalPath;

            var superPartitions = selected.Where(r => EdlService.IsSuperPartition(r.Name)).ToList();
            var otherPartitions = selected.Where(r => !EdlService.IsSuperPartition(r.Name))
                                          .OrderBy(r => r.SizeBytes) 
                                          .ToList();

            _cts = new CancellationTokenSource();
            BtnReadSelected.IsEnabled = false;
            try
            {
                AppendLog(Lang.WaitingForEdl);
                var port = await edl.WaitForEdlPortAsync(_cts.Token);
                AppendLog(Lang.DeviceConnected + port);

                AppendLog(Lang.Configuring);
                var ok = await edl.ConfigureAsync(port);
                if (!ok) { AppendLog(Lang.ConfigureFailed); return; }

                AppendLog(Lang.TestingRwMode);
                var mode = await edl.TestRwModeAsync(port);
                AppendLog("RW mode: " + mode.rwmode);

                if (otherPartitions.Count > 0)
                {
                    AppendLog($"Backing up {otherPartitions.Count} partition(s) first...");
                    foreach (var r in otherPartitions)
                    {
                        if (_cts.Token.IsCancellationRequested) { AppendLog("Task cancelled"); break; }
                        AppendLog($"Reading partition: {r.Name} ({FormatSize(r.SizeBytes)})...");
                        var outPath = await edl.BackupPartitionAsync(port, mode.rwmode, r.ToEntry());
                        if (outPath == null) { AppendLog("Failed: " + r.Name); continue; }
                        var dest = Path.Combine(destBase, r.Name + ".img");
                        try { File.Copy(outPath, dest, true); AppendLog("Saved: " + dest); }
                        catch { AppendLog("Copy failed: " + r.Name); }
                    }
                }

                if (superPartitions.Count > 0 && !_cts.Token.IsCancellationRequested)
                {
                    AppendLog("Now backing up super partition (this may take a while)...");
                    foreach (var r in superPartitions)
                    {
                        if (_cts.Token.IsCancellationRequested) { AppendLog("Task cancelled"); break; }
                        AppendLog($"Reading super partition ({FormatSize(r.SizeBytes)})...");
                        var outPath = await edl.BackupSuperPartitionAsync(port, mode.rwmode, destBase);
                        if (outPath == null) { AppendLog("Failed: " + r.Name); continue; }
                        var dest = Path.Combine(destBase, "super.img");
                        if (outPath != dest)
                        {
                            try 
                            { 
                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(outPath, dest); 
                                AppendLog("Saved: " + dest); 
                            }
                            catch { AppendLog("Rename failed, file at: " + outPath); }
                        }
                        else
                        {
                            AppendLog("Saved: " + dest);
                        }
                    }
                }

                AppendLog("Read selected partitions completed");
                if (ExportXmlCheckBox.IsChecked == true)
                {
                    ExportPartitionsToXml(selected, destBase);
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Task cancelled by user");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnReadSelected.IsEnabled = true;
            }
        }

        private async Task ReadByCustomXml()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select XML files for reading partitions",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("XML Files") { Patterns = new[] { "*.xml" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                }
            });
            
            if (files.Count == 0)
            {
                AppendLog("No XML files selected");
                return;
            }

            var xmlFiles = files.Select(f => f.Path.LocalPath).ToList();

            _cts = new CancellationTokenSource();
            BtnReadSelected.IsEnabled = false;
            try
            {
                AppendLog(Lang.WaitingForEdl);
                var port = await edl.WaitForEdlPortAsync(_cts.Token);
                AppendLog(Lang.DeviceConnected + port);

                AppendLog(Lang.Configuring);
                var ok = await edl.ConfigureAsync(port);
                if (!ok) { AppendLog(Lang.ConfigureFailed); return; }

                AppendLog(Lang.TestingRwMode);
                var mode = await edl.TestRwModeAsync(port);
                AppendLog("RW mode: " + mode.rwmode + (string.IsNullOrEmpty(mode.gptmainMode) ? "" : (" " + mode.gptmainMode)));

                AppendLog($"Reading by custom XML ({xmlFiles.Count} file(s))...");
                var outDir = await edl.ReadByXmlAsync(port, xmlFiles, mode.rwmode);
                
                if (outDir == null)
                {
                    AppendLog("Failed to read by XML");
                }
                else
                {
                    AppendLog($"Read by XML completed. Output: {outDir}");
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Task cancelled by user");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnReadSelected.IsEnabled = true;
            }
        }

        private void ExportPartitionsToXml(List<PartitionRow> partitions, string outputDir)
        {
            try
            {
                var groupedByLun = partitions.GroupBy(r => r.Lun).OrderBy(g => g.Key);

                foreach (var lunGroup in groupedByLun)
                {
                    var lun = lunGroup.Key;
                    var xmlPath = Path.Combine(outputDir, $"rawprogram_{lun}_backup.xml");
                    var doc = new XDocument(
                        new XDeclaration("1.0", "utf-8", null),
                        new XElement("data")
                    );
                    var dataElement = doc.Element("data")!;
                    dataElement.Add(new XComment(" Generated by OPLUS EDL Tool "));
                    dataElement.Add(new XComment($" Export time: {DateTime.Now:yyyy-MM-dd HH:mm:ss} "));
                    dataElement.Add(new XComment($" LUN {lun} - {lunGroup.Count()} partitions "));

                    foreach (var partition in lunGroup.OrderBy(p => p.FirstLBA))
                    {
                        var sectorSize = 4096;
                        if (!string.IsNullOrEmpty(partition.SectorSize) && int.TryParse(partition.SectorSize, out var parsedSize))
                            sectorSize = parsedSize;

                        var numSectors = partition.LastLBA - partition.FirstLBA + 1;
                        if (!string.IsNullOrEmpty(partition.NumSectors) && ulong.TryParse(partition.NumSectors, out var parsedNumSectors))
                            numSectors = parsedNumSectors;

                        var startByteHex = $"0x{partition.FirstLBA * (ulong)sectorSize:x}";
                        var sizeInKB = (numSectors * (ulong)sectorSize) / 1024.0;
                        var fileName = partition.Name + ".img";

                        var programElement = new XElement("program",
                            new XAttribute("SECTOR_SIZE_IN_BYTES", sectorSize.ToString()),
                            new XAttribute("file_sector_offset", "0"),
                            new XAttribute("filename", fileName),
                            new XAttribute("label", partition.Name),
                            new XAttribute("num_partition_sectors", numSectors.ToString()),
                            new XAttribute("partofsingleimage", "false"),
                            new XAttribute("physical_partition_number", lun.ToString()),
                            new XAttribute("readbackverify", "false"),
                            new XAttribute("size_in_KB", sizeInKB.ToString("F1")),
                            new XAttribute("sparse", "false"),
                            new XAttribute("start_byte_hex", startByteHex),
                            new XAttribute("start_sector", partition.FirstLBA.ToString())
                        );

                        dataElement.Add(programElement);
                    }

                    doc.Save(xmlPath);
                    AppendLog(string.Format(Lang.ExportedXml, xmlPath));
                }
            }
            catch (Exception ex)
            {
                AppendLog(string.Format(Lang.ExportXmlFailed, ex.Message));
            }
        }

        private static readonly string[] GptMainPartitionNames = new[] { 
            "PrimaryGPT", "gpt_main0", "gpt_main1", "gpt_main2", "gpt_main3", "gpt_main4", "gpt_main5" 
        };
        private static readonly string[] GptBackupPartitionNames = new[] { 
            "BackupGPT", "gpt_backup0", "gpt_backup1", "gpt_backup2", "gpt_backup3", "gpt_backup4", "gpt_backup5" 
        };
        private static readonly string[] GptPartitionNames = GptMainPartitionNames.Concat(GptBackupPartitionNames).ToArray();
        
        private List<PartitionRow> FilterGptPartitions(List<PartitionRow> gptPartitions)
        {
            var result = new List<PartitionRow>();
            var processedLuns = new HashSet<int>();
            
            foreach (var p in gptPartitions.Where(p => GptMainPartitionNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            {
                result.Add(p);
                processedLuns.Add(p.Lun);
                AppendLog($"[GPT] Will flash {p.Name} (LUN{p.Lun})");
            }
            
            foreach (var p in gptPartitions.Where(p => GptBackupPartitionNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            {
                if (!processedLuns.Contains(p.Lun))
                {
                    result.Add(p);
                    AppendLog($"[GPT] Will flash {p.Name} (LUN{p.Lun}) - no gpt_main found");
                }
                else
                {
                    AppendLog($"[GPT] Skipping {p.Name} (LUN{p.Lun}) - gpt_main exists");
                }
            }
            
            return result;
        }

        private async void WriteSelected_Click(object? sender, RoutedEventArgs e)
        {
            var allSelected = rows.Where(r => r.IsSelected && !string.IsNullOrEmpty(r.FilePath) && !r.FilePath.StartsWith("[NOT FOUND]")).ToList();
            
            if (allSelected.Count == 0)
            {
                AppendLog(Lang.NoValidPartitions);
                await ShowMessageBox(Lang.NoValidPartitions + "\n\nTip: Double-click on a partition row to select a file for flashing.", Lang.NoSelection, MessageBoxButtons.OK);
                return;
            }
            var gptPartitionsRaw = allSelected.Where(r => GptPartitionNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var normalPartitions = allSelected.Where(r => !GptPartitionNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            
            var gptPartitions = FilterGptPartitions(gptPartitionsRaw);
            
            if (normalPartitions.Count > 0)
            {
                AppendLog($"[Info] Normal partitions (spoof flash): {string.Join(", ", normalPartitions.Take(5).Select(r => r.Name))}{(normalPartitions.Count > 5 ? $" and {normalPartitions.Count - 5} more" : "")}");
            }
            
            var totalCount = gptPartitions.Count + normalPartitions.Count;
            if (totalCount == 0) { AppendLog(Lang.NoValidPartitions); return; }
            var confirmMsg = string.Format(Lang.ConfirmFlashMessage, totalCount) +
                string.Join(", ", gptPartitions.Concat(normalPartitions).Take(10).Select(r => r.Name)) + 
                (totalCount > 10 ? string.Format(Lang.AndMore, totalCount - 10) : "");
            var result = await ShowMessageBox(confirmMsg, Lang.ConfirmFlash, MessageBoxButtons.YesNo);
            if (result != MessageBoxResult.Yes) return;
            var persistPartition = normalPartitions.FirstOrDefault(r => r.Name.Equals("persist", StringComparison.OrdinalIgnoreCase));
            if (persistPartition != null)
            {
                var persistResult = await ShowMessageBox(
                    Lang.PersistPartitionWarningMessage,
                    Lang.PersistPartitionWarning,
                    MessageBoxButtons.YesNo);
                
                if (persistResult != MessageBoxResult.Yes)
                {
                    normalPartitions = normalPartitions.Where(r => !r.Name.Equals("persist", StringComparison.OrdinalIgnoreCase)).ToList();
                    AppendLog(Lang.PersistPartitionSkipped);
                }
            }
            var ocdtPartition = normalPartitions.FirstOrDefault(r => 
                r.Name.Equals("ocdt", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(r.FilePath) && 
                !r.FilePath.StartsWith("[NOT FOUND]"));
            if (ocdtPartition != null)
            {
                var ocdtResult = await ShowMessageBox(
                    Lang.OcdtPartitionWarningMessage,
                    Lang.OcdtPartitionWarning,
                    MessageBoxButtons.YesNo);
                
                if (ocdtResult != MessageBoxResult.Yes)
                {
                    normalPartitions = normalPartitions.Where(r => !r.Name.Equals("ocdt", StringComparison.OrdinalIgnoreCase)).ToList();
                    AppendLog(Lang.OcdtPartitionSkipped);
                }
            }

            _cts = new CancellationTokenSource();
            BtnWriteSelected.IsEnabled = false;
            try
            {
                AppendLog(Lang.WaitingForEdl);
                var port = await edl.WaitForEdlPortAsync(_cts.Token);
                AppendLog(Lang.DeviceConnected + port);

                AppendLog(Lang.Configuring);
                var ok = await edl.ConfigureAsync(port);
                if (!ok) { AppendLog(Lang.ConfigureFailed); return; }
                var protectLun5 = ProtectLun5CheckBox.IsChecked == true;
                if (protectLun5)
                {
                    var lun5Count = allSelected.Count(p => p.Lun == 5);
                    if (lun5Count > 0)
                    {
                        AppendLog($"[Protect LUN5] Skipping {lun5Count} partition(s) in LUN5");
                    }
                }

                var sectorSize = edl.StorageType == "emmc" ? 512 : 4096;
                if (gptPartitions.Count > 0)
                {
                    AppendLog("Step 1: Flashing GPT partitions (normal mode)...");
                    var rwMode = await edl.TestRwModeAsync(port);
                    
                    foreach (var partition in gptPartitions)
                    {
                        if (_cts.Token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                        if (protectLun5 && partition.Lun == 5)
                        {
                            AppendLog($"[Protect LUN5] Skipped: {partition.Name}");
                            continue;
                        }
                        
                        AppendLog($"[GPT] Flashing {partition.Name} (LUN{partition.Lun})...");
                        ok = await edl.FlashPartitionNormalAsync(port, rwMode.rwmode, partition.ToEntry(), partition.FilePath);
                        if (!ok)
                        {
                            AppendLog($"[GPT] Failed to flash {partition.Name}");
                            return;
                        }
                        AppendLog($"[GPT] {partition.Name} flashed successfully");
                    }
                }
                if (normalPartitions.Count > 0)
                {
                    AppendLog("Step 2: Flashing normal partitions (spoof mode)...");
                    
                    var flashTasks = new List<EdlService.FlashPartitionInfo>();
                    foreach (var partition in normalPartitions)
                    {
                        if (protectLun5 && partition.Lun == 5)
                        {
                            AppendLog($"[Protect LUN5] Skipped: {partition.Name}");
                            continue;
                        }

                        flashTasks.Add(new EdlService.FlashPartitionInfo
                        {
                            Name = partition.Name,
                            FilePath = partition.FilePath,
                            Lun = partition.Lun.ToString(),
                            StartSector = partition.FirstLBA,
                            NumSectors = partition.LastLBA - partition.FirstLBA + 1,
                            SectorSize = sectorSize
                        });
                    }

                    if (flashTasks.Count > 0)
                    {
                        ok = await edl.FlashPartitionsWithSpoofAsync(port, flashTasks, _cts.Token);
                        if (!ok)
                        {
                            AppendLog(Lang.FlashFailed);
                            return;
                        }
                    }
                }

                AppendLog(Lang.FlashCompleted);

                if (rawProgramFiles != null && rawProgramFiles.Length > 0)
                {
                    AppendLog(SkipPatchXmlCheckBox.IsChecked == true 
                        ? "[Skip Patch XML] Skipping patch XML files" 
                        : "Applying patch files...");
                    var patchMode = await edl.TestRwModeAsync(port);
                    var patchRwMode = patchMode.rwmode;
                    var patchXmlFiles = rawProgramFiles.AsEnumerable();
                    if (SkipPatchXmlCheckBox.IsChecked == true)
                    {
                        patchXmlFiles = Enumerable.Empty<string>();
                    }
                    else if (protectLun5)
                    {
                        patchXmlFiles = rawProgramFiles.Where(f => 
                            !Path.GetFileName(f).Equals("rawprogram5.xml", StringComparison.OrdinalIgnoreCase));
                        AppendLog("[Protect LUN5] Skipping patch5.xml");
                    }
                    
                    var patchCount = await edl.WritePatchXmlsAsync(port, patchXmlFiles, romImagesPath, patchRwMode);
                    
                    if (patchCount > 0)
                    {
                        AppendLog($"Patch files applied: {patchCount} file(s)");
                    }
                    else if (SkipPatchXmlCheckBox.IsChecked == true)
                    {
                        AppendLog("[Skip Patch XML] Skipped by user setting");
                    }
                    else
                    {
                        AppendLog("No patch files were applied (files may not exist)");
                    }
                    
                    if (_romPackage.IsThirdParty)
                    {
                        AppendLog(Lang.BootPartitionSkippedNonOfficial);
                    }
                    else
                    {
                        await edl.SendSetBootableStorageDriveAsync(port);
                    }
                }

                if (AutoRebootCheckBox.IsChecked == true)
                {
                    AppendLog("Auto reboot is enabled, rebooting device...");
                    var rebootOk = await edl.RebootToEdlAsync(port);
                    if (rebootOk)
                    {
                        AppendLog("Device reboot command sent successfully!");
                    }
                    else
                    {
                        AppendLog("Warning: Failed to send reboot command");
                    }
                }

                AppendLog(Lang.AllOperationsCompleted);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Task cancelled by user");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnWriteSelected.IsEnabled = true;
            }
        }

        private async void StartFlash_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DevPrg.Text) || 
                string.IsNullOrWhiteSpace(Digest.Text) || 
                string.IsNullOrWhiteSpace(Sig.Text))
            {
                AppendLog("Please load DevPrg, Digest and Sig files first");
                await ShowMessageBox("Please load DevPrg, Digest and Sig files first", "Error", MessageBoxButtons.OK);
                return;
            }

            if (rawProgramFiles == null || rawProgramFiles.Length == 0 || string.IsNullOrEmpty(romImagesPath))
            {
                AppendLog(Lang.NoRomLoaded);
                return;
            }

            var allSelected = rows.Where(r => r.IsSelected && !string.IsNullOrEmpty(r.FilePath) && !r.FilePath.StartsWith("[NOT FOUND]")).ToList();
            var gptPartitionsRaw = allSelected.Where(r => GptPartitionNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var normalPartitions = allSelected.Where(r => !GptPartitionNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var gptPartitions = FilterGptPartitions(gptPartitionsRaw);
            
            if (normalPartitions.Count > 0)
            {
                AppendLog($"[Info] Normal partitions (spoof flash): {string.Join(", ", normalPartitions.Take(5).Select(r => r.Name))}{(normalPartitions.Count > 5 ? $" and {normalPartitions.Count - 5} more" : "")}");
            }
            
            var totalCount = gptPartitions.Count + normalPartitions.Count;
            if (totalCount == 0) { AppendLog(Lang.NoValidPartitions); return; }

            var confirmMsg = string.Format(Lang.ConfirmFlashMessage, totalCount) +
                string.Join(", ", gptPartitions.Concat(normalPartitions).Take(10).Select(r => r.Name)) + 
                (totalCount > 10 ? string.Format(Lang.AndMore, totalCount - 10) : "");
            var result = await ShowMessageBox(confirmMsg, Lang.ConfirmFlash, MessageBoxButtons.YesNo);
            if (result != MessageBoxResult.Yes) return;

            var persistPartition = normalPartitions.FirstOrDefault(r => r.Name.Equals("persist", StringComparison.OrdinalIgnoreCase));
            if (persistPartition != null)
            {
                var persistResult = await ShowMessageBox(
                    Lang.PersistPartitionWarningMessage,
                    Lang.PersistPartitionWarning,
                    MessageBoxButtons.YesNo);
                
                if (persistResult != MessageBoxResult.Yes)
                {
                    normalPartitions = normalPartitions.Where(r => !r.Name.Equals("persist", StringComparison.OrdinalIgnoreCase)).ToList();
                    AppendLog(Lang.PersistPartitionSkipped);
                }
            }

            var ocdtPartition = normalPartitions.FirstOrDefault(r => 
                r.Name.Equals("ocdt", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(r.FilePath) && 
                !r.FilePath.StartsWith("[NOT FOUND]"));
            if (ocdtPartition != null)
            {
                var ocdtResult = await ShowMessageBox(
                    Lang.OcdtPartitionWarningMessage,
                    Lang.OcdtPartitionWarning,
                    MessageBoxButtons.YesNo);
                
                if (ocdtResult != MessageBoxResult.Yes)
                {
                    normalPartitions = normalPartitions.Where(r => !r.Name.Equals("ocdt", StringComparison.OrdinalIgnoreCase)).ToList();
                    AppendLog(Lang.OcdtPartitionSkipped);
                }
            }

            _cts = new CancellationTokenSource();
            BtnStartFlash.IsEnabled = false;
            BtnWriteSelected.IsEnabled = false;
            BtnEnterFirehose.IsEnabled = false;

            try
            {
                var token = _cts.Token;
                AppendLog(Lang.WaitingForEdl);
                var port = await edl.WaitForEdlPortAsync(token);
                AppendLog(Lang.DeviceConnected + port);
                AppendLog("Checking if device is in Firehose mode...");
                var isInFirehose = await edl.ConfigureAsync(port);

                if (!isInFirehose)
                {
                    AppendLog("Device not in Firehose mode, sending programmer files...");

                    AppendLog("Sending programmer...");
                    var ok = await edl.SendProgrammerAsync(port, DevPrg.Text);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed to send programmer"); return; }

                    AppendLog("Sending digest...");
                    ok = await edl.SendDigestsAsync(port, Digest.Text);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed to send digest"); return; }

                    AppendLog("Sending verify command...");
                    ok = await edl.SendVerifyAsync(port);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed to verify"); return; }

                    AppendLog("Sending sig...");
                    ok = await edl.SendDigestsAsync(port, Sig.Text);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed to send sig"); return; }

                    AppendLog("Sending sha256init command...");
                    ok = await edl.SendSha256InitAsync(port);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed sha256init"); return; }

                    AppendLog("Configuring...");
                    ok = await edl.ConfigureAsync(port);
                    if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                    if (!ok) { AppendLog("Failed to configure"); return; }

                    AppendLog("Firehose mode entered successfully!");
                    await Task.Delay(200, token);
                }
                else
                {
                    AppendLog("Device already in Firehose mode");
                }

                currentPort = port;

                AppendLog("Starting flash process...");
                var protectLun5 = ProtectLun5CheckBox.IsChecked == true;
                if (protectLun5)
                {
                    var lun5Count = allSelected.Count(p => p.Lun == 5);
                    if (lun5Count > 0)
                    {
                        AppendLog($"[Protect LUN5] Skipping {lun5Count} partition(s) in LUN5");
                    }
                }

                var sectorSize = edl.StorageType == "emmc" ? 512 : 4096;
                bool flashOk;
                if (gptPartitions.Count > 0)
                {
                    AppendLog("Step 1: Flashing GPT partitions (normal mode)...");
                    var rwMode = await edl.TestRwModeAsync(port);
                    
                    foreach (var partition in gptPartitions)
                    {
                        if (token.IsCancellationRequested) { AppendLog("Task cancelled"); return; }
                        if (protectLun5 && partition.Lun == 5)
                        {
                            AppendLog($"[Protect LUN5] Skipped: {partition.Name}");
                            continue;
                        }
                        
                        AppendLog($"[GPT] Flashing {partition.Name} (LUN{partition.Lun})...");
                        flashOk = await edl.FlashPartitionNormalAsync(port, rwMode.rwmode, partition.ToEntry(), partition.FilePath);
                        if (!flashOk)
                        {
                            AppendLog($"[GPT] Failed to flash {partition.Name}");
                            return;
                        }
                        AppendLog($"[GPT] {partition.Name} flashed successfully");
                    }
                }

                if (normalPartitions.Count > 0)
                {
                    AppendLog("Step 2: Flashing normal partitions (spoof mode)...");
                    
                    var flashTasks = new List<EdlService.FlashPartitionInfo>();
                    foreach (var partition in normalPartitions)
                    {
                        if (protectLun5 && partition.Lun == 5)
                        {
                            AppendLog($"[Protect LUN5] Skipped: {partition.Name}");
                            continue;
                        }

                        flashTasks.Add(new EdlService.FlashPartitionInfo
                        {
                            Name = partition.Name,
                            FilePath = partition.FilePath,
                            Lun = partition.Lun.ToString(),
                            StartSector = partition.FirstLBA,
                            NumSectors = partition.LastLBA - partition.FirstLBA + 1,
                            SectorSize = sectorSize
                        });
                    }

                    if (flashTasks.Count > 0)
                    {
                        flashOk = await edl.FlashPartitionsWithSpoofAsync(port, flashTasks, token);
                        if (!flashOk)
                        {
                            AppendLog(Lang.FlashFailed);
                            return;
                        }
                    }
                }

                AppendLog(Lang.FlashCompleted);

                if (rawProgramFiles != null && rawProgramFiles.Length > 0)
                {
                    AppendLog(SkipPatchXmlCheckBox.IsChecked == true 
                        ? "[Skip Patch XML] Skipping patch XML files" 
                        : "Applying patch files...");
                    
                    var patchMode = await edl.TestRwModeAsync(port);
                    var patchRwMode = patchMode.rwmode;
                    
                    var patchXmlFiles = rawProgramFiles.AsEnumerable();
                    if (SkipPatchXmlCheckBox.IsChecked == true)
                    {
                        patchXmlFiles = Enumerable.Empty<string>();
                    }
                    else if (protectLun5)
                    {
                        patchXmlFiles = rawProgramFiles.Where(f => 
                            !Path.GetFileName(f).Equals("rawprogram5.xml", StringComparison.OrdinalIgnoreCase));
                        AppendLog("[Protect LUN5] Skipping patch5.xml");
                    }
                    
                    var patchCount = await edl.WritePatchXmlsAsync(port, patchXmlFiles, romImagesPath, patchRwMode);
                    
                    if (patchCount > 0)
                    {
                        AppendLog($"Patch files applied: {patchCount} file(s)");
                    }
                    else if (SkipPatchXmlCheckBox.IsChecked == true)
                    {
                        AppendLog("[Skip Patch XML] Skipped by user setting");
                    }
                    else
                    {
                        AppendLog("No patch files were applied (files may not exist)");
                    }
                    
                    if (_romPackage.IsThirdParty)
                    {
                        AppendLog(Lang.BootPartitionSkippedNonOfficial);
                    }
                    else
                    {
                        await edl.SendSetBootableStorageDriveAsync(port);
                    }
                }

                if (AutoRebootCheckBox.IsChecked == true)
                {
                    AppendLog("Auto reboot is enabled, rebooting device...");
                    var rebootOk = await edl.RebootToEdlAsync(port);
                    if (rebootOk)
                    {
                        AppendLog("Device reboot command sent successfully!");
                    }
                    else
                    {
                        AppendLog("Warning: Failed to send reboot command");
                    }
                }

                AppendLog(Lang.AllOperationsCompleted);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Task cancelled by user");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnStartFlash.IsEnabled = true;
                BtnWriteSelected.IsEnabled = true;
                BtnEnterFirehose.IsEnabled = true;
            }
        }

        private async void EraseSelected_Click(object? sender, RoutedEventArgs e)
        {
            var selected = rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) { AppendLog(Lang.NoPartitionsSelected); return; }

            var result = await ShowMessageBox(
                string.Format(Lang.ConfirmEraseMessage, selected.Count), 
                Lang.ConfirmErase, 
                MessageBoxButtons.YesNo);
            if (result != MessageBoxResult.Yes) return;

            _cts = new CancellationTokenSource();
            BtnEraseSelected.IsEnabled = false;
            try
            {
                AppendLog(Lang.WaitingForEdl);
                var port = await edl.WaitForEdlPortAsync(_cts.Token);
                AppendLog(Lang.DeviceConnected + port);

                AppendLog(Lang.Configuring);
                var ok = await edl.ConfigureAsync(port);
                if (!ok) { AppendLog(Lang.ConfigureFailed); return; }

                AppendLog(Lang.TestingRwMode);
                var mode = await edl.TestRwModeAsync(port);
                AppendLog("RW mode: " + mode.rwmode);

                foreach (var r in selected)
                {
                    if (_cts.Token.IsCancellationRequested) { AppendLog("Task cancelled"); break; }
                    AppendLog($"Erasing partition: {r.Name}...");
                    ok = await edl.ErasePartitionAsync(port, mode.rwmode, r.ToEntry());
                    AppendLog(ok ? ("Erased: " + r.Name) : ("Failed: " + r.Name));
                }
                AppendLog("Erase selected partitions completed");
            }
            catch (OperationCanceledException)
            {
                AppendLog("Task cancelled by user");
            }
            catch (Exception ex)
            {
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                _cts = null;
                BtnEraseSelected.IsEnabled = true;
            }
        }

        private void StopAll_Click(object? sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
                AppendLog(Lang.StoppingTasks);
                AppendLog("All tasks have been stopped.");
            }
            else
            {
                AppendLog(Lang.NoTasksRunning);
            }
            
            try
            {
                foreach (var procName in new[] { "fh_loader", "QSaharaServer", "lsusb", "lpmake", "simg2img" })
                {
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(procName))
                    {
                        try
                        {
                            proc.Kill();
                            AppendLog($"Killed process: {procName}");
                        }
                        catch { }
                    }
                }
            }
            catch { }
            
            BtnEnterFirehose.IsEnabled = true;
            BtnReadPartitions.IsEnabled = true;
            BtnReadSelected.IsEnabled = true;
            BtnWriteSelected.IsEnabled = true;
            BtnEraseSelected.IsEnabled = true;
            
            UpdateProgress(0);
        }
        #endregion

        #region Helper Methods
        private enum MessageBoxButtons { OK, YesNo, YesNoCancel }
        private enum MessageBoxResult { OK, Yes, No, Cancel }

        private static string GetRomPackageKindName(RomPackageKind kind) => kind switch
        {
            RomPackageKind.OfficialSfp => Lang.PackageKindOfficialSfp,
            RomPackageKind.ThirdParty => Lang.PackageKindThirdParty,
            _ => Lang.PackageKindUnknown
        };

        private void LogRomPackage()
        {
            AppendLog(string.Format(Lang.RomPackageDetected, GetRomPackageKindName(_romPackage.Kind), _romPackage.Reason));
        }

        private async System.Threading.Tasks.Task<MessageBoxResult> ShowMessageBox(
            string message, string title, MessageBoxButtons buttons)
        {
            var dialog = new Window
            {
                Title = title,
                Icon = this.Icon,
                MinWidth = 300,
                MaxWidth = 600,
                MinHeight = 150,
                MaxHeight = 500,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };
            var mainPanel = new StackPanel { Margin = new Thickness(20) };
            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 350,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            scrollViewer.Content = new TextBlock 
            { 
                Text = message, 
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 540
            };
            mainPanel.Children.Add(scrollViewer);
            var buttonPanel = new StackPanel 
            { 
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0)
            };

            MessageBoxResult result = MessageBoxResult.Cancel;

            if (buttons == MessageBoxButtons.OK)
            {
                var okBtn = new Button { Content = Lang.BtnOk, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                okBtn.Click += (s, e) => { result = MessageBoxResult.OK; dialog.Close(); };
                buttonPanel.Children.Add(okBtn);
            }
            else if (buttons == MessageBoxButtons.YesNo)
            {
                var yesBtn = new Button { Content = Lang.BtnYes, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                yesBtn.Click += (s, e) => { result = MessageBoxResult.Yes; dialog.Close(); };
                var noBtn = new Button { Content = Lang.BtnNo, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                noBtn.Click += (s, e) => { result = MessageBoxResult.No; dialog.Close(); };
                buttonPanel.Children.Add(yesBtn);
                buttonPanel.Children.Add(noBtn);
            }
            else if (buttons == MessageBoxButtons.YesNoCancel)
            {
                var yesBtn = new Button { Content = Lang.BtnYes, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                yesBtn.Click += (s, e) => { result = MessageBoxResult.Yes; dialog.Close(); };
                var noBtn = new Button { Content = Lang.BtnNo, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                noBtn.Click += (s, e) => { result = MessageBoxResult.No; dialog.Close(); };
                var cancelBtn = new Button { Content = Lang.BtnCancel, Width = 80, Margin = new Thickness(5), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                cancelBtn.Click += (s, e) => { result = MessageBoxResult.Cancel; dialog.Close(); };
                buttonPanel.Children.Add(yesBtn);
                buttonPanel.Children.Add(noBtn);
                buttonPanel.Children.Add(cancelBtn);
            }

            mainPanel.Children.Add(buttonPanel);

            dialog.Content = mainPanel;

            await dialog.ShowDialog(this);
            return result;
        }
        #endregion
    }
}

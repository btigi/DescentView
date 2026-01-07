using ii.CompleteDestruction;
using ii.CompleteDestruction.Model.Hpi;
using ii.CompleteDestruction.Model.Taf;
using MahApps.Metro.Controls;
using NAudio.Wave;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DescentView
{
    // Dropped file info
    public class ExternalFileEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    public class FadeOutSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _fadeOutSamples;
        private readonly long _totalSamples;
        private long _position;

        public FadeOutSampleProvider(ISampleProvider source, long totalSamples, int fadeOutDurationMs = 50)
        {
            _source = source;
            _totalSamples = totalSamples;
            _fadeOutSamples = (int)(source.WaveFormat.SampleRate * source.WaveFormat.Channels * fadeOutDurationMs / 1000.0);
            _position = 0;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public void Reset()
        {
            _position = 0;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            long fadeStartPosition = _totalSamples - _fadeOutSamples;

            for (int i = 0; i < samplesRead; i++)
            {
                long samplePosition = _position + i;
                if (samplePosition >= fadeStartPosition)
                {
                    float fadeProgress = (float)(samplePosition - fadeStartPosition) / _fadeOutSamples;
                    float volume = Math.Max(0, 1.0f - fadeProgress);
                    buffer[offset + i] *= volume;
                }
            }

            _position += samplesRead;
            return samplesRead;
        }
    }

    public partial class MainWindow : Window
    {
        private Dictionary<TreeViewItem, HpiFileEntry> _filePathMap = new();
        private Dictionary<TreeViewItem, ExternalFileEntry> _externalFileMap = new();
        private HashSet<string> _deletedArchiveFiles = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, BitmapSource> _tntImageCache = new(StringComparer.OrdinalIgnoreCase);
        private string? _currentHpiFilePath;
        private HpiArchive? _currentArchive;
        private HpiProcessor? _currentHpiProcessor;
        private string? _lastOpenFolder;
        private string? _lastSaveFolder;

        // Terrain HPIs for TA:K
        private List<HpiProcessor> _terrainHpiProcessors = new();

        // File type filter state
        private HashSet<string> _checkedExtensions = new(StringComparer.OrdinalIgnoreCase);

        // GAF navigation state
        private List<GafImageEntry>? _currentGafEntries;
        private int _currentGafEntryIndex = 0;
        private int _currentGafFrameIndex = 0;
        private byte[]? _currentGafFileData;

        // TAF navigation state
        private List<TafImageEntry>? _currentTafEntries;
        private int _currentTafEntryIndex = 0;
        private int _currentTafFrameIndex = 0;
        private byte[]? _currentTafFileData;

        // Palette state
        private List<string> _availablePalettes = new();
        private string? _selectedPalettePath;
        private bool _isPaletteChangeInProgress = false;

        // Current file view state
        private byte[]? _currentFileData;
        private HpiFileEntry? _currentFileEntry;
        private bool _isHexView = AppSettings.Instance.DefaultView == DefaultViewOption.Hex;

        // Audio playback state
        private DispatcherTimer? _audioPositionTimer;
        private bool _isDraggingAudioSlider = false;
        private WaveOutEvent? _waveOut;
        private WaveStream? _waveStream;
        private FadeOutSampleProvider? _fadeOutProvider;
        private TimeSpan _audioTotalDuration = TimeSpan.Zero;

        // Drag-drop state
        private System.Windows.Point _dragStartPoint;
        private bool _isDragging = false;

        // Filter debouncing
        private DispatcherTimer? _filterDebounceTimer;

        // 3D model state
        private System.Windows.Point _lastMousePosition3D;
        private bool _isRotating3D = false;
        private double _rotationX3D = 0;
        private double _rotationY3D = 0;
        private double _initialCameraDistance = 500;

        public MainWindow(string? filePath = null)
        {
            InitializeComponent();

            // Register code page encodings (required for .NET Core/.NET 5+)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Set up title bar theming
            ThemeManager.InitializeWindow(this);

            ApplyFontSettings();
            OptionsWindow.FontSettingsChanged += ApplyFontSettings;
            OptionsWindow.TerrainHpiPathChanged += LoadTerrainHpi;

            // Load terrain.hpi if configured
            LoadTerrainHpi();

            // Populate palette dropdown
            PopulatePaletteDropdown();

            // Load file if provided via command line
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    LoadHpiFile(filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening HPI file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void ApplyFontSettings()
        {
            var settings = AppSettings.Instance;
            ContentTextBox.FontFamily = new FontFamily(settings.FontFamily);
            ContentTextBox.FontSize = settings.FontSize;
        }

        private void LoadTerrainHpi()
        {
            _terrainHpiProcessors.Clear();

            var terrainPaths = AppSettings.Instance.TerrainHpiPaths;
            if (terrainPaths == null || terrainPaths.Count == 0)
            {
                return;
            }

            foreach (var terrainPath in terrainPaths)
            {
                if (string.IsNullOrEmpty(terrainPath) || !File.Exists(terrainPath))
                {
                    continue;
                }

                try
                {
                    var processor = new HpiProcessor();
                    processor.Read(terrainPath, quickRead: true);
                    _terrainHpiProcessors.Add(processor);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading terrain HPI '{terrainPath}': {ex.Message}");
                }
            }
        }

        private void PopulatePaletteDropdown()
        {
            _availablePalettes.Clear();
            PaletteComboBox.Items.Clear();

            var exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var palettesFolder = Path.Combine(exeDirectory ?? "", "palettes");

            if (!Directory.Exists(palettesFolder))
            {
                return;
            }

            var palFiles = Directory.GetFiles(palettesFolder, "*.pal", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var palFile in palFiles)
            {
                _availablePalettes.Add(palFile);
                PaletteComboBox.Items.Add(Path.GetFileName(palFile));
            }

            // Select first palette by default if available
            if (PaletteComboBox.Items.Count > 0)
            {
                PaletteComboBox.SelectedIndex = 0;
                _selectedPalettePath = _availablePalettes[0];
            }
        }

        private void ShowPaletteSelector(bool show)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            PaletteLabelTextBlock.Visibility = visibility;
            PaletteComboBox.Visibility = visibility;
        }

        private void PaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPaletteChangeInProgress || PaletteComboBox.SelectedIndex < 0)
                return;

            if (PaletteComboBox.SelectedIndex < _availablePalettes.Count)
            {
                _selectedPalettePath = _availablePalettes[PaletteComboBox.SelectedIndex];

                // Re-render the current file with the new palette
                if (_currentFileData != null && _currentFileEntry != null)
                {
                    var extension = Path.GetExtension(_currentFileEntry.RelativePath).ToLower();
                    if (extension == ".gaf" || extension == ".taf" || extension == ".tnt")
                    {
                        // Clear cache for this file so it re-renders
                        var cacheKey = _currentFileEntry.RelativePath;
                        if (_tntImageCache.ContainsKey(cacheKey))
                        {
                            _tntImageCache.Remove(cacheKey);
                        }

                        DisplayImage(_currentFileData, extension);
                    }
                }
            }
        }

        private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Archive Files (*.hpi;*.ccx;*.gp3;*.ufo;*.kmp)|*.hpi;*.ccx;*.gp3;*.ufo;*.kmp|HPI Files (*.hpi)|*.hpi|CCX Files (*.ccx)|*.ccx|GP3 Files (*.gp3)|*.gp3|UFO Files (*.ufo)|*.ufo|KMP Files (*.kmp)|*.kmp|All Files (*.*)|*.*",
                Title = "Open Archive File"
            };

            // Set initial directory from last used open folder
            if (!string.IsNullOrEmpty(_lastOpenFolder) && Directory.Exists(_lastOpenFolder))
            {
                dialog.InitialDirectory = _lastOpenFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                // Save the folder for next time
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    _lastOpenFolder = folder;
                }

                try
                {
                    LoadHpiFile(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening HPI file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow
            {
                Owner = this
            };
            aboutWindow.ShowDialog();
        }

        private void OptionsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var optionsWindow = new OptionsWindow
            {
                Owner = this
            };
            optionsWindow.ShowDialog();
        }

        private void ExtractAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentHpiFilePath))
            {
                MessageBox.Show("No HPI file is currently open.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            // Prompt user for folder
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select folder to extract files to"
            };

            // Set initial directory from last used save folder
            if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
            {
                dialog.InitialDirectory = _lastSaveFolder;
            }

            if (dialog.ShowDialog() != true)
                return;

            var targetFolder = dialog.FolderName;
            _lastSaveFolder = targetFolder;

            try
            {
                // Read all files with content
                var processor = new HpiProcessor();
                var archive = processor.Read(_currentHpiFilePath, false);

                if (archive == null || archive.Files == null || archive.Files.Count == 0)
                {
                    MessageBox.Show("No files found in HPI archive.",
                                   "Information",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Information);
                    return;
                }

                int extractedCount = 0;
                int errorCount = 0;

                foreach (var file in archive.Files)
                {
                    try
                    {
                        var targetPath = Path.Combine(targetFolder, file.RelativePath);
                        var targetDir = Path.GetDirectoryName(targetPath);

                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        if (file.Data != null)
                        {
                            File.WriteAllBytes(targetPath, file.Data);
                            extractedCount++;
                        }
                        else
                        {
                            errorCount++;
                        }
                    }
                    catch
                    {
                        errorCount++;
                    }
                }

                var message = $"Extracted {extractedCount} files to:\n{targetFolder}";
                if (errorCount > 0)
                {
                    message += $"\n\n{errorCount} files could not be extracted.";
                }

                MessageBox.Show(message,
                               "Extract All Complete",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting files:\n{ex.Message}",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }
        }

        private void SaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentHpiFilePath))
            {
                MessageBox.Show("No archive file is currently open.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            SaveArchive(_currentHpiFilePath);
        }

        private void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentHpiFilePath))
            {
                MessageBox.Show("No archive file is currently open.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(_currentHpiFilePath),
                Filter = "HPI Files (*.hpi)|*.hpi|CCX Files (*.ccx)|*.ccx|GP3 Files (*.gp3)|*.gp3|UFO Files (*.ufo)|*.ufo|All Files (*.*)|*.*",
                Title = "Save Archive As"
            };

            if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
            {
                dialog.InitialDirectory = _lastSaveFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    _lastSaveFolder = folder;
                }

                SaveArchive(dialog.FileName);
            }
        }

        private void UpdateSaveMenuItemState()
        {
            var hasModifications = _deletedArchiveFiles.Count > 0 || _externalFileMap.Count > 0;
            SaveMenuItem.IsEnabled = !string.IsNullOrEmpty(_currentHpiFilePath) && hasModifications;
        }

        private void SaveArchive(string outputPath)
        {
            if (_currentArchive == null || _currentHpiProcessor == null)
            {
                MessageBox.Show("No archive is currently loaded.",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
                return;
            }

            try
            {
                var filesToSave = new List<HpiFileEntry>();

                foreach (var file in _currentArchive.Files)
                {
                    if (_deletedArchiveFiles.Contains(file.RelativePath))
                        continue;

                    var fileData = _currentHpiProcessor.Extract(file.RelativePath);
                    if (fileData != null)
                    {
                        filesToSave.Add(new HpiFileEntry
                        {
                            RelativePath = file.RelativePath,
                            Data = fileData
                        });
                    }
                }

                foreach (var kvp in _externalFileMap)
                {
                    var externalEntry = kvp.Value;
                    filesToSave.Add(new HpiFileEntry
                    {
                        RelativePath = externalEntry.RelativePath,
                        Data = externalEntry.Data
                    });
                }

                var processor = new HpiProcessor();
                processor.Write(outputPath, filesToSave);

                MessageBox.Show($"Archive saved successfully to:\n{outputPath}",
                               "Success",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);

                LoadHpiFile(outputPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving archive:\n{ex.Message}",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }
        }

        private void SortAlphabeticallyButton_Click(object sender, RoutedEventArgs e)
        {
            // Rebuild tree with the new sort setting (no need to reload from disk)
            if (!string.IsNullOrEmpty(_currentHpiFilePath) && _currentArchive != null)
            {
                BuildTreeView();
            }
        }

        private async void LoadHpiFile(string filePath)
        {
            _currentHpiFilePath = filePath;
            _deletedArchiveFiles.Clear();
            _externalFileMap.Clear();
            _tntImageCache.Clear();
            ContentTextBox.Text = string.Empty;
            FileInfoTextBlock.Text = $"Loading: {Path.GetFileName(filePath)}...";
            Title = $"{Path.GetFileName(filePath)} - DescentView";

            ExtractAllMenuItem.IsEnabled = false;
            SaveMenuItem.IsEnabled = false;
            SaveAsMenuItem.IsEnabled = false;
            FilterDropdownButton.IsEnabled = false;

            try
            {
                HpiArchive? archive = null;
                HpiProcessor? processor = null;

                await Task.Run(() =>
                {
                    processor = new HpiProcessor();
                    archive = processor.Read(filePath);
                });

                _currentHpiProcessor = processor;
                _currentArchive = archive;

                if (archive == null || archive.Files == null || archive.Files.Count == 0)
                {
                    MessageBox.Show("No files found in HPI archive.",
                                   "Information",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Information);
                    FilterDropdownButton.IsEnabled = false;
                    FileInfoTextBlock.Text = $"File: {Path.GetFileName(filePath)}";
                    return;
                }

                FileInfoTextBlock.Text = $"File: {Path.GetFileName(filePath)}";

                // Build file type filter
                BuildFileTypeFilter();

                // Enable menu items
                ExtractAllMenuItem.IsEnabled = true;
                SaveAsMenuItem.IsEnabled = true;
                UpdateSaveMenuItemState();

                // Build tree view (already async)
                BuildTreeView();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading HPI file:\n{ex.Message}",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
                FileInfoTextBlock.Text = "Error loading file";
            }
        }

        private void BuildFileTypeFilter()
        {
            if (_currentArchive == null)
                return;

            FilterCheckboxPanel.Children.Clear();
            _checkedExtensions.Clear();

            // Get all unique extensions, sorted alphabetically
            var extensions = _currentArchive.Files
                .Select(f => Path.GetExtension(f.RelativePath).ToUpperInvariant())
                .Where(ext => !string.IsNullOrEmpty(ext))
                .Distinct()
                .OrderBy(ext => ext, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Add [All] checkbox at the top
            var allCheckBox = new CheckBox
            {
                Content = "[All]",
                IsChecked = true,
                Margin = new Thickness(2),
                Tag = "ALL",
                FontWeight = FontWeights.Bold
            };
            allCheckBox.SetResourceReference(CheckBox.ForegroundProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundCheckedProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundIndeterminateProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.Checked += AllFilterCheckBox_Checked;
            allCheckBox.Unchecked += AllFilterCheckBox_Unchecked;
            FilterCheckboxPanel.Children.Add(allCheckBox);

            // Add separator
            FilterCheckboxPanel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 2) });

            // Check all extensions by default
            foreach (var ext in extensions)
            {
                _checkedExtensions.Add(ext);

                var checkBox = new CheckBox
                {
                    Content = ext,
                    IsChecked = true,
                    Margin = new Thickness(2),
                    Tag = ext
                };
                checkBox.SetResourceReference(CheckBox.ForegroundProperty, "MahApps.Brushes.ThemeForeground");
                checkBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundCheckedProperty, "MahApps.Brushes.ThemeForeground");
                checkBox.Checked += FilterCheckBox_Changed;
                checkBox.Unchecked += FilterCheckBox_Changed;

                FilterCheckboxPanel.Children.Add(checkBox);
            }

            FilterDropdownButton.IsEnabled = extensions.Count > 0;
        }

        private void AllFilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SetAllFilterCheckboxes(true);
        }

        private void AllFilterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAllFilterCheckboxes(false);
        }

        private void SetAllFilterCheckboxes(bool isChecked)
        {
            foreach (var child in FilterCheckboxPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.Tag is string tag && tag != "ALL")
                {
                    // Temporarily unhook events to avoid multiple tree rebuilds
                    checkBox.Checked -= FilterCheckBox_Changed;
                    checkBox.Unchecked -= FilterCheckBox_Changed;

                    checkBox.IsChecked = isChecked;

                    if (isChecked)
                    {
                        _checkedExtensions.Add(tag);
                    }
                    else
                    {
                        _checkedExtensions.Remove(tag);
                    }

                    // Rehook events
                    checkBox.Checked += FilterCheckBox_Changed;
                    checkBox.Unchecked += FilterCheckBox_Changed;
                }
            }

            DebounceFilterChange();
        }

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.Tag is string ext)
            {
                if (checkBox.IsChecked == true)
                {
                    _checkedExtensions.Add(ext);
                }
                else
                {
                    _checkedExtensions.Remove(ext);
                }

                UpdateAllCheckboxState();
                DebounceFilterChange();
            }
        }

        private void DebounceFilterChange()
        {
            if (_filterDebounceTimer != null)
            {
                _filterDebounceTimer.Stop();
                _filterDebounceTimer.Tick -= FilterDebounceTimer_Tick;
            }

            _filterDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _filterDebounceTimer.Tick += FilterDebounceTimer_Tick;
            _filterDebounceTimer.Start();
        }

        private void FilterDebounceTimer_Tick(object? sender, EventArgs e)
        {
            if (_filterDebounceTimer != null)
            {
                _filterDebounceTimer.Stop();
                _filterDebounceTimer.Tick -= FilterDebounceTimer_Tick;
                _filterDebounceTimer = null;
            }
            BuildTreeView();
        }

        private void UpdateAllCheckboxState()
        {
            // Find the [All] checkbox and update its state
            foreach (var child in FilterCheckboxPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.Tag is string tag && tag == "ALL")
                {
                    // Temporarily unhook events
                    checkBox.Checked -= AllFilterCheckBox_Checked;
                    checkBox.Unchecked -= AllFilterCheckBox_Unchecked;

                    // Count total extension checkboxes
                    int totalCount = 0;
                    int checkedCount = 0;
                    foreach (var c in FilterCheckboxPanel.Children)
                    {
                        if (c is CheckBox cb && cb.Tag is string t && t != "ALL")
                        {
                            totalCount++;
                            if (cb.IsChecked == true)
                                checkedCount++;
                        }
                    }

                    if (checkedCount == 0)
                        checkBox.IsChecked = false;
                    else if (checkedCount == totalCount)
                        checkBox.IsChecked = true;
                    else
                        checkBox.IsChecked = null; // Indeterminate

                    // Rehook events
                    checkBox.Checked += AllFilterCheckBox_Checked;
                    checkBox.Unchecked += AllFilterCheckBox_Unchecked;
                    break;
                }
            }
        }

        private void BuildTreeView()
        {
            if (_currentArchive == null || _currentHpiFilePath == null)
                return;

            FileTreeView.Items.Clear();
            _filePathMap.Clear();
            _externalFileMap.Clear();

            var sortAlphabetically = SortAlphabeticallyButton.IsChecked == true;

            // Build tree structure
            var rootNode = new TreeViewItem
            {
                Header = Path.GetFileName(_currentHpiFilePath),
                Tag = "ROOT"
            };

            // Get files, optionally sorted and filtered
            var files = sortAlphabetically
                ? _currentArchive.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()
                : _currentArchive.Files.ToList();

            // Filter by checked extensions
            files = files.Where(f =>
            {
                var ext = Path.GetExtension(f.RelativePath).ToUpperInvariant();
                return string.IsNullOrEmpty(ext) || _checkedExtensions.Contains(ext);
            }).ToList();

            // Filter out deleted files
            files = files.Where(f => !_deletedArchiveFiles.Contains(f.RelativePath)).ToList();

            // Build tree asynchronously in batches to keep UI responsive
            BuildTreeViewAsync(rootNode, files);
        }

        private async void BuildTreeViewAsync(TreeViewItem rootNode, List<HpiFileEntry> files)
        {
            const int batchSize = 100;
            var directoryMap = new Dictionary<string, TreeViewItem>();

            FileTreeView.Items.Add(rootNode);
            rootNode.IsExpanded = true;

            int processed = 0;

            foreach (var file in files)
            {
                var parts = file.RelativePath.Split('\\', '/');
                var currentPath = string.Empty;
                TreeViewItem? parentNode = rootNode;

                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var part = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}\\{part}";

                    if (!directoryMap.ContainsKey(currentPath))
                    {
                        var dirNode = new TreeViewItem
                        {
                            Header = part,
                            Tag = currentPath
                        };
                        parentNode.Items.Add(dirNode);
                        directoryMap[currentPath] = dirNode;
                    }
                    parentNode = directoryMap[currentPath];
                }

                // Create file node with context menu
                var fileNode = new TreeViewItem
                {
                    Header = parts[parts.Length - 1],
                    Tag = file
                };

                // Add context menu for files
                var contextMenu = new ContextMenu();
                var extractMenuItem = new MenuItem
                {
                    Header = "Extract",
                    Tag = fileNode
                };
                extractMenuItem.Click += ExtractMenuItem_Click;
                contextMenu.Items.Add(extractMenuItem);

                var removeMenuItem = new MenuItem
                {
                    Header = "Remove",
                    Tag = fileNode
                };
                removeMenuItem.Click += RemoveArchiveFileMenuItem_Click;
                contextMenu.Items.Add(removeMenuItem);

                fileNode.ContextMenu = contextMenu;

                parentNode.Items.Add(fileNode);
                _filePathMap[fileNode] = file;

                processed++;

                if (processed % batchSize == 0)
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }

            ScrollTreeViewToTop();
        }

        private void ScrollTreeViewToTop()
        {
            var sv = GetScrollViewer(FileTreeView);
            if (sv != null)
                sv.ScrollToTop();
        }

        public static ScrollViewer GetScrollViewer(DependencyObject o)
        {
            if (o is ScrollViewer sv)
                return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                var result = GetScrollViewer(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            StopAudio(disposeResources: true);

            if (e.NewValue is TreeViewItem selectedItem)
            {
                if (_filePathMap.TryGetValue(selectedItem, out var fileEntry))
                {
                    DisplayFileContent(fileEntry);
                }
                else if (_externalFileMap.TryGetValue(selectedItem, out var externalEntry))
                {
                    DisplayExternalFileContent(externalEntry);
                }
                else
                {
                    ContentTextBox.Text = string.Empty;
                    FileInfoTextBlock.Text = $"Directory: {selectedItem.Header}";
                    TextScrollViewer.ScrollToHome();
                }
            }
        }

        private void FileTreeView_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
        }

        private void FileTreeView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Delete)
            {
                if (FileTreeView.SelectedItem is TreeViewItem selectedItem)
                {
                    if (_externalFileMap.ContainsKey(selectedItem))
                    {
                        RemoveExternalFile(selectedItem);
                        e.Handled = true;
                    }
                    else if (_filePathMap.ContainsKey(selectedItem))
                    {
                        RemoveArchiveFile(selectedItem);
                        e.Handled = true;
                    }
                }
            }
        }

        private void FileTreeView_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                _isDragging = false;
                return;
            }

            System.Windows.Point currentPosition = e.GetPosition(null);
            System.Windows.Vector diff = _dragStartPoint - currentPosition;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (_isDragging)
                    return;

                var treeViewItem = FindAncestor<TreeViewItem>((DependencyObject)e.OriginalSource);
                if (treeViewItem == null)
                    return;

                if (!_filePathMap.TryGetValue(treeViewItem, out var fileEntry))
                    return;

                var fileData = GetFileData(fileEntry);
                if (fileData == null || fileData.Length == 0)
                    return;

                _isDragging = true;

                try
                {
                    var fileName = Path.GetFileName(fileEntry.RelativePath);
                    var tempPath = Path.Combine(Path.GetTempPath(), fileName);
                    File.WriteAllBytes(tempPath, fileData);

                    var dataObject = new DataObject();
                    dataObject.SetFileDropList(new System.Collections.Specialized.StringCollection { tempPath });

                    DragDrop.DoDragDrop(treeViewItem, dataObject, DragDropEffects.Copy);
                }
                catch
                {
                    // :(
                }
                finally
                {
                    _isDragging = false;
                }
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t)
                    return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void FileTreeView_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            // Find the TreeViewItem under the mouse
            var targetItem = FindAncestor<TreeViewItem>((DependencyObject)e.OriginalSource);
            if (targetItem == null)
                return;

            // Check if it's a folder via string Tag (path) or "ROOT"
            // Files have HpiFileEntry as Tag or are in _filePathMap or _externalFileMap
            if (_filePathMap.ContainsKey(targetItem) || _externalFileMap.ContainsKey(targetItem))
            {
                // This is a file - don't allow drop
                return;
            }

            // It's a folder or root - allow drop
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void FileTreeView_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            // Find the target TreeViewItem (folder)
            var targetItem = FindAncestor<TreeViewItem>((DependencyObject)e.OriginalSource);
            if (targetItem == null)
                return;

            // Don't allow drop on files
            if (_filePathMap.ContainsKey(targetItem) || _externalFileMap.ContainsKey(targetItem))
                return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0)
                return;

            foreach (var filePath in files)
            {
                try
                {
                    // Only handle files, not directories
                    if (!File.Exists(filePath))
                        continue;

                    var fileName = Path.GetFileName(filePath);
                    var fileData = File.ReadAllBytes(filePath);

                    // Determine the relative path based on target folder
                    string relativePath;
                    if (targetItem.Tag is string tagPath)
                    {
                        if (tagPath == "ROOT")
                        {
                            relativePath = fileName;
                        }
                        else
                        {
                            relativePath = $"{tagPath}\\{fileName}";
                        }
                    }
                    else
                    {
                        relativePath = fileName;
                    }

                    var externalEntry = new ExternalFileEntry
                    {
                        FileName = fileName,
                        RelativePath = relativePath,
                        Data = fileData
                    };

                    // Create tree node for the dropped file
                    var fileNode = new TreeViewItem
                    {
                        Header = $"📎 {fileName}",
                        Tag = externalEntry
                    };

                    var contextMenu = new ContextMenu();
                    var removeMenuItem = new MenuItem
                    {
                        Header = "Remove",
                        Tag = fileNode
                    };
                    removeMenuItem.Click += RemoveExternalFileMenuItem_Click;
                    contextMenu.Items.Add(removeMenuItem);
                    fileNode.ContextMenu = contextMenu;

                    targetItem.Items.Add(fileNode);
                    _externalFileMap[fileNode] = externalEntry;

                    targetItem.IsExpanded = true;

                    fileNode.IsSelected = true;
                    
                    UpdateSaveMenuItemState();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading file '{Path.GetFileName(filePath)}':\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }

            e.Handled = true;
        }

        private void RemoveExternalFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not TreeViewItem treeViewItem)
                return;

            RemoveExternalFile(treeViewItem);
        }

        private void RemoveExternalFile(TreeViewItem treeViewItem)
        {
            if (!_externalFileMap.ContainsKey(treeViewItem))
                return;

            _externalFileMap.Remove(treeViewItem);

            var parent = treeViewItem.Parent as TreeViewItem;
            parent?.Items.Remove(treeViewItem);

            ClearPreviewIfSelected(treeViewItem);
            UpdateSaveMenuItemState();
        }

        private void RemoveArchiveFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not TreeViewItem treeViewItem)
                return;

            RemoveArchiveFile(treeViewItem);
        }

        private void RemoveArchiveFile(TreeViewItem treeViewItem)
        {
            if (!_filePathMap.TryGetValue(treeViewItem, out var fileEntry))
                return;

            _deletedArchiveFiles.Add(fileEntry.RelativePath);

            _filePathMap.Remove(treeViewItem);

            var parent = treeViewItem.Parent as TreeViewItem;
            parent?.Items.Remove(treeViewItem);

            ClearPreviewIfSelected(treeViewItem);
            UpdateSaveMenuItemState();
        }

        private void ClearPreviewIfSelected(TreeViewItem treeViewItem)
        {
            if (treeViewItem.IsSelected)
            {
                _currentFileData = null;
                _currentFileEntry = null;
                ContentTextBox.Text = string.Empty;
                FileInfoTextBlock.Text = "No file selected";
                ViewTogglePanel.Visibility = Visibility.Collapsed;
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void ExtractMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not TreeViewItem treeViewItem)
                return;

            if (!_filePathMap.TryGetValue(treeViewItem, out var fileEntry))
            {
                MessageBox.Show("Please select a file to extract.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            var fileData = GetFileData(fileEntry);
            if (fileData == null || fileData.Length == 0)
            {
                MessageBox.Show("Could not read file data from archive.",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
                return;
            }

            var fileName = Path.GetFileName(fileEntry.RelativePath);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Filter = "All Files (*.*)|*.*",
                Title = "Extract File"
            };

            // Set initial directory from last used save folder
            if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
            {
                dialog.InitialDirectory = _lastSaveFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                // Save the folder for next time
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    _lastSaveFolder = folder;
                }

                try
                {
                    File.WriteAllBytes(dialog.FileName, fileData);
                    MessageBox.Show($"File extracted successfully to:\n{dialog.FileName}",
                                   "Success",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error extracting file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void ViewToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_currentFileEntry == null || _currentFileData == null)
                return;

            _isHexView = HexViewButton.IsChecked == true;

            if (_isHexView)
            {
                ShowHexView();
            }
            else
            {
                ShowPreviewView();
            }
        }

        private void ShowHexView()
        {
            if (_currentFileData == null || _currentFileEntry == null)
                return;

            StopAudio(disposeResources: true);

            TextScrollViewer.Visibility = Visibility.Visible;
            ImageContentGrid.Visibility = Visibility.Collapsed;
            AudioContentGrid.Visibility = Visibility.Collapsed;
            Model3DContentGrid.Visibility = Visibility.Collapsed;

            ContentTextBox.Text = FormatHexDump(_currentFileData, _currentFileEntry.RelativePath);
            TextScrollViewer.ScrollToHome();
        }

        private void ShowPreviewView()
        {
            if (_currentFileEntry == null)
                return;

            // Re-display the file content in preview mode
            _isHexView = false;
            DisplayFileContentInternal(_currentFileEntry, _currentFileData);
        }

        private void DisplayFileContent(HpiFileEntry fileEntry)
        {
            try
            {
                var filePath = fileEntry.RelativePath;
                FileInfoTextBlock.Text = $"File: {filePath}";

                if (_currentHpiFilePath == null || _currentArchive == null)
                {
                    ContentTextBox.Text = "No HPI file loaded.";
                    ViewTogglePanel.Visibility = Visibility.Collapsed;
                    return;
                }

                byte[]? fileData = GetFileData(fileEntry);

                if (fileData == null || fileData.Length == 0)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "(empty file)";
                    ViewTogglePanel.Visibility = Visibility.Collapsed;
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                // Store current file data for view toggling
                _currentFileData = fileData;
                _currentFileEntry = fileEntry;

                // Show view toggle (maintain current view selection)
                ViewTogglePanel.Visibility = Visibility.Visible;

                // Update radio button state to match current view
                if (_isHexView)
                {
                    HexViewButton.IsChecked = true;
                    ShowHexView();
                }
                else
                {
                    PreviewViewButton.IsChecked = true;
                    DisplayFileContentInternal(fileEntry, fileData);
                }
            }
            catch (Exception ex)
            {
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private void DisplayExternalFileContent(ExternalFileEntry externalEntry)
        {
            try
            {
                FileInfoTextBlock.Text = $"External File: {externalEntry.RelativePath}";

                var fileData = externalEntry.Data;

                if (fileData == null || fileData.Length == 0)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "(empty file)";
                    ViewTogglePanel.Visibility = Visibility.Collapsed;
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                var tempEntry = new HpiFileEntry
                {
                    RelativePath = externalEntry.RelativePath
                };

                _currentFileData = fileData;
                _currentFileEntry = tempEntry;

                ViewTogglePanel.Visibility = Visibility.Visible;

                if (_isHexView)
                {
                    HexViewButton.IsChecked = true;
                    ShowHexView();
                }
                else
                {
                    PreviewViewButton.IsChecked = true;
                    DisplayFileContentInternal(tempEntry, fileData);
                }
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private void DisplayFileContentInternal(HpiFileEntry fileEntry, byte[]? fileData)
        {
            try
            {
                var filePath = fileEntry.RelativePath;

                if (fileData == null || fileData.Length == 0)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "(empty file)";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                var extension = Path.GetExtension(filePath).ToLower();

                if (extension == ".wav")
                {
                    DisplayAudio(fileData, extension);
                    return;
                }

                if (extension == ".3do")
                {
                    Display3DModel(fileData, extension);
                    return;
                }

                if (extension == ".pcx" || extension == ".bmp" || extension == ".gaf" || extension == ".taf" || extension == ".tnt" ||
                    extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                {
                    DisplayImage(fileData, extension);
                    return;
                }

                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;

                try
                {
                    string content;

                    switch (extension)
                    {
                        case ".tsf":
                        case ".tdf":
                        case ".fbi":
                        case ".gui":
                        case ".ota":
                        case ".txt":
                        case ".bat":
                        case ".ini":
                        case ".cfg":
                        case ".bos":
                        case ".h":
                        case ".pl":
                            var encoding = Encoding.GetEncoding(1252);
                            content = encoding.GetString(fileData);
                            break;

                        case ".crt":
                            try
                            {
                                var crtProcessor = new CrtProcessor();
                                var crtFile = crtProcessor.Read(fileData);
                                content = crtProcessor.ToJson(crtFile);
                            }
                            catch (Exception ex)
                            {
                                content = $"Error reading CRT file:\n{ex.Message}\n\nHex dump:\n\n{FormatHexDump(fileData, filePath)}";
                            }
                            break;

                        default:
                            content = FormatHexDump(fileData, filePath);
                            break;
                    }

                    ContentTextBox.Text = content;

                    TextScrollViewer.ScrollToHome();
                }
                catch (Exception ex)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Error processing file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                    TextScrollViewer.ScrollToHome();
                }
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private byte[]? GetFileData(HpiFileEntry fileEntry)
        {
            if (_currentHpiProcessor == null)
            {
                return null;
            }

            try
            {
                var fileData = _currentHpiProcessor.Extract(fileEntry.RelativePath);
                return fileData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting file {fileEntry.RelativePath}: {ex.Message}");
                return null;
            }
        }


        private void DisplayImage(byte[] imageData, string extension)
        {
            try
            {
                ContentImage.Source = null;

                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = 1.0;
                }
                if (ImageScaleTransform != null)
                {
                    ImageScaleTransform.ScaleX = 1.0;
                    ImageScaleTransform.ScaleY = 1.0;
                }
                if (ZoomValueTextBlock != null)
                {
                    ZoomValueTextBlock.Text = "100%";
                }

                TextScrollViewer.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ImageContentGrid.Visibility = Visibility.Visible;

                // Hide palette selector by default (GAF/TAF/TNT will show it)
                ShowPaletteSelector(false);

                if (extension != ".gaf")
                {
                    _currentGafEntries = null;
                    _currentGafFileData = null;
                }

                if (extension != ".taf")
                {
                    _currentTafEntries = null;
                    _currentTafFileData = null;
                }

                if (extension != ".gaf" && extension != ".taf")
                {
                    PreviousFrameButton.Visibility = Visibility.Collapsed;
                    NextFrameButton.Visibility = Visibility.Collapsed;
                }

                BitmapSource? bitmapImage = null;
                string? imageInfo = null;

                if (extension == ".pcx")
                {
                    var pcxProcessor = new PcxConverter();
                    var pcxImage = pcxProcessor.Parse(imageData);

                    bitmapImage = ConvertPcxToBitmapImage(pcxImage);
                    imageInfo = $"PCX Image: {pcxImage?.Width ?? 0}x{pcxImage?.Height ?? 0}";
                }
                else if (extension == ".bmp")
                {
                    // Stream will be disposed when BitmapImage is garbage collected
                    var memoryStream = new MemoryStream(imageData);
                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.StreamSource = memoryStream;
                    bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                    bmpImage.EndInit();
                    bmpImage.Freeze();
                    bitmapImage = bmpImage;
                    imageInfo = $"BMP Image";
                }
                else if (extension == ".jpg" || extension == ".jpeg")
                {
                    // Stream will be disposed when BitmapImage is garbage collected
                    var memoryStream = new MemoryStream(imageData);
                    var jpgImage = new BitmapImage();
                    jpgImage.BeginInit();
                    jpgImage.StreamSource = memoryStream;
                    jpgImage.CacheOption = BitmapCacheOption.OnLoad;
                    jpgImage.EndInit();
                    jpgImage.Freeze();
                    bitmapImage = jpgImage;
                    imageInfo = $"JPEG Image: {jpgImage.PixelWidth}x{jpgImage.PixelHeight}";
                }
                else if (extension == ".png")
                {
                    // Stream will be disposed when BitmapImage is garbage collected
                    var memoryStream = new MemoryStream(imageData);
                    var pngImage = new BitmapImage();
                    pngImage.BeginInit();
                    pngImage.StreamSource = memoryStream;
                    pngImage.CacheOption = BitmapCacheOption.OnLoad;
                    pngImage.EndInit();
                    pngImage.Freeze();
                    bitmapImage = pngImage;
                    imageInfo = $"PNG Image: {pngImage.PixelWidth}x{pngImage.PixelHeight}";
                }
                else if (extension == ".gaf")
                {
                    _currentGafFileData = imageData;
                    ShowPaletteSelector(true);

                    if (string.IsNullOrEmpty(_selectedPalettePath) || !File.Exists(_selectedPalettePath))
                    {
                        imageInfo = "GAF File: No palette selected or palette file not found";
                    }
                    else
                    {
                        var paletteBytes = File.ReadAllBytes(_selectedPalettePath);
                        var paletteProcessor = new PalProcessor();
                        paletteProcessor.Load(paletteBytes);

                        var gafProcessor = new GafProcessor();
                        var gafEntries = gafProcessor.Read(imageData, paletteProcessor);
                        _currentGafEntries = gafEntries;
                        _currentGafEntryIndex = 0;
                        _currentGafFrameIndex = 0;

                        DisplayGafFrame();
                    }
                    return;
                }
                else if (extension == ".taf")
                {
                    _currentTafFileData = imageData;
                    ShowPaletteSelector(false);

                    var tafProcessor = new TafProcessor();
                    var tafEntries = tafProcessor.Read(imageData);
                    _currentTafEntries = tafEntries;
                    _currentTafEntryIndex = 0;
                    _currentTafFrameIndex = 0;

                    DisplayTafFrame();
                    return;
                }
                else if (extension == ".tnt")
                {
                    ShowPaletteSelector(true);

                    var cacheKey = _currentFileEntry?.RelativePath ?? "";
                    var cachingEnabled = AppSettings.Instance.EnableTntCaching;

                    // Include palette in cache key so different palettes have different cache entries
                    var paletteName = Path.GetFileName(_selectedPalettePath ?? "");
                    var fullCacheKey = $"{cacheKey}|{paletteName}";

                    if (cachingEnabled && !string.IsNullOrEmpty(fullCacheKey) && _tntImageCache.TryGetValue(fullCacheKey, out var cachedImage))
                    {
                        bitmapImage = cachedImage;
                        imageInfo = $"TNT Map (cached)";
                    }
                    else
                    {
                        var tntProcessor = new TntProcessor();

                        if (string.IsNullOrEmpty(_selectedPalettePath) || !File.Exists(_selectedPalettePath))
                        {
                            imageInfo = "TNT File: No palette selected or palette file not found";
                        }
                        else
                        {
                            try
                            {
                                var paletteBytes = File.ReadAllBytes(_selectedPalettePath);
                                var paletteProcessor = new PalProcessor();
                                paletteProcessor.Load(paletteBytes);

                                var tntFile = tntProcessor.Read(imageData, paletteProcessor);

                                if (tntFile != null)
                                {
                                    var isV2 = tntFile.TerrainNames != null && tntFile.TerrainNames.Count > 0;

                                    if (isV2 && _terrainHpiProcessors.Count > 0)
                                    {
                                        var renderedMap = RenderTntV2WithTerrain(tntFile);
                                        if (renderedMap != null)
                                        {
                                            bitmapImage = renderedMap;
                                            imageInfo = $"TNT Map (Kingdoms): {tntFile.AttributeWidth * 16}x{tntFile.AttributeHeight * 16}";
                                        }
                                        else if (tntFile.Map != null)
                                        {
                                            if (tntFile.Map is Image<Rgba32> rgbaMap)
                                            {
                                                bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaMap);
                                            }
                                            else
                                            {
                                                bitmapImage = ConvertImageSharpToBitmapImage(tntFile.Map);
                                            }
                                            imageInfo = $"TNT Map (heightmap): {tntFile.Map.Width}x{tntFile.Map.Height}";
                                        }
                                    }
                                    else if (tntFile.Map != null)
                                    {
                                        if (tntFile.Map is Image<Rgba32> rgbaMap)
                                        {
                                            bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaMap);
                                        }
                                        else
                                        {
                                            bitmapImage = ConvertImageSharpToBitmapImage(tntFile.Map);
                                        }
                                        imageInfo = $"TNT Map: {tntFile.Map.Width}x{tntFile.Map.Height}";
                                    }
                                    else
                                    {
                                        imageInfo = "TNT File: Map not available";
                                    }

                                    if (cachingEnabled && bitmapImage != null && !string.IsNullOrEmpty(fullCacheKey))
                                    {
                                        _tntImageCache[fullCacheKey] = bitmapImage;
                                    }
                                }
                                else
                                {
                                    imageInfo = "TNT File: Map not available";
                                }
                            }
                            catch (Exception ex)
                            {
                                imageInfo = $"TNT File: Error loading - {ex.Message}";
                            }
                        }
                    }
                }

                if (bitmapImage != null)
                {
                    ContentImage.Source = bitmapImage;
                    if (!string.IsNullOrEmpty(imageInfo))
                    {
                        FileInfoTextBlock.Text = imageInfo;
                    }

                    if (extension == ".tnt" && AppSettings.Instance.AutoFitTnt)
                    {
                        AutoFitImage(bitmapImage);
                    }
                }
                else
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Could not display image: {extension}\n\n{imageInfo ?? ""}";
                    TextScrollViewer.ScrollToHome();
                }
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error displaying image:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private void AutoFitImage(BitmapSource image)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var viewportWidth = ImageScrollViewer.ActualWidth;
                var viewportHeight = ImageScrollViewer.ActualHeight;

                if (viewportWidth <= 0 || viewportHeight <= 0)
                    return;

                var imageWidth = image.PixelWidth;
                var imageHeight = image.PixelHeight;

                if (imageWidth <= 0 || imageHeight <= 0)
                    return;

                var zoomX = viewportWidth / imageWidth;
                var zoomY = viewportHeight / imageHeight;
                var zoom = Math.Min(zoomX, zoomY);

                zoom = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, zoom));

                ZoomSlider.Value = zoom;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void DisplayGafFrame()
        {
            ContentImage.Source = null;

            if (ZoomSlider != null)
            {
                ZoomSlider.Value = 1.0;
            }
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = 1.0;
                ImageScaleTransform.ScaleY = 1.0;
            }
            if (ZoomValueTextBlock != null)
            {
                ZoomValueTextBlock.Text = "100%";
            }

            if (_currentGafEntries == null || _currentGafEntries.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = "GAF: No entries found";
                TextScrollViewer.ScrollToHome();
                return;
            }

            if (_currentGafEntryIndex >= _currentGafEntries.Count)
                _currentGafEntryIndex = 0;

            var currentEntry = _currentGafEntries[_currentGafEntryIndex];

            if (currentEntry.Frames == null || currentEntry.Frames.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"GAF: {currentEntry.Name}\nNo frames found";
                TextScrollViewer.ScrollToHome();
                PreviousFrameButton.Visibility = Visibility.Collapsed;
                NextFrameButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_currentGafFrameIndex >= currentEntry.Frames.Count)
                _currentGafFrameIndex = 0;

            var currentFrame = currentEntry.Frames[_currentGafFrameIndex];

            if (currentFrame.Image != null)
            {
                BitmapSource? bitmapImage = null;

                try
                {
                    if (currentFrame.Image is Image<Rgba32> rgbaImage)
                    {
                        bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaImage);
                    }
                    else
                    {
                        bitmapImage = ConvertImageSharpToBitmapImage(currentFrame.Image);
                    }
                }
                catch (Exception ex)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Error converting GAF frame:\n{ex.Message}";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                if (bitmapImage != null)
                {
                    TextScrollViewer.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ImageContentGrid.Visibility = Visibility.Visible;

                    ContentImage.Source = bitmapImage;

                    var totalFrames = currentEntry.Frames.Count;
                    var totalEntries = _currentGafEntries.Count;
                    FileInfoTextBlock.Text = $"GAF: {currentEntry.Name}\nEntry {_currentGafEntryIndex + 1}/{totalEntries}, Frame {_currentGafFrameIndex + 1}/{totalFrames}\nSize: {currentFrame.Image.Width}x{currentFrame.Image.Height}, Offset: ({currentFrame.XOffset}, {currentFrame.YOffset}), Compressed: {currentFrame.UseCompression}";

                    var hasMultipleFrames = totalFrames > 1;
                    var hasMultipleEntries = totalEntries > 1;
                    var showNavigation = hasMultipleFrames || hasMultipleEntries;

                    PreviousFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                    NextFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "GAF: Could not convert frame to image";
                    TextScrollViewer.ScrollToHome();
                }
            }
        }

        private void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGafEntries != null && _currentGafEntries.Count > 0)
            {
                var currentEntry = _currentGafEntries[_currentGafEntryIndex];
                _currentGafFrameIndex--;
                if (_currentGafFrameIndex < 0)
                {
                    _currentGafEntryIndex--;
                    if (_currentGafEntryIndex < 0)
                    {
                        _currentGafEntryIndex = _currentGafEntries.Count - 1;
                    }

                    currentEntry = _currentGafEntries[_currentGafEntryIndex];
                    _currentGafFrameIndex = currentEntry.Frames?.Count - 1 ?? 0;
                }

                DisplayGafFrame();
            }
            else if (_currentTafEntries != null && _currentTafEntries.Count > 0)
            {
                var currentEntry = _currentTafEntries[_currentTafEntryIndex];
                _currentTafFrameIndex--;
                if (_currentTafFrameIndex < 0)
                {
                    _currentTafEntryIndex--;
                    if (_currentTafEntryIndex < 0)
                    {
                        _currentTafEntryIndex = _currentTafEntries.Count - 1;
                    }

                    currentEntry = _currentTafEntries[_currentTafEntryIndex];
                    _currentTafFrameIndex = currentEntry.Frames?.Count - 1 ?? 0;
                }

                DisplayTafFrame();
            }
        }

        private void NextFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGafEntries != null && _currentGafEntries.Count > 0)
            {
                var currentEntry = _currentGafEntries[_currentGafEntryIndex];
                _currentGafFrameIndex++;
                if (currentEntry.Frames == null || _currentGafFrameIndex >= currentEntry.Frames.Count)
                {
                    _currentGafEntryIndex++;
                    if (_currentGafEntryIndex >= _currentGafEntries.Count)
                    {
                        _currentGafEntryIndex = 0;
                    }

                    _currentGafFrameIndex = 0;
                }

                DisplayGafFrame();
            }
            else if (_currentTafEntries != null && _currentTafEntries.Count > 0)
            {
                var currentEntry = _currentTafEntries[_currentTafEntryIndex];
                _currentTafFrameIndex++;
                if (currentEntry.Frames == null || _currentTafFrameIndex >= currentEntry.Frames.Count)
                {
                    _currentTafEntryIndex++;
                    if (_currentTafEntryIndex >= _currentTafEntries.Count)
                    {
                        _currentTafEntryIndex = 0;
                    }

                    _currentTafFrameIndex = 0;
                }

                DisplayTafFrame();
            }
        }

        private void DisplayTafFrame()
        {
            ContentImage.Source = null;

            if (ZoomSlider != null)
            {
                ZoomSlider.Value = 1.0;
            }
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = 1.0;
                ImageScaleTransform.ScaleY = 1.0;
            }
            if (ZoomValueTextBlock != null)
            {
                ZoomValueTextBlock.Text = "100%";
            }

            if (_currentTafEntries == null || _currentTafEntries.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = "TAF: No entries found";
                TextScrollViewer.ScrollToHome();
                return;
            }

            if (_currentTafEntryIndex >= _currentTafEntries.Count)
                _currentTafEntryIndex = 0;

            var currentEntry = _currentTafEntries[_currentTafEntryIndex];

            if (currentEntry.Frames == null || currentEntry.Frames.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"TAF: {currentEntry.Name}\nNo frames found";
                TextScrollViewer.ScrollToHome();
                PreviousFrameButton.Visibility = Visibility.Collapsed;
                NextFrameButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_currentTafFrameIndex >= currentEntry.Frames.Count)
                _currentTafFrameIndex = 0;

            var currentFrame = currentEntry.Frames[_currentTafFrameIndex];

            if (currentFrame.Image != null)
            {
                BitmapSource? bitmapImage = null;

                try
                {
                    if (currentFrame.Image is Image<Rgba32> rgbaImage)
                    {
                        bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaImage);
                    }
                    else
                    {
                        bitmapImage = ConvertImageSharpToBitmapImage(currentFrame.Image);
                    }
                }
                catch (Exception ex)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Error converting TAF frame:\n{ex.Message}";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                if (bitmapImage != null)
                {
                    TextScrollViewer.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ImageContentGrid.Visibility = Visibility.Visible;

                    ContentImage.Source = bitmapImage;

                    var totalFrames = currentEntry.Frames.Count;
                    var totalEntries = _currentTafEntries.Count;
                    var pixelFormatStr = currentFrame.PixelFormat == TafPixelFormat.Argb1555 ? "ARGB1555" : "ARGB4444";
                    FileInfoTextBlock.Text = $"TAF: {currentEntry.Name}\nEntry {_currentTafEntryIndex + 1}/{totalEntries}, Frame {_currentTafFrameIndex + 1}/{totalFrames}\nSize: {currentFrame.Image.Width}x{currentFrame.Image.Height}, Offset: ({currentFrame.XOffset}, {currentFrame.YOffset}), Format: {pixelFormatStr}";

                    var hasMultipleFrames = totalFrames > 1;
                    var hasMultipleEntries = totalEntries > 1;
                    var showNavigation = hasMultipleFrames || hasMultipleEntries;

                    PreviousFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                    NextFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "TAF: Could not convert frame to image";
                    TextScrollViewer.ScrollToHome();
                }
            }
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = e.NewValue;
                ImageScaleTransform.ScaleY = e.NewValue;

                if (ZoomValueTextBlock != null)
                {
                    ZoomValueTextBlock.Text = $"{(int)(e.NewValue * 100)}%";
                }
            }
        }

        private BitmapImage? ConvertPcxToBitmapImage(object? pcxImage)
        {
            if (pcxImage == null) return null;

            try
            {
                if (pcxImage is Image<Bgra32> imageSharpImage)
                {
                    return ConvertImageSharpToBitmapImage(imageSharpImage);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting PCX image: {ex.Message}");
            }

            return null;
        }

        private BitmapImage ConvertImageSharpToBitmapImage(Image<Bgra32> image)
        {
            using (var memoryStream = new MemoryStream())
            {
                image.Save(memoryStream, new PngEncoder());

                memoryStream.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memoryStream;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private BitmapSource ConvertImageSharpRgba32ToBitmapImage(Image<Rgba32> image)
        {
            using var bgraImage = image.CloneAs<Bgra32>();

            var width = bgraImage.Width;
            var height = bgraImage.Height;
            var dpi = 96d;
            var stride = width * 4;

            var pixelStructs = new Bgra32[width * height];
            bgraImage.CopyPixelDataTo(pixelStructs);

            var pixelBytes = MemoryMarshal.AsBytes(pixelStructs.AsSpan()).ToArray();

            var bitmap = BitmapSource.Create(
                width,
                height,
                dpi,
                dpi,
                PixelFormats.Bgra32,
                null,
                pixelBytes,
                stride);

            bitmap.Freeze();
            return bitmap;
        }

        private BitmapImage ConvertImageSharpToBitmapImage(SixLabors.ImageSharp.Image image)
        {
            using (var memoryStream = new MemoryStream())
            {
                image.Save(memoryStream, new PngEncoder());

                memoryStream.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memoryStream;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private BitmapSource? RenderTntV2WithTerrain(TntFile tntFile)
        {
            if (_terrainHpiProcessors.Count == 0 || tntFile.TerrainNames == null ||
                tntFile.UMapping == null || tntFile.VMapping == null)
            {
                return null;
            }

            try
            {
                const int GraphicUnitSize = 32;
                const int DataUnitSize = 16;
                const int TextureCellSize = 32; // Each UV cell is 32x32 in the 256x256 texture

                // Calculate dimensions
                var guWidth = tntFile.AttributeWidth * DataUnitSize / GraphicUnitSize;
                var guHeight = tntFile.AttributeHeight * DataUnitSize / GraphicUnitSize;
                var pixelWidth = guWidth * GraphicUnitSize;
                var pixelHeight = guHeight * GraphicUnitSize;

                var textureCache = new Dictionary<uint, Image<Rgba32>>();
                var outputImage = new Image<Rgba32>(pixelWidth, pixelHeight);

                for (var guY = 0; guY < guHeight; guY++)
                {
                    for (var guX = 0; guX < guWidth; guX++)
                    {
                        var guIndex = guY * guWidth + guX;
                        if (guIndex >= tntFile.TerrainNames.Count)
                            continue;

                        var terrainName = tntFile.TerrainNames[guIndex];
                        var u = tntFile.UMapping[guIndex];
                        var v = tntFile.VMapping[guIndex];

                        if (!textureCache.TryGetValue(terrainName, out var texture))
                        {
                            texture = LoadTerrainTexture(terrainName);
                            if (texture != null)
                            {
                                textureCache[terrainName] = texture;
                            }
                        }

                        if (texture == null)
                            continue;

                        // Calculate source position in texture (UV coords select 32x32 block)
                        var srcX = u * TextureCellSize;
                        var srcY = v * TextureCellSize;

                        // Calculate destination position
                        var destX = guX * GraphicUnitSize;
                        var destY = guY * GraphicUnitSize;

                        // Copy the 32x32 block from texture to output
                        outputImage.ProcessPixelRows(texture, (destAccessor, srcAccessor) =>
                        {
                            for (int y = 0; y < GraphicUnitSize; y++)
                            {
                                var srcRowY = srcY + y;
                                var destRowY = destY + y;

                                if (srcRowY >= texture.Height || destRowY >= outputImage.Height)
                                    continue;

                                var srcRow = srcAccessor.GetRowSpan(srcRowY);
                                var destRow = destAccessor.GetRowSpan(destRowY);

                                for (int x = 0; x < GraphicUnitSize; x++)
                                {
                                    var srcColX = srcX + x;
                                    var destColX = destX + x;

                                    if (srcColX >= texture.Width || destColX >= outputImage.Width)
                                        continue;

                                    destRow[destColX] = srcRow[srcColX];
                                }
                            }
                        });
                    }
                }

                foreach (var texture in textureCache.Values)
                {
                    texture.Dispose();
                }

                var result = ConvertImageSharpRgba32ToBitmapImage(outputImage);
                outputImage.Dispose();

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error rendering TNT V2 with terrain: {ex.Message}");
                return null;
            }
        }

        private Image<Rgba32>? LoadTerrainTexture(uint terrainName)
        {
            if (_terrainHpiProcessors.Count == 0)
                return null;

            var filename = $"{terrainName:X8}.JPG";
            var relativePath = $"terrain\\{filename}";
            var relativePathLower = $"terrain\\{filename.ToLowerInvariant()}";

            foreach (var processor in _terrainHpiProcessors)
            {
                try
                {
                    var jpgData = processor.Extract(relativePath);
                    if (jpgData == null || jpgData.Length == 0)
                    {
                        jpgData = processor.Extract(relativePathLower);
                    }

                    if (jpgData != null && jpgData.Length > 0)
                    {
                        using var ms = new MemoryStream(jpgData);
                        return SixLabors.ImageSharp.Image.Load<Rgba32>(ms);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting terrain texture {terrainName:X8} from HPI: {ex.Message}");
                }
            }

            return null;
        }

        private void DisplayAudio(byte[] audioData, string extension)
        {
            try
            {
                StopAudio(disposeResources: true);

                TextScrollViewer.Visibility = Visibility.Collapsed;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Visible;

                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                AudioPositionSlider.Value = 0;
                CurrentTimeTextBlock.Text = "00:00";
                TotalTimeTextBlock.Text = "00:00";

                var memoryStream = new MemoryStream(audioData);
                _waveStream = new WaveFileReader(memoryStream);
                _audioTotalDuration = _waveStream.TotalTime;

                long totalSamples = _waveStream.Length / (_waveStream.WaveFormat.BitsPerSample / 8);

                var sampleProvider = _waveStream.ToSampleProvider();
                _fadeOutProvider = new FadeOutSampleProvider(sampleProvider, totalSamples);

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_fadeOutProvider);
                _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;

                TotalTimeTextBlock.Text = FormatTimeSpan(_audioTotalDuration);
                AudioPositionSlider.Maximum = _audioTotalDuration.TotalSeconds;

                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;

                FileInfoTextBlock.Text = $"WAV Audio File ({audioData.Length} bytes)";
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error loading audio:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();

                StopAudio(disposeResources: true);
                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
            }
        }

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StopAudioPositionTimer();

                if (_waveStream != null)
                {
                    _waveStream.Position = 0;
                }

                _fadeOutProvider?.Reset();

                AudioPositionSlider.Value = 0;
                CurrentTimeTextBlock.Text = "00:00";

                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
            });
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waveOut != null && _waveStream != null)
            {
                _waveOut.Play();
                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;

                StartAudioPositionTimer();
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waveOut != null)
            {
                _waveOut.Pause();
                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                StopAudioPositionTimer();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopAudio();
        }

        private void StopAudio(bool disposeResources = false)
        {
            StopAudioPositionTimer();

            // Unsubscribe from event to prevent a race condition which prevents a new file being played 
            // Race condition results in: 1st file plays, 2nd does not, 3rd does play, etc.
            if (_waveOut != null)
            {
                try
                {
                    _waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                }
                catch { }
            }

            try
            {
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    if (disposeResources)
                    {
                        _waveOut.Dispose();
                        _waveOut = null;
                    }
                }

                if (_waveStream != null)
                {
                    if (disposeResources)
                    {
                        _waveStream.Dispose();
                        _waveStream = null;
                        _fadeOutProvider = null;
                    }
                    else
                    {
                        // Reset stream position for replay
                        _waveStream.Position = 0;
                        _fadeOutProvider?.Reset();
                    }
                }
            }
            catch { }

            AudioPositionSlider.Value = 0;
            CurrentTimeTextBlock.Text = "00:00";

            // Reset button states
            PlayButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
        }

        private void StartAudioPositionTimer()
        {
            StopAudioPositionTimer();

            _audioPositionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioPositionTimer.Tick += AudioPositionTimer_Tick;
            _audioPositionTimer.Start();
        }

        private void StopAudioPositionTimer()
        {
            if (_audioPositionTimer != null)
            {
                _audioPositionTimer.Stop();
                _audioPositionTimer.Tick -= AudioPositionTimer_Tick;
                _audioPositionTimer = null;
            }
        }

        private void AudioPositionTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDraggingAudioSlider && _waveStream != null)
            {
                var position = _waveStream.CurrentTime;
                CurrentTimeTextBlock.Text = FormatTimeSpan(position);
                AudioPositionSlider.Value = position.TotalSeconds;
            }
        }

        private void AudioPositionSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingAudioSlider = true;
        }

        private void AudioPositionSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingAudioSlider && _waveStream != null)
            {
                _isDraggingAudioSlider = false;
                var newPosition = TimeSpan.FromSeconds(AudioPositionSlider.Value);
                _waveStream.CurrentTime = newPosition;
                CurrentTimeTextBlock.Text = FormatTimeSpan(newPosition);
            }
        }

        private void AudioPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingAudioSlider && _waveStream != null)
            {
                var newPosition = TimeSpan.FromSeconds(e.NewValue);
                CurrentTimeTextBlock.Text = FormatTimeSpan(newPosition);
            }
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
            else
            {
                return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
        }

        private string FormatHexDump(byte[] data, string filePath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Binary file: {filePath}");
            sb.AppendLine($"File size: {data.Length} bytes");
            sb.AppendLine();
            sb.AppendLine("Hex dump:");
            sb.AppendLine();

            const int bytesPerLine = 16;
            const int maxBytes = 1024 * 16; // Limit to first 16KB for performance

            int bytesToShow = Math.Min(data.Length, maxBytes);

            for (int i = 0; i < bytesToShow; i += bytesPerLine)
            {
                // Offset
                sb.Append($"{i:X8}  ");

                // Hex bytes
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < bytesToShow)
                    {
                        sb.Append($"{data[i + j]:X2} ");
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    // Spacing after 8 bytes
                    if (j == 7)
                    {
                        sb.Append(" ");
                    }
                }

                sb.Append(" |");

                // ASCII representation
                for (int j = 0; j < bytesPerLine && i + j < bytesToShow; j++)
                {
                    byte b = data[i + j];
                    char c = (b >= 32 && b < 127) ? (char)b : '.';
                    sb.Append(c);
                }

                sb.AppendLine("|");
            }

            if (data.Length > maxBytes)
            {
                sb.AppendLine();
                sb.AppendLine($"... ({data.Length - maxBytes} more bytes not shown)");
            }

            return sb.ToString();
        }

        private void Display3DModel(byte[] modelData, string extension)
        {
            try
            {
                StopAudio(disposeResources: true);

                TextScrollViewer.Visibility = Visibility.Collapsed;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                Model3DContentGrid.Visibility = Visibility.Visible;

                var processor = new ThreeDOProcessor();
                var threeDOFile = processor.Read(modelData);

                if (threeDOFile?.RootObject == null)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "Error: Could not parse 3DO file";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                var model3DGroup = ThreeDOConverter.ConvertToModel3DGroup(threeDOFile.RootObject);

                if (model3DGroup.Children.Count == 0)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    Model3DContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "3DO file contains no renderable geometry";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                // Calculate model bounds to center and scale
                var bounds = CalculateModelBounds(model3DGroup);
                var center = new Point3D(
                    (bounds.Item1.X + bounds.Item2.X) / 2,
                    (bounds.Item1.Y + bounds.Item2.Y) / 2,
                    (bounds.Item1.Z + bounds.Item2.Z) / 2
                );

                // Center
                var centerTransform = new TranslateTransform3D(-center.X, -center.Y, -center.Z);
                model3DGroup.Transform = centerTransform;

                // Camera distance
                var size = new Vector3D(
                    bounds.Item2.X - bounds.Item1.X,
                    bounds.Item2.Y - bounds.Item1.Y,
                    bounds.Item2.Z - bounds.Item1.Z
                );
                var maxDimension = Math.Max(Math.Max(size.X, size.Y), size.Z);
                _initialCameraDistance = maxDimension * 2.5;

                // Reset camera
                _rotationX3D = AppSettings.Instance.Model3DDefaultRotationX;
                _rotationY3D = AppSettings.Instance.Model3DDefaultRotationY;
                ResetCamera3D();

                var rotateTransformGroup = new Transform3DGroup();
                rotateTransformGroup.Children.Add(centerTransform);
                rotateTransformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _rotationX3D)));
                rotateTransformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _rotationY3D)));
                model3DGroup.Transform = rotateTransformGroup;

                Model3DContainer.Content = model3DGroup;

                Model3DViewport.MouseLeftButtonDown += Model3DViewport_MouseLeftButtonDown;
                Model3DViewport.MouseMove += Model3DViewport_MouseMove;
                Model3DViewport.MouseLeftButtonUp += Model3DViewport_MouseLeftButtonUp;
                Model3DViewport.MouseWheel += Model3DViewport_MouseWheel;

                UpdateModel3DRotation();

                FileInfoTextBlock.Text = ThreeDOConverter.GetModelInfo(threeDOFile);
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                Model3DContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error displaying 3D model:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private (Point3D, Point3D) CalculateModelBounds(Model3DGroup modelGroup)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var child in modelGroup.Children)
            {
                if (child is GeometryModel3D geometryModel && geometryModel.Geometry is MeshGeometry3D mesh)
                {
                    foreach (var position in mesh.Positions)
                    {
                        minX = Math.Min(minX, position.X);
                        minY = Math.Min(minY, position.Y);
                        minZ = Math.Min(minZ, position.Z);
                        maxX = Math.Max(maxX, position.X);
                        maxY = Math.Max(maxY, position.Y);
                        maxZ = Math.Max(maxZ, position.Z);
                    }
                }
            }

            return (new Point3D(minX, minY, minZ), new Point3D(maxX, maxY, maxZ));
        }

        private void ResetCamera3D()
        {
            if (Camera3D != null)
            {
                Camera3D.Position = new Point3D(0, 0, _initialCameraDistance);
                Camera3D.LookDirection = new Vector3D(0, 0, -1);
                Camera3D.UpDirection = new Vector3D(0, 1, 0);
                Camera3D.FieldOfView = 60;

                if (Model3DZoomSlider != null)
                {
                    Model3DZoomSlider.Value = 1.0;
                    Model3DZoomSlider.Minimum = 0.1;
                    Model3DZoomSlider.Maximum = 10.0;
                }
            }
        }

        private void ResetCameraButton_Click(object sender, RoutedEventArgs e)
        {
            _rotationX3D = AppSettings.Instance.Model3DDefaultRotationX;
            _rotationY3D = AppSettings.Instance.Model3DDefaultRotationY;
            ResetCamera3D();
            UpdateModel3DRotation();
        }

        private void Model3DZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Camera3D != null && _initialCameraDistance > 0)
            {
                var cameraDistance = _initialCameraDistance / e.NewValue;
                Camera3D.Position = new Point3D(0, 0, cameraDistance);

                if (Model3DZoomValueTextBlock != null)
                {
                    var zoomPercent = (int)(e.NewValue * 100);
                    Model3DZoomValueTextBlock.Text = $"{zoomPercent}%";
                }
            }
        }

        private void Model3DViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRotating3D = true;
            _lastMousePosition3D = e.GetPosition(Model3DViewport);
            Model3DViewport.CaptureMouse();
        }

        private void Model3DViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isRotating3D && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPosition = e.GetPosition(Model3DViewport);
                var delta = currentPosition - _lastMousePosition3D;

                _rotationY3D += delta.X * 0.5;
                _rotationX3D += delta.Y * 0.5;

                // Keep rotation in reasonable bounds
                _rotationX3D = Math.Max(-89, Math.Min(89, _rotationX3D));

                UpdateModel3DRotation();

                _lastMousePosition3D = currentPosition;
            }
        }

        private void Model3DViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRotating3D = false;
            Model3DViewport.ReleaseMouseCapture();
        }

        private void Model3DViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Model3DZoomSlider != null)
            {
                var delta = e.Delta > 0 ? 0.05 : -0.05;
                var newValue = Model3DZoomSlider.Value + delta;
                newValue = Math.Max(Model3DZoomSlider.Minimum, Math.Min(Model3DZoomSlider.Maximum, newValue));
                Model3DZoomSlider.Value = newValue;
            }
        }

        private void UpdateModel3DRotation()
        {
            if (Model3DContainer.Content is Model3DGroup modelGroup)
            {
                var transforms = new Transform3DGroup();

                if (modelGroup.Transform is Transform3DGroup existingGroup && existingGroup.Children.Count > 0)
                {
                    transforms.Children.Add(existingGroup.Children[0]); // Keep center transform
                }
                else if (modelGroup.Transform is TranslateTransform3D existingTranslate)
                {
                    transforms.Children.Add(existingTranslate);
                }

                transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _rotationX3D)));
                transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _rotationY3D)));

                modelGroup.Transform = transforms;
            }

            RotationXValueTextBlock?.Text = $"{_rotationX3D:F1}°";
            RotationYValueTextBlock?.Text = $"{_rotationY3D:F1}°";
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAudio(disposeResources: true);

            base.OnClosed(e);
        }
    }
}

using ii.Ascend;
using ii.Ascend.Model;
using MahApps.Metro.Controls;
using MeltySynth;
using NAudio.Wave;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DescentView
{
	public class ArchiveFileEntry
	{
		public string FileName { get; set; } = string.Empty;
		public string RelativePath { get; set; } = string.Empty;
		public byte[] Data { get; set; } = Array.Empty<byte>();
		// PIG image metadata (for BBM rendering)
		public short Width { get; set; }
		public short Height { get; set; }
		public bool IsRleCompressed { get; set; }
		public byte Flags { get; set; }
	}

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
		private Dictionary<TreeViewItem, ArchiveFileEntry> _filePathMap = new();
		private Dictionary<TreeViewItem, ExternalFileEntry> _externalFileMap = new();
		private HashSet<string> _deletedArchiveFiles = new(StringComparer.OrdinalIgnoreCase);
		private string? _currentArchiveFilePath;
		private List<ArchiveFileEntry>? _currentArchiveFiles;
		private bool _isPigFile = false;
		private string? _lastOpenFolder;
		private string? _lastSaveFolder;

		// File type filter state
		private HashSet<string> _checkedExtensions = new(StringComparer.OrdinalIgnoreCase);

		// Palette state (for BBM images)
		private List<string> _availablePalettes = new();
		private string? _selectedPalettePath;
		private bool _isPaletteChangeInProgress = false;

		// Current file view state
		private byte[]? _currentFileData;
		private ArchiveFileEntry? _currentFileEntry;
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

		// FNT font state
		private FontData? _currentFontData;

		// MIDI playback state
		private Synthesizer? _midiSynthesizer;
		private MidiFileSequencer? _midiSequencer;
		private MidiSampleProvider? _midiSampleProvider;
		private MidiFile? _currentMidiFile;
		private byte[]? _currentMidiFileData;

		// Game data state (for PIG files)
		private D1PigGameData? _currentGameData;
		private Dictionary<TreeViewItem, D1PigGameData> _gameDataMap = new();

		public MainWindow(string? filePath = null)
		{
			InitializeComponent();

			// Register code page encodings (required for .NET Core/.NET 5+)
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			// Set up title bar theming
			ThemeManager.InitializeWindow(this);

			ApplyFontSettings();
			OptionsWindow.FontSettingsChanged += ApplyFontSettings;

			// Populate palette dropdown
			PopulatePaletteDropdown();

			// Load file if provided via command line
			if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
			{
				try
				{
					LoadArchiveFile(filePath);
				}
				catch (Exception ex)
				{
					MessageBox.Show($"Error opening archive file:\n{ex.Message}",
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

			var palFiles = Directory.GetFiles(palettesFolder, "*.256", SearchOption.TopDirectoryOnly)
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
					if (extension == ".bbm" || extension == ".iff")
					{
						DisplayImage(_currentFileData, extension);
					}
				}
			}
		}

		private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
		{
			var dialog = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "Archive Files (*.pig;*.hog)|*.pig;*.hog|PIG Files (*.pig)|*.pig|HOG Files (*.hog)|*.hog|All Files (*.*)|*.*",
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
					LoadArchiveFile(dialog.FileName);
				}
				catch (Exception ex)
				{
					MessageBox.Show($"Error opening archive file:\n{ex.Message}",
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
			if (string.IsNullOrEmpty(_currentArchiveFilePath) || _currentArchiveFiles == null)
			{
				MessageBox.Show("No archive file is currently open.",
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
				if (_currentArchiveFiles.Count == 0)
				{
					MessageBox.Show("No files found in archive.",
								   "Information",
								   MessageBoxButton.OK,
								   MessageBoxImage.Information);
					return;
				}

				int extractedCount = 0;
				int errorCount = 0;

				if (_currentGameData != null)
				{
					try
					{
						var gameDataPath = Path.Combine(targetFolder, "game-data.txt");
						var textContent = FormatGameDataAsText(_currentGameData);
						File.WriteAllText(gameDataPath, textContent, Encoding.UTF8);
						extractedCount++;
					}
					catch
					{
						errorCount++;
					}
				}

				foreach (var file in _currentArchiveFiles)
				{
					try
					{
						var targetPath = Path.Combine(targetFolder, file.RelativePath);
						var targetDir = Path.GetDirectoryName(targetPath);

						if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
						{
							Directory.CreateDirectory(targetDir);
						}

						if (file.Data != null && file.Data.Length > 0)
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
			if (string.IsNullOrEmpty(_currentArchiveFilePath))
			{
				MessageBox.Show("No archive file is currently open.",
							   "Information",
							   MessageBoxButton.OK,
							   MessageBoxImage.Information);
				return;
			}

			SaveArchive(_currentArchiveFilePath);
		}

		private void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
		{
			if (string.IsNullOrEmpty(_currentArchiveFilePath))
			{
				MessageBox.Show("No archive file is currently open.",
							   "Information",
							   MessageBoxButton.OK,
							   MessageBoxImage.Information);
				return;
			}

			var dialog = new Microsoft.Win32.SaveFileDialog
			{
				FileName = Path.GetFileName(_currentArchiveFilePath),
				Filter = "HOG Files (*.hog)|*.hog|PIG Files (*.pig)|*.pig|All Files (*.*)|*.*",
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
			SaveMenuItem.IsEnabled = !string.IsNullOrEmpty(_currentArchiveFilePath) && hasModifications && !_isPigFile; // PIG files are read-only
		}

		private void SaveArchive(string outputPath)
		{
			if (_currentArchiveFiles == null)
			{
				MessageBox.Show("No archive is currently loaded.",
							   "Error",
							   MessageBoxButton.OK,
							   MessageBoxImage.Error);
				return;
			}

			if (_isPigFile)
			{
				MessageBox.Show("PIG files are read-only and cannot be saved.",
							   "Information",
							   MessageBoxButton.OK,
							   MessageBoxImage.Information);
				return;
			}

			try
			{
				var filesToSave = new List<(string filename, byte[] bytes)>();

				foreach (var file in _currentArchiveFiles)
				{
					if (_deletedArchiveFiles.Contains(file.RelativePath))
						continue;

					if (file.Data != null && file.Data.Length > 0)
					{
						filesToSave.Add((file.RelativePath, file.Data));
					}
				}

				foreach (var kvp in _externalFileMap)
				{
					var externalEntry = kvp.Value;
					if (externalEntry.Data != null && externalEntry.Data.Length > 0)
					{
						filesToSave.Add((externalEntry.RelativePath, externalEntry.Data));
					}
				}

				var processor = new HogProcessor();
				processor.Write(outputPath, filesToSave);

				MessageBox.Show($"Archive saved successfully to:\n{outputPath}",
							   "Success",
							   MessageBoxButton.OK,
							   MessageBoxImage.Information);

				LoadArchiveFile(outputPath);
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
			if (!string.IsNullOrEmpty(_currentArchiveFilePath) && _currentArchiveFiles != null)
			{
				BuildTreeView();
			}
		}

		private async void LoadArchiveFile(string filePath)
		{
			_currentArchiveFilePath = filePath;
			_deletedArchiveFiles.Clear();
			_externalFileMap.Clear();
			_currentGameData = null;
			_gameDataMap.Clear();
			ContentTextBox.Text = string.Empty;
			FileInfoTextBlock.Text = $"Loading: {Path.GetFileName(filePath)}...";
			Title = $"{Path.GetFileName(filePath)} - DescentView";

			ExtractAllMenuItem.IsEnabled = false;
			SaveMenuItem.IsEnabled = false;
			SaveAsMenuItem.IsEnabled = false;
			FilterDropdownButton.IsEnabled = false;

			try
			{
				List<ArchiveFileEntry>? archiveFiles = null;
				var extension = Path.GetExtension(filePath).ToLower();
				_isPigFile = extension == ".pig";

				await Task.Run(() =>
				{
					if (_isPigFile)
					{
						var processor = new PigProcessor();
						var (images, sounds, data) = processor.ReadDetailed(filePath);
						
						_currentGameData = data;
						
						archiveFiles = images.Select(img => new ArchiveFileEntry
						{
							FileName = img.Filename,
							RelativePath = img.Filename,
							Data = img.Data,
							Width = img.Width,
							Height = img.Height,
							IsRleCompressed = img.IsRleCompressed,
							Flags = img.Flags
						}).ToList();
						// Add sounds to archive files
						archiveFiles.AddRange(sounds.Select(s => new ArchiveFileEntry
						{
							FileName = s.Filename,
							RelativePath = s.Filename,
							Data = s.Data
						}));
					}
					else
					{
						var processor = new HogProcessor();
						var files = processor.Read(filePath);
						archiveFiles = files.Select(f => new ArchiveFileEntry
						{
							FileName = f.filename,
							RelativePath = f.filename,
							Data = f.bytes
						}).ToList();
					}
				});

				_currentArchiveFiles = archiveFiles;

				if (archiveFiles == null || archiveFiles.Count == 0)
				{
					MessageBox.Show("No files found in archive.",
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
				SaveAsMenuItem.IsEnabled = !_isPigFile;
				UpdateSaveMenuItemState();

				// Build tree view (already async)
				BuildTreeView();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Error loading archive file:\n{ex.Message}",
							   "Error",
							   MessageBoxButton.OK,
							   MessageBoxImage.Error);
				FileInfoTextBlock.Text = "Error loading file";
			}
		}

		private void BuildFileTypeFilter()
		{
			if (_currentArchiveFiles == null)
				return;

			FilterCheckboxPanel.Children.Clear();
			_checkedExtensions.Clear();

			// Get all unique extensions, sorted alphabetically
			var extensions = _currentArchiveFiles
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
			if (_currentArchiveFiles == null || _currentArchiveFilePath == null)
				return;

			FileTreeView.Items.Clear();
			_filePathMap.Clear();
			_externalFileMap.Clear();
			_gameDataMap.Clear();

			var sortAlphabetically = SortAlphabeticallyButton.IsChecked == true;

			// Build tree structure
			var rootNode = new TreeViewItem
			{
				Header = Path.GetFileName(_currentArchiveFilePath),
				Tag = "ROOT"
			};

			// Get files, optionally sorted and filtered
			var files = sortAlphabetically
				? _currentArchiveFiles.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()
				: _currentArchiveFiles.ToList();

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

		private async void BuildTreeViewAsync(TreeViewItem rootNode, List<ArchiveFileEntry> files)
		{
			const int batchSize = 100;
			var directoryMap = new Dictionary<string, TreeViewItem>();

			FileTreeView.Items.Add(rootNode);
			rootNode.IsExpanded = true;

			// Add game data entry at the top if available
			if (_currentGameData != null)
			{
				var gameDataNode = new TreeViewItem
				{
					Header = "<game-data>",
					Tag = "GAMEDATA"
				};

				var contextMenu = new ContextMenu();
				var extractMenuItem = new MenuItem
				{
					Header = "Extract",
					Tag = gameDataNode
				};
				extractMenuItem.Click += ExtractMenuItem_Click;
				contextMenu.Items.Add(extractMenuItem);
				gameDataNode.ContextMenu = contextMenu;

				rootNode.Items.Insert(0, gameDataNode);
				_gameDataMap[gameDataNode] = _currentGameData;
			}

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
				if (selectedItem.Tag is string tag && tag == "GAMEDATA" && _gameDataMap.TryGetValue(selectedItem, out var gameData))
				{
					DisplayGameData(gameData);
				}
				else if (_filePathMap.TryGetValue(selectedItem, out var fileEntry))
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
			// Files have ArchiveFileEntry as Tag or are in _filePathMap or _externalFileMap
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
			}
		}

		private void ExtractMenuItem_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not MenuItem menuItem || menuItem.Tag is not TreeViewItem treeViewItem)
				return;

			// Check if this is game data
			if (treeViewItem.Tag is string tag && tag == "GAMEDATA" && _gameDataMap.TryGetValue(treeViewItem, out var gameData))
			{
				var dialog = new Microsoft.Win32.SaveFileDialog
				{
					FileName = "game-data.txt",
					Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
					Title = "Extract Game Data"
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

					try
					{
						var textContent = FormatGameDataAsText(gameData);
						File.WriteAllText(dialog.FileName, textContent, Encoding.UTF8);
						MessageBox.Show($"Game data extracted successfully to:\n{dialog.FileName}",
									   "Success",
									   MessageBoxButton.OK,
									   MessageBoxImage.Information);
					}
					catch (Exception ex)
					{
						MessageBox.Show($"Error extracting game data:\n{ex.Message}",
									   "Error",
									   MessageBoxButton.OK,
									   MessageBoxImage.Error);
					}
				}
				return;
			}

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
			var dialog2 = new Microsoft.Win32.SaveFileDialog
			{
				FileName = fileName,
				Filter = "All Files (*.*)|*.*",
				Title = "Extract File"
			};

			// Set initial directory from last used save folder
			if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
			{
				dialog2.InitialDirectory = _lastSaveFolder;
			}

			if (dialog2.ShowDialog() == true)
			{
				// Save the folder for next time
				var folder = Path.GetDirectoryName(dialog2.FileName);
				if (!string.IsNullOrEmpty(folder))
				{
					_lastSaveFolder = folder;
				}

				try
				{
					File.WriteAllBytes(dialog2.FileName, fileData);
					MessageBox.Show($"File extracted successfully to:\n{dialog2.FileName}",
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

		private void DisplayFileContent(ArchiveFileEntry fileEntry)
		{
			try
			{
				var filePath = fileEntry.RelativePath;
				FileInfoTextBlock.Text = $"File: {filePath}";

				if (_currentArchiveFilePath == null || _currentArchiveFiles == null)
				{
					ContentTextBox.Text = "No archive file loaded.";
					ViewTogglePanel.Visibility = Visibility.Collapsed;
					return;
				}

				byte[]? fileData = GetFileData(fileEntry);

				if (fileData == null || fileData.Length == 0)
				{
					TextScrollViewer.Visibility = Visibility.Visible;
					ImageContentGrid.Visibility = Visibility.Collapsed;
					AudioContentGrid.Visibility = Visibility.Collapsed;
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
					ContentTextBox.Text = "(empty file)";
					ViewTogglePanel.Visibility = Visibility.Collapsed;
					TextScrollViewer.ScrollToHome();
					return;
				}

				var tempEntry = new ArchiveFileEntry
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
				ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();
			}
		}

		private void DisplayFileContentInternal(ArchiveFileEntry fileEntry, byte[]? fileData)
		{
			try
			{
				var filePath = fileEntry.RelativePath;

				if (fileData == null || fileData.Length == 0)
				{
					TextScrollViewer.Visibility = Visibility.Visible;
					ImageContentGrid.Visibility = Visibility.Collapsed;
					AudioContentGrid.Visibility = Visibility.Collapsed;
					ContentTextBox.Text = "(empty file)";
					TextScrollViewer.ScrollToHome();
					return;
				}

				var extension = Path.GetExtension(filePath).ToLower();

				if (extension == ".wav" || extension == ".raw")
				{
					DisplayAudio(fileData, extension);
					return;
				}

				if (extension == ".mid")
				{
					DisplayMidi(fileData);
					return;
				}

				if (extension == ".fnt")
				{
					DisplayFont(fileData);
					return;
				}

				if (extension == ".pcx" || extension == ".bmp" || extension == ".bbm" || extension == ".iff" ||
					extension == ".jpg" || extension == ".jpeg" || extension == ".png")
				{
					DisplayImage(fileData, extension);
					return;
				}

				if (extension == ".256")
				{
					DisplayPalette(fileData);
					return;
				}

			ShowPaletteSelector(false);
			TextScrollViewer.Visibility = Visibility.Visible;
			ImageContentGrid.Visibility = Visibility.Collapsed;
			AudioContentGrid.Visibility = Visibility.Collapsed;

			try
			{
				string content;
					var encoding = Encoding.GetEncoding(1252);
					switch (extension)
					{
						case ".msn":
						case ".mn2":
							content = encoding.GetString(fileData);
							break;

						case ".sng":
							content = encoding.GetString(fileData);
							break;

						case ".txb":
							try
							{
								var txbProcessor = new TxbProcessor();
								content = txbProcessor.Read(fileData);
							}
							catch (Exception ex)
							{
								content = $"Error reading TXB file:\n{ex.Message}\n\nHex dump:\n\n{FormatHexDump(fileData, filePath)}";
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
					ContentTextBox.Text = $"Error processing file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
					TextScrollViewer.ScrollToHome();
				}
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();
			}
		}

		private byte[]? GetFileData(ArchiveFileEntry fileEntry)
		{
			return fileEntry.Data;
		}

		private void DisplayGameData(D1PigGameData gameData)
		{
			try
			{
				ShowPaletteSelector(false);
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ViewTogglePanel.Visibility = Visibility.Collapsed;

				var textContent = FormatGameDataAsText(gameData);
				ContentTextBox.Text = textContent;
				FileInfoTextBlock.Text = "Game Data";
				TextScrollViewer.ScrollToHome();
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error displaying game data:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();
			}
		}

		private string FormatGameDataAsText(D1PigGameData gameData)
		{
			var sb = new StringBuilder();
			FormatObjectProperties(sb, gameData, 0);
			return sb.ToString();
		}

		private void FormatObjectProperties(StringBuilder sb, object obj, int indentLevel)
		{
			var indent = new string(' ', indentLevel * 2);
			var type = obj.GetType();

			foreach (var prop in type.GetProperties())
			{
				var value = prop.GetValue(obj);
				var name = prop.Name;

				if (value == null)
				{
					sb.AppendLine($"{indent}{name}: (null)");
				}
				else if (value is byte[] bytes)
				{
					sb.AppendLine($"{indent}{name}: {bytes.Length} bytes");
				}
				else if (value is int[] intArray)
				{
					sb.AppendLine($"{indent}{name}: {intArray.Length} entries");
				}
				else if (value is Array array)
				{
					sb.AppendLine($"{indent}{name}: {array.Length} entries");
				}
				else if (value is System.Collections.IList list)
				{
					sb.AppendLine($"{indent}{name}: {list.Count} items");
				}
				else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string))
				{
					sb.AppendLine($"{indent}{name}:");
					FormatObjectProperties(sb, value, indentLevel + 1);
				}
				else
				{
					sb.AppendLine($"{indent}{name}: {value}");
				}
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
				ImageContentGrid.Visibility = Visibility.Visible;

				// Hide palette selector by default (BBM/IFF will show it)
				ShowPaletteSelector(false);


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
				else if (extension == ".bbm" || extension == ".iff")
				{
					try
					{
						if (BbmProcessor.IsIffFormat(imageData))
						{
							var bbmProcessor = new BbmProcessor();
							// IFF files contain their own dimensions
							var image = bbmProcessor.Read(imageData, 0, 0, []);
							bitmapImage = ConvertImageSharpRgba32ToBitmapImage(image);
							imageInfo = $"IFF Image: {image.Width}x{image.Height}";
						}
						else
						{
							ShowPaletteSelector(true);

							if (string.IsNullOrEmpty(_selectedPalettePath) || !File.Exists(_selectedPalettePath))
							{
								imageInfo = "BBM File: No palette selected or palette file not found";
							}
							else if (_currentFileEntry == null || (_currentFileEntry.Width == 0 && _currentFileEntry.Height == 0))
							{
								imageInfo = "BBM File: No dimension metadata available";
							}
							else
							{
								// Load palette
								var paletteBytes = File.ReadAllBytes(_selectedPalettePath);
								var paletteProcessor = new TwoFiveSixProcessor();
								var paletteFile = paletteProcessor.Read(paletteBytes);
								var palette = paletteFile.Palette.Select(p => (p.red, p.green, p.blue)).ToList();

								var bbmProcessor = new BbmProcessor();
								var image = bbmProcessor.Read(
									imageData,
									_currentFileEntry.Width,
									_currentFileEntry.Height,
									palette,
									_currentFileEntry.IsRleCompressed,
									_currentFileEntry.Flags);
								bitmapImage = ConvertImageSharpRgba32ToBitmapImage(image);
								imageInfo = $"BBM Image: {_currentFileEntry.Width}x{_currentFileEntry.Height}" +
									(_currentFileEntry.IsRleCompressed ? " (RLE)" : "");
							}
						}
					}
					catch (Exception ex)
					{
						imageInfo = $"BBM/IFF File: Error loading - {ex.Message}";
					}
				}

				if (bitmapImage != null)
				{
					ContentImage.Source = bitmapImage;
					if (!string.IsNullOrEmpty(imageInfo))
					{
						FileInfoTextBlock.Text = imageInfo;
					}

				}
				else
				{
					TextScrollViewer.Visibility = Visibility.Visible;
					ImageContentGrid.Visibility = Visibility.Collapsed;
					AudioContentGrid.Visibility = Visibility.Collapsed;
					ContentTextBox.Text = $"Could not display image: {extension}\n\n{imageInfo ?? ""}";
					TextScrollViewer.ScrollToHome();
				}
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
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


		private void DisplayAudio(byte[] audioData, string extension)
		{
			try
			{
				StopAudio(disposeResources: true);

				ShowPaletteSelector(false);
				TextScrollViewer.Visibility = Visibility.Collapsed;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Visible;

				PlayButton.IsEnabled = false;
				PauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
				AudioPositionSlider.Value = 0;
				CurrentTimeTextBlock.Text = "00:00";
				TotalTimeTextBlock.Text = "00:00";

				byte[] wavData;
				if (extension == ".raw")
				{
					var rawProcessor = new RawProcessor();
					wavData = rawProcessor.Convert(audioData, sampleRate: 11025);
					FileInfoTextBlock.Text = $"RAW Audio File ({audioData.Length} bytes, converted to WAV)";
				}
				else
				{
					wavData = audioData;
					FileInfoTextBlock.Text = $"WAV Audio File ({audioData.Length} bytes)";
				}

				var memoryStream = new MemoryStream(wavData);
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
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error loading audio:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();

				StopAudio(disposeResources: true);
				PlayButton.IsEnabled = false;
				PauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
			}
		}

		private void DisplayMidi(byte[] midiData)
		{
			try
			{
				StopAudio(disposeResources: true);

				ShowPaletteSelector(false);
				TextScrollViewer.Visibility = Visibility.Collapsed;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Visible;

				PlayButton.IsEnabled = false;
				PauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
				AudioPositionSlider.Value = 0;
				CurrentTimeTextBlock.Text = "00:00";
				TotalTimeTextBlock.Text = "00:00";

				string soundFontPath;
				if (string.IsNullOrEmpty(AppSettings.Instance.SoundFontPath))
				{
					var exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
					soundFontPath = Path.Combine(exeDirectory ?? "", "soundfonts", "Yamaha DB50XG Presets.sf2");
				}
				else
				{
					soundFontPath = AppSettings.Instance.SoundFontPath;
				}

				if (!File.Exists(soundFontPath))
				{
					throw new FileNotFoundException($"SoundFont file not found: {soundFontPath}");
				}

				// Store MIDI file data for later playback (don't create synthesizer yet)
				using var midiStream = new MemoryStream(midiData);
				_currentMidiFile = new MidiFile(midiStream);
				_currentMidiFileData = midiData;

				_audioTotalDuration = _currentMidiFile.Length;

				TotalTimeTextBlock.Text = FormatTimeSpan(_audioTotalDuration);
				AudioPositionSlider.Maximum = _audioTotalDuration.TotalSeconds;

				FileInfoTextBlock.Text = $"MIDI File ({midiData.Length} bytes)";

				if (_waveOut == null)
				{
					_waveOut = new WaveOutEvent();
				}

				PlayButton.IsEnabled = true;
				PauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error loading MIDI:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();

				StopAudio(disposeResources: true);
				PlayButton.IsEnabled = false;
				PauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
			}
		}

		private void DisplayFont(byte[] fontData)
		{
			try
			{
				ShowPaletteSelector(false);
				TextScrollViewer.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ImageContentGrid.Visibility = Visibility.Visible;

				var fntProcessor = new FntProcessor();
				var font = fntProcessor.Read(fontData);
				_currentFontData = font;

				// Create a preview image showing all characters
				var charsPerRow = 16;
				var charSpacing = 2;
				var previewWidth = charsPerRow * (font.Width + charSpacing);
				var numRows = (int)Math.Ceiling((font.MaxChar - font.MinChar + 1) / (double)charsPerRow);
				var previewHeight = numRows * (font.Height + charSpacing);

				var previewImage = new Image<Rgba32>(previewWidth, previewHeight);
				previewImage.Mutate(x => x.BackgroundColor(new Rgba32(0, 0, 0, 255)));

				int charIndex = 0;
				for (int row = 0; row < numRows; row++)
				{
					for (int col = 0; col < charsPerRow && charIndex < font.Characters.Count; col++)
					{
						var charData = font.Characters[charIndex];
						var xOffset = col * (font.Width + charSpacing);
						var yOffset = row * (font.Height + charSpacing);

						for (int y = 0; y < charData.Image.Height && yOffset + y < previewHeight; y++)
						{
							for (int x = 0; x < charData.Image.Width && xOffset + x < previewWidth; x++)
							{
								previewImage[xOffset + x, yOffset + y] = charData.Image[x, y];
							}
						}

						charIndex++;
					}
				}

				var bitmapImage = ConvertImageSharpRgba32ToBitmapImage(previewImage);
				ContentImage.Source = bitmapImage;

				var isColor = (font.Flags & 1) != 0;
				var isProportional = (font.Flags & 2) != 0;
				var isKerned = (font.Flags & 4) != 0;
				FileInfoTextBlock.Text = $"FNT Font: {font.Width}x{font.Height}, Chars: {font.MinChar}-{font.MaxChar} ({font.Characters.Count} total)\n" +
					$"Color: {isColor}, Proportional: {isProportional}, Kerned: {isKerned}";

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
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error displaying font:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();
			}
		}

		private void DisplayPalette(byte[] paletteData)
		{
			try
			{
				ShowPaletteSelector(false);
				TextScrollViewer.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ImageContentGrid.Visibility = Visibility.Visible;

				var processor = new TwoFiveSixProcessor();
				var paletteFile = processor.Read(paletteData);

				// Create a 16x16 grid of colors (256 total)
				var colorsPerRow = 16;
				var colorSize = 24; // Each color swatch is 24x24 pixels
				var previewWidth = colorsPerRow * colorSize;
				var previewHeight = colorsPerRow * colorSize;

				var previewImage = new Image<Rgba32>(previewWidth, previewHeight);

				for (int i = 0; i < 256; i++)
				{
					var (red, green, blue) = paletteFile.Palette[i];
					var color = new Rgba32(red, green, blue, 255);

					var col = i % colorsPerRow;
					var row = i / colorsPerRow;
					var xStart = col * colorSize;
					var yStart = row * colorSize;

					for (int y = yStart; y < yStart + colorSize; y++)
					{
						for (int x = xStart; x < xStart + colorSize; x++)
						{
							previewImage[x, y] = color;
						}
					}
				}

				var bitmapImage = ConvertImageSharpRgba32ToBitmapImage(previewImage);
				ContentImage.Source = bitmapImage;

				FileInfoTextBlock.Text = $"256-Color Palette: {paletteData.Length} bytes (768 bytes palette + 8704 bytes fade table)";

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
			}
			catch (Exception ex)
			{
				TextScrollViewer.Visibility = Visibility.Visible;
				ImageContentGrid.Visibility = Visibility.Collapsed;
				AudioContentGrid.Visibility = Visibility.Collapsed;
				ContentTextBox.Text = $"Error displaying palette:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
				TextScrollViewer.ScrollToHome();
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
			if (_currentMidiFile != null && _currentMidiFileData != null)
			{
				if (_midiSynthesizer != null)
				{
					_midiSampleProvider = null;
					_midiSequencer = null;
					_midiSynthesizer = null;
				}

				string soundFontPath;
				if (string.IsNullOrEmpty(AppSettings.Instance.SoundFontPath))
				{
					var exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
					soundFontPath = Path.Combine(exeDirectory ?? "", "soundfonts", "Yamaha DB50XG Presets.sf2");
				}
				else
				{
					soundFontPath = AppSettings.Instance.SoundFontPath;
				}

				if (File.Exists(soundFontPath))
				{
					var sampleRate = 44100;
					var soundFont = new SoundFont(soundFontPath);
					_midiSynthesizer = new Synthesizer(soundFont, sampleRate);

					_midiSequencer = new MidiFileSequencer(_midiSynthesizer);
					_midiSampleProvider = new MidiSampleProvider(_midiSynthesizer, _midiSequencer, _currentMidiFile);

					if (_waveOut != null)
					{
						_waveOut.Stop();
						_waveOut.Dispose();
					}
					_waveOut = new WaveOutEvent();
					_waveOut.Init(_midiSampleProvider);
					_waveOut.PlaybackStopped += WaveOut_PlaybackStopped;

					_midiSampleProvider.Play(loop: true);
					_waveOut.Play();
					PlayButton.IsEnabled = false;
					PauseButton.IsEnabled = true;
					StopButton.IsEnabled = true;

					StartAudioPositionTimer();
				}
			}
			else if (_waveOut != null)
			{
				if (_midiSampleProvider != null && !_midiSampleProvider.IsPlaying)
				{
					_midiSampleProvider.Play(loop: true);
				}

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

				if (disposeResources)
				{
					_midiSampleProvider?.Stop();
					_midiSequencer = null;
					_midiSampleProvider = null;
					_midiSynthesizer = null;
					_currentMidiFile = null;
					_currentMidiFileData = null;
				}
				else if (_midiSampleProvider != null)
				{
					_midiSampleProvider.Stop();
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
			if (_isDraggingAudioSlider)
				return;

			if (_midiSampleProvider != null)
			{
				var position = _midiSampleProvider.CurrentTime;
				CurrentTimeTextBlock.Text = FormatTimeSpan(position);
				AudioPositionSlider.Value = position.TotalSeconds;

				// Check if MIDI playback has finished (for non-looping)
				if (!_midiSampleProvider.IsPlaying && _waveOut?.PlaybackState == PlaybackState.Playing)
				{
					StopAudio();
				}
			}
			else if (_waveStream != null)
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
			if (!_isDraggingAudioSlider)
				return;

			_isDraggingAudioSlider = false;
			var newPosition = TimeSpan.FromSeconds(AudioPositionSlider.Value);
			CurrentTimeTextBlock.Text = FormatTimeSpan(newPosition);

			if (_midiSampleProvider != null)
			{
				_midiSampleProvider.Seek(newPosition);
			}
			else if (_waveStream != null)
			{
				_waveStream.CurrentTime = newPosition;
			}
		}

		private void AudioPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			if (_isDraggingAudioSlider && (_waveStream != null || _midiSampleProvider != null))
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




		protected override void OnClosed(EventArgs e)
		{
			StopAudio(disposeResources: true);

			base.OnClosed(e);
		}
	}
}

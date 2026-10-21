using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DescentView
{
    public partial class OptionsWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private ThemeOption _selectedTheme;
        private DefaultViewOption _selectedDefaultView;
        private string _selectedFontFamily;
        private double _selectedFontSize;
        private string _selectedSoundFontPath;
        private Dictionary<string, (TextBlock Status, Button Register, Button Unregister)> _associationRows = new();

        public OptionsWindow()
        {
            InitializeComponent();

            // Set dark title bar if needed
            SourceInitialized += (s, e) =>
            {
                UpdateTitleBarTheme();
            };

            // Load current settings
            _selectedTheme = AppSettings.Instance.Theme;
            ThemeComboBox.SelectedIndex = (int)_selectedTheme;

            _selectedDefaultView = AppSettings.Instance.DefaultView;
            DefaultViewComboBox.SelectedIndex = (int)_selectedDefaultView;

            _selectedFontFamily = AppSettings.Instance.FontFamily;
            PopulateFontList();

            _selectedFontSize = AppSettings.Instance.FontSize;
            SelectFontSize(_selectedFontSize);

            _selectedSoundFontPath = AppSettings.Instance.SoundFontPath;
            UpdateSoundFontDisplay();

            _associationRows = new Dictionary<string, (TextBlock Status, Button Register, Button Unregister)>(StringComparer.OrdinalIgnoreCase)
            {
                [".pig"] = (PigStatusText, PigRegisterButton, PigUnregisterButton),
                [".hog"] = (HogStatusText, HogRegisterButton, HogUnregisterButton),
            };
            RefreshAssociationRows();
        }

        private void RefreshAssociationRows()
        {
            foreach (var ext in _associationRows.Keys)
            {
                RefreshAssociationRow(ext);
            }
        }

        private void RefreshAssociationRow(string ext)
        {
            if (!_associationRows.TryGetValue(ext, out var row))
            {
                return;
            }

            bool registered;
            try
            {
                registered = FileAssociations.IsRegistered(ext);
            }
            catch (Exception)
            {
                registered = false;
            }

            row.Status.Text = registered ? "Registered" : "Not registered";
            row.Register.IsEnabled = !registered;
            row.Unregister.IsEnabled = registered;
        }

        private void RegisterAssociation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string ext })
            {
                return;
            }

            try
            {
                FileAssociations.Register(ext);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not register {ext}:\n{ex.Message}",
                               "File association",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }

            RefreshAssociationRow(ext);
        }

        private void UnregisterAssociation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string ext })
            {
                return;
            }

            try
            {
                FileAssociations.Unregister(ext);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not unregister {ext}:\n{ex.Message}",
                               "File association",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }

            RefreshAssociationRow(ext);
        }

        private void PopulateFontList()
        {
            var preferredFonts = new[] { "Consolas", "Cascadia Code", "Cascadia Mono", "Courier New", "Lucida Console" };
            
            var allFonts = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .OrderBy(f => f)
                .ToList();

            foreach (var font in preferredFonts)
            {
                if (allFonts.Contains(font))
                {
                    var item = new ComboBoxItem { Content = font };
                    FontComboBox.Items.Add(item);
                    if (font == _selectedFontFamily)
                    {
                        FontComboBox.SelectedItem = item;
                    }
                }
            }

            FontComboBox.Items.Add(new Separator());

            foreach (var font in allFonts)
            {
                if (!preferredFonts.Contains(font))
                {
                    var item = new ComboBoxItem { Content = font };
                    FontComboBox.Items.Add(item);
                    if (font == _selectedFontFamily)
                    {
                        FontComboBox.SelectedItem = item;
                    }
                }
            }

            if (FontComboBox.SelectedItem == null && FontComboBox.Items.Count > 0)
            {
                FontComboBox.SelectedIndex = 0;
            }
        }

        private void SelectFontSize(double size)
        {
            foreach (ComboBoxItem item in FontSizeComboBox.Items)
            {
                if (item.Content?.ToString() == size.ToString())
                {
                    FontSizeComboBox.SelectedItem = item;
                    return;
                }
            }
            FontSizeComboBox.SelectedIndex = 4;
        }

        private void UpdateTitleBarTheme()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int value = AppSettings.Instance.ShouldUseDarkMode() ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }

        private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedTheme = (ThemeOption)ThemeComboBox.SelectedIndex;
        }

        private void DefaultViewComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedDefaultView = (DefaultViewOption)DefaultViewComboBox.SelectedIndex;
        }

        private void FontComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FontComboBox.SelectedItem is ComboBoxItem item && item.Content is string fontName)
            {
                _selectedFontFamily = fontName;
            }
        }

        private void FontSizeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FontSizeComboBox.SelectedItem is ComboBoxItem item && 
                item.Content is string sizeStr && 
                double.TryParse(sizeStr, out double size))
            {
                _selectedFontSize = size;
            }
        }

        private void UpdateSoundFontDisplay()
        {
            if (string.IsNullOrEmpty(_selectedSoundFontPath))
            {
                var exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var defaultPath = Path.Combine(exeDirectory ?? "", "soundfonts", "Yamaha DB50XG Presets.sf2");
                if (File.Exists(defaultPath))
                {
                    SoundFontTextBox.Text = "Yamaha DB50XG Presets.sf2 (Default)";
                }
                else
                {
                    SoundFontTextBox.Text = "(Default - not found)";
                }
            }
            else
            {
                SoundFontTextBox.Text = Path.GetFileName(_selectedSoundFontPath);
            }
        }

        private void SoundFontBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "SoundFont Files (*.sf2)|*.sf2|All Files (*.*)|*.*",
                Title = "Select SoundFont File"
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedSoundFontPath = dialog.FileName;
                UpdateSoundFontDisplay();
            }
        }

        private void SoundFontTextBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _selectedSoundFontPath = string.Empty;
            UpdateSoundFontDisplay();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.Theme = _selectedTheme;
            AppSettings.Instance.DefaultView = _selectedDefaultView;
            AppSettings.Instance.FontFamily = _selectedFontFamily;
            AppSettings.Instance.FontSize = _selectedFontSize;
            AppSettings.Instance.SoundFontPath = _selectedSoundFontPath;
            AppSettings.Instance.Save();

            // Apply theme
            ThemeManager.ApplyTheme(_selectedTheme);

            // Notify that font settings changed
            FontSettingsChanged?.Invoke();

            // Notify that terrain HPI path changed
            TerrainHpiPathChanged?.Invoke();

            DialogResult = true;
            Close();
        }

        public static event Action? FontSettingsChanged;
        public static event Action? TerrainHpiPathChanged;

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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
        private bool _enableTntCaching;
        private bool _autoFitTnt;
        private List<string> _terrainHpiPaths;
        private double _model3DDefaultRotationX;
        private double _model3DDefaultRotationY;

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

            _enableTntCaching = AppSettings.Instance.EnableTntCaching;
            TntCachingCheckBox.IsChecked = _enableTntCaching;

            _autoFitTnt = AppSettings.Instance.AutoFitTnt;
            AutoFitTntCheckBox.IsChecked = _autoFitTnt;

            _terrainHpiPaths = new List<string>(AppSettings.Instance.TerrainHpiPaths);
            foreach (var path in _terrainHpiPaths)
            {
                TerrainHpiListBox.Items.Add(path);
            }

            _model3DDefaultRotationX = AppSettings.Instance.Model3DDefaultRotationX;
            Model3DRotationXSlider.Value = _model3DDefaultRotationX;
            Model3DRotationXValueTextBlock.Text = $"{_model3DDefaultRotationX:F0}°";

            _model3DDefaultRotationY = AppSettings.Instance.Model3DDefaultRotationY;
            Model3DRotationYSlider.Value = _model3DDefaultRotationY;
            Model3DRotationYValueTextBlock.Text = $"{_model3DDefaultRotationY:F0}°";
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

        private void TntCachingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _enableTntCaching = TntCachingCheckBox.IsChecked == true;
        }

        private void AutoFitTntCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _autoFitTnt = AutoFitTntCheckBox.IsChecked == true;
        }

        private void Model3DRotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender == Model3DRotationXSlider)
            {
                _model3DDefaultRotationX = e.NewValue;
                if (Model3DRotationXValueTextBlock != null)
                {
                    Model3DRotationXValueTextBlock.Text = $"{_model3DDefaultRotationX:F0}°";
                }
            }
            else if (sender == Model3DRotationYSlider)
            {
                _model3DDefaultRotationY = e.NewValue;
                if (Model3DRotationYValueTextBlock != null)
                {
                    Model3DRotationYValueTextBlock.Text = $"{_model3DDefaultRotationY:F0}°";
                }
            }
        }

        private void AddTerrainHpiButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "HPI Files (*.hpi)|*.hpi|All Files (*.*)|*.*",
                Title = "Select Kingdoms Terrain HPI File"
            };

            // Set initial directory based on last added file
            if (_terrainHpiPaths.Count > 0)
            {
                var lastPath = _terrainHpiPaths[_terrainHpiPaths.Count - 1];
                var directory = System.IO.Path.GetDirectoryName(lastPath);
                if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                }
            }

            if (dialog.ShowDialog() == true)
            {
                // Don't add duplicates
                if (!_terrainHpiPaths.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    _terrainHpiPaths.Add(dialog.FileName);
                    TerrainHpiListBox.Items.Add(dialog.FileName);
                }
            }
        }

        private void RemoveTerrainHpiButton_Click(object sender, RoutedEventArgs e)
        {
            if (TerrainHpiListBox.SelectedItem is string selectedPath)
            {
                _terrainHpiPaths.Remove(selectedPath);
                TerrainHpiListBox.Items.Remove(selectedPath);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.Theme = _selectedTheme;
            AppSettings.Instance.DefaultView = _selectedDefaultView;
            AppSettings.Instance.FontFamily = _selectedFontFamily;
            AppSettings.Instance.FontSize = _selectedFontSize;
            AppSettings.Instance.EnableTntCaching = _enableTntCaching;
            AppSettings.Instance.AutoFitTnt = _autoFitTnt;
            AppSettings.Instance.TerrainHpiPaths = new List<string>(_terrainHpiPaths);
            AppSettings.Instance.Model3DDefaultRotationX = _model3DDefaultRotationX;
            AppSettings.Instance.Model3DDefaultRotationY = _model3DDefaultRotationY;
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


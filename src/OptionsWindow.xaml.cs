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

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.Theme = _selectedTheme;
            AppSettings.Instance.DefaultView = _selectedDefaultView;
            AppSettings.Instance.FontFamily = _selectedFontFamily;
            AppSettings.Instance.FontSize = _selectedFontSize;
            AppSettings.Instance.EnableTntCaching = _enableTntCaching;
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


using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DescentView
{
    public static class ThemeManager
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private static readonly Uri DarkThemeUri = new("pack://application:,,,/MahApps.Metro;component/Styles/Themes/Dark.Steel.xaml");
        private static readonly Uri LightThemeUri = new("pack://application:,,,/MahApps.Metro;component/Styles/Themes/Light.Steel.xaml");

        public static void ApplyTheme(ThemeOption theme)
        {
            bool useDark = theme switch
            {
                ThemeOption.Dark => true,
                ThemeOption.Light => false,
                ThemeOption.System => AppSettings.IsSystemDarkMode(),
                _ => false
            };

            ApplyTheme(useDark);
        }

        public static void ApplyTheme(bool useDark)
        {
            var app = Application.Current;
            if (app?.Resources?.MergedDictionaries == null)
                return;

            // Find and replace the theme dictionary
            ResourceDictionary? existingTheme = null;
            foreach (var dict in app.Resources.MergedDictionaries)
            {
                if (dict.Source != null && dict.Source.ToString().Contains("/Themes/"))
                {
                    existingTheme = dict;
                    break;
                }
            }

            var newThemeUri = useDark ? DarkThemeUri : LightThemeUri;
            var newTheme = new ResourceDictionary { Source = newThemeUri };

            if (existingTheme != null)
            {
                var index = app.Resources.MergedDictionaries.IndexOf(existingTheme);
                app.Resources.MergedDictionaries[index] = newTheme;
            }
            else
            {
                app.Resources.MergedDictionaries.Add(newTheme);
            }

            // Update title bars for all windows
            foreach (Window window in app.Windows)
            {
                UpdateWindowTitleBar(window, useDark);
            }
        }

        public static void UpdateWindowTitleBar(Window window, bool useDark)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int value = useDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }

        public static void InitializeWindow(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                bool useDark = AppSettings.Instance.ShouldUseDarkMode();
                UpdateWindowTitleBar(window, useDark);
            };
        }
    }
}


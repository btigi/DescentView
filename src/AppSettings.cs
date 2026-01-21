using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DescentView
{
    public enum ThemeOption
    {
        Light,
        Dark,
        System
    }

    public enum DefaultViewOption
    {
        Preview,
        Hex
    }

    public class AppSettings
    {
        public ThemeOption Theme { get; set; } = ThemeOption.System;
        public DefaultViewOption DefaultView { get; set; } = DefaultViewOption.Preview;
        public string FontFamily { get; set; } = "Consolas";
        public double FontSize { get; set; } = 12;
        public bool EnableTntCaching { get; set; } = true;

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DescentView");

        private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

        private static AppSettings? _instance;
        public static AppSettings Instance => _instance ??= Load();

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _instance = settings;
                        return settings;
                    }
                }
            }
            catch
            {
                // If loading fails, return default settings
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Silently fail if we can't save settings
            }
        }

        public static bool IsSystemDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return value is int i && i == 0;
            }
            catch
            {
                return false;
            }
        }

        public bool ShouldUseDarkMode()
        {
            return Theme switch
            {
                ThemeOption.Dark => true,
                ThemeOption.Light => false,
                ThemeOption.System => IsSystemDarkMode(),
                _ => false
            };
        }
    }
}


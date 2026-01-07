using System;
using System.Windows;

namespace DescentView
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Load settings and apply theme
            var settings = AppSettings.Instance;
            ThemeManager.ApplyTheme(settings.Theme);

            // Check for command-line arguments
            string? filePath = null;
            if (e.Args.Length > 0)
            {
                filePath = e.Args[0];
            }

            // Create and show main window
            var mainWindow = new MainWindow(filePath);
            mainWindow.Show();
        }
    }
}

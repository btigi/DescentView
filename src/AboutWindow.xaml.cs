using System.Reflection;
using System.Windows;

namespace DescentView
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();

            // Set up title bar theming
            ThemeManager.InitializeWindow(this);

            SetVersionText();
        }

        private void SetVersionText()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            
            if (version != null)
            {
                VersionTextBlock.Text = $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            else
            {
                VersionTextBlock.Text = "Version";
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

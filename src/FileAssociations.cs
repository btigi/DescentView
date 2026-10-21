using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DescentView
{
    public static class FileAssociations
    {
        public static readonly (string Ext, string Description)[] Types =
        {
            (".pig", "PIG archive"),
            (".hog", "HOG archive"),
        };

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static bool IsRegistered(string ext)
        {
            var progId = ProgIdFor(ext);
            using var extKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\." + NormalizeExt(ext));
            if (!string.Equals(extKey?.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var commandKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            return !string.IsNullOrEmpty(command) && CommandMatchesCurrentExe(command);
        }

        public static void Register(string ext)
        {
            var bare = NormalizeExt(ext);
            var progId = ProgIdFor(ext);
            var exe = ExePath;
            var description = DescriptionFor(bare);

            using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                progIdKey.SetValue(null, description);

                using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue(null, $"\"{exe}\",0");

                using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
                commandKey.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\." + bare))
            {
                extKey.SetValue(null, progId);

                using var openWith = extKey.CreateSubKey("OpenWithProgids");
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            NotifyAssociationsChanged();
        }

        public static void Unregister(string ext)
        {
            var bare = NormalizeExt(ext);
            var progId = ProgIdFor(ext);

            using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
            {
                classes?.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);

                using var extKey = classes?.OpenSubKey("." + bare, writable: true);
                if (extKey != null)
                {
                    if (string.Equals(extKey.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
                    {
                        extKey.SetValue(null, string.Empty);
                    }

                    using var openWith = extKey.OpenSubKey("OpenWithProgids", writable: true);
                    openWith?.DeleteValue(progId, throwOnMissingValue: false);
                }
            }

            NotifyAssociationsChanged();
        }

        private static string NormalizeExt(string ext)
        {
            var bare = ext.Trim().TrimStart('.').ToLowerInvariant();
            if (bare.Length == 0)
            {
                throw new ArgumentException("Extension is required.", nameof(ext));
            }

            return bare;
        }

        private static string ProgIdFor(string ext) => "DescentView." + NormalizeExt(ext);

        private static string DescriptionFor(string bareExt)
        {
            foreach (var (ext, description) in Types)
            {
                if (string.Equals(NormalizeExt(ext), bareExt, StringComparison.OrdinalIgnoreCase))
                {
                    return description;
                }
            }

            return bareExt.ToUpperInvariant() + " archive";
        }

        private static string ExePath =>
            Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path could not be determined.");

        private static bool CommandMatchesCurrentExe(string command)
        {
            var exe = ExePath;
            if (command.Length >= 2 && command[0] == '"')
            {
                var end = command.IndexOf('"', 1);
                if (end > 1)
                {
                    var path = command.Substring(1, end - 1);
                    return string.Equals(path, exe, StringComparison.OrdinalIgnoreCase);
                }
            }

            return command.StartsWith(exe, StringComparison.OrdinalIgnoreCase);
        }

        private static void NotifyAssociationsChanged()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace GameLogReader
{
    static class Program
    {
        private const string ConfigFilePath = "config.txt";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MessageBox.Show("Please select the Star Citizen Launcher shortcut.", "Launcher Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            string launcherShortcutPath = GetPathFromUser("Locate the Star Citizen Launcher Shortcut", "Shortcut Files (*.*)|*.*");

            if (launcherShortcutPath == null)
            {
                MessageBox.Show("You must select the launcher shortcut. The application will now exit.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string launcherPath = ResolveShortcut(launcherShortcutPath);
            if (launcherPath == null)
            {
                MessageBox.Show("Failed to resolve the launcher shortcut. The application will now exit.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show("Please select the Game.log file. This will be located in your StarCitizen folder in your Star Citizen Install.", "Log File Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            string logFilePath = GetPathFromUser("Locate the Game.log File", "Log Files (*.log)|*.log");

            if (logFilePath == null)
            {
                MessageBox.Show("You must select the Game.log file. The application will now exit.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SaveConfiguration(launcherPath, logFilePath);
            SetupLauncherIntegration(launcherPath);

            Application.Run(new OverlayWindow(logFilePath));
        }

        private static string GetPathFromUser(string promptTitle, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = promptTitle;
                dialog.Filter = filter;
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }
            return null;
        }

        private static string ResolveShortcut(string shortcutPath)
        {
            var shell = new Shell32.Shell();
            var folder = shell.NameSpace(Path.GetDirectoryName(shortcutPath));
            var folderItem = folder.ParseName(Path.GetFileName(shortcutPath));

            if (folderItem != null)
            {
                var link = (Shell32.ShellLinkObject)folderItem.GetLink;
                return link.Path;
            }
            return null;
        }

        private static void SaveConfiguration(string launcherPath, string logFilePath)
        {
            File.WriteAllLines(ConfigFilePath, new[] { launcherPath, logFilePath });
        }

        private static void SetupLauncherIntegration(string launcherPath)
        {
            string overlayPath = Application.ExecutablePath;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AppKeyAssignments"))
                {
                    if (key != null)
                    {
                        string valueName = launcherPath.Replace(@"\", "_");
                        key.SetValue(valueName, overlayPath);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to integrate with the launcher.\nError: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class OverlayWindow : Form
    {
        private string logFilePath;
        private const string OutputLogFilePath = "FilteredLog.txt";
        private long lastReadPosition = 0;

        private System.Windows.Forms.Timer fileCheckTimer;
        private System.Windows.Forms.Timer displayTimer;

        private Label displayLabel;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        public OverlayWindow(string logFilePath)
        {
            this.logFilePath = logFilePath;

            // Window setup
            this.Text = "Game Log Overlay";
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = true;
            this.Size = new Size(600, 50);
            this.BackColor = Color.Black;
            this.Opacity = 0.30;
            this.ShowInTaskbar = false; // Hide the taskbar icon
            this.SetOverlayPosition();

            // Label setup
            displayLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Calibri", 12, FontStyle.Bold),
                Text = "Waiting for log data..."
            };
            this.Controls.Add(displayLabel);

            // Timers setup
            fileCheckTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            fileCheckTimer.Tick += CheckLogFile;
            fileCheckTimer.Start();

            displayTimer = new System.Windows.Forms.Timer { Interval = 10000 }; // 10 seconds
            displayTimer.Tick += (sender, e) => { this.Hide(); };

            // Tray menu setup
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Exit", null, (sender, e) => { this.Close(); });

            // Tray icon setup
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information, // Use a standard icon
                ContextMenuStrip = trayMenu,
                Text = "Game Log Overlay",
                Visible = true
            };

            trayIcon.DoubleClick += (sender, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
        }

        private void SetOverlayPosition()
        {
            var screenBounds = Screen.PrimaryScreen.Bounds;
            int x = (screenBounds.Width - this.Width) / 2;
            int y = (screenBounds.Height - this.Height) / 25; // Top of the screen
            this.Location = new Point(x, y);
        }

        private void CheckLogFile(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(logFilePath))
                {
                    displayLabel.Text = "Game.log not found.";
                    return;
                }

                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fileStream.Seek(lastReadPosition, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fileStream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Contains("<Actor Death>"))
                            {
                                ParseAndLog(line);
                            }
                        }

                        lastReadPosition = fileStream.Position;
                    }
                }
            }
            catch (Exception ex)
            {
                displayLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void ParseAndLog(string line)
        {
            var match = Regex.Match(line, @"<Actor Death> CActor::Kill: '(.+?)'.*?killed by '(.+?)'");
            if (match.Success)
            {
                string victim = match.Groups[1].Value;
                string killer = match.Groups[2].Value;
                string result = $"{killer} killed {victim}";

                displayLabel.Text = result;

                File.AppendAllText(OutputLogFilePath, $"{DateTime.Now}: {result}{Environment.NewLine}");

                this.Show();
                displayTimer.Start();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            trayIcon.Visible = false;
            fileCheckTimer?.Stop();
            fileCheckTimer?.Dispose();
            displayTimer?.Stop();
            displayTimer?.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VexBloxLauncher
{
    internal static class Program
    {
        private const string ProtocolName = "vexblox";
        private const string LegacyProtocolName = "bbclient";
        private const string DefaultBaseUrl = "http://vexblx.fun";

        private static readonly Dictionary<string, string> Clients = new Dictionary<string, string>
        {
            { "2015", "Roblox.exe" },
            { "2020", "RobloxPlayerBeta.exe" },
        };

        private const string PatchedKeyFile = "vexblox-key.pem";

        private static readonly string InstallRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VexBlox");

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private static string BaseUrl = DefaultBaseUrl;

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            LoadSettings();

            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall();
                return;
            }

            var uri = args.FirstOrDefault(a => a.StartsWith(ProtocolName + ":", StringComparison.OrdinalIgnoreCase));
            using (var form = new ProgressForm())
            {
                form.Shown += async (s, e) =>
                {
                    try
                    {
                        if (await UpdateSelf(args, form))
                        {
                            form.Close();
                            return;
                        }
                        await Run(uri, form);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "VexBlox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    form.Close();
                };
                Application.Run(form);
            }
        }

        private static void LoadSettings()
        {
            var ini = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VexBloxLauncher.ini");
            if (!File.Exists(ini))
                return;
            foreach (var line in File.ReadAllLines(ini))
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length == 2 && parts[0].Trim().Equals("BaseUrl", StringComparison.OrdinalIgnoreCase))
                    BaseUrl = parts[1].Trim().TrimEnd('/');
            }
        }

        private static async Task<bool> UpdateSelf(string[] args, ProgressForm form)
        {
            var current = Process.GetCurrentProcess().MainModule.FileName;
            try { File.Delete(current + ".old"); } catch (Exception) { }

            form.SetStatus("Checking for updates...", 0);
            string remote;
            try
            {
                remote = (await Http.GetStringAsync($"{BaseUrl}/downloads/VexBloxLauncher.sha256.txt?t={DateTime.UtcNow.Ticks}")).Trim().ToLowerInvariant();
            }
            catch (Exception)
            {
                return false;
            }
            if (remote.Length != 64 || Sha256(current) == remote)
                return false;

            var downloaded = current + ".new";
            await Download($"{BaseUrl}/downloads/VexBloxLauncher.exe?sha={remote}", downloaded, form);
            if (Sha256(downloaded) != remote)
            {
                File.Delete(downloaded);
                return false;
            }

            File.Move(current, current + ".old");
            File.Move(downloaded, current);
            Process.Start(new ProcessStartInfo
            {
                FileName = current,
                Arguments = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")),
                UseShellExecute = false,
            });
            return true;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static async Task Run(string uri, ProgressForm form)
        {
            var year = "2015";
            Dictionary<string, string> query = null;
            if (uri != null)
            {
                query = ParseJoinUri(uri);
                if (query.TryGetValue("year", out var requested) && !string.IsNullOrEmpty(requested))
                    year = requested;
                if (!Clients.ContainsKey(year))
                    throw new Exception($"This game uses the {year} client, which is not available yet. Only these are installed: {string.Join(", ", Clients.Keys)}.");
            }

            await EnsureClient(year, form);
            foreach (var other in Clients.Keys.Where(y => y != year))
            {
                try
                {
                    await EnsureClient(other, form);
                }
                catch (Exception) when (query != null)
                {
                }
            }
            var launcherPath = InstallSelf();
            RegisterProtocol(launcherPath);
            RegisterUninstall(launcherPath);

            if (query == null)
            {
                form.SetStatus("VexBlox is installed!", 100);
                MessageBox.Show(form, "VexBlox is installed. Press Play on a game on the website to join.", "VexBlox",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            form.SetStatus("Starting VexBlox...", 100);
            var client = LaunchClient(year, query);
            var keyFile = Path.Combine(ClientDirectory(year), PatchedKeyFile);
            if (File.Exists(keyFile))
            {
                form.Hide();
                var domain = new Uri(BaseUrl).Host;
                if (domain.StartsWith("www."))
                    domain = domain.Substring(4);
                if (domain.Length != "roblox.com".Length)
                    throw new Exception($"The domain {domain} must be 10 characters long to patch the {year} client.");
                var key = File.ReadAllText(keyFile);
                await Task.Run(() => MemoryPatcher.Run(client, domain, key, TimeSpan.FromSeconds(45)));
                return;
            }
            await Task.Delay(1500);
        }

        private static Dictionary<string, string> ParseJoinUri(string uri)
        {
            var parsed = new Uri(uri);
            if (!parsed.Host.Equals("join", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unknown launch request: " + uri);

            var values = HttpUtility.ParseQueryString(parsed.Query);
            var result = values.AllKeys.Where(k => k != null).ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
            if (!result.ContainsKey("place") || !long.TryParse(result["place"], out _))
                throw new Exception("The launch link has no valid place id.");
            if (!result.ContainsKey("ticket") || string.IsNullOrEmpty(result["ticket"]))
                throw new Exception("The launch link has no ticket. Are you logged in?");
            return result;
        }

        private static string ClientDirectory(string year) => Path.Combine(InstallRoot, "Versions", year);

        private static async Task EnsureClient(string year, ProgressForm form)
        {
            var dir = ClientDirectory(year);
            var marker = Path.Combine(dir, ".version");
            var exe = Path.Combine(dir, Clients[year]);
            var installed = File.Exists(exe) && File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;

            form.SetStatus("Checking for updates...", 0);
            string remote;
            try
            {
                remote = (await Http.GetStringAsync($"{BaseUrl}/downloads/client-{year}.sha256.txt?t={DateTime.UtcNow.Ticks}")).Trim().ToLowerInvariant();
            }
            catch (Exception) when (installed != null)
            {
                return;
            }

            if (installed == remote)
                return;

            OfferDefenderException(form);

            var zip = Path.Combine(Path.GetTempPath(), $"vexblox-client-{year}-{Guid.NewGuid():N}.zip");
            try
            {
                await Download($"{BaseUrl}/downloads/client-{year}.zip?sha={remote}", zip, form);

                form.SetStatus("Verifying...", 100);
                if (Sha256(zip) != remote)
                    throw new Exception("The downloaded client is corrupted, please try again.");

                form.SetStatus("Installing...", 100);
                var staging = dir + ".new";
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging));
                File.WriteAllText(Path.Combine(staging, ".version"), remote);

                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                Directory.CreateDirectory(Path.GetDirectoryName(dir));
                Directory.Move(staging, dir);
            }
            finally
            {
                try { File.Delete(zip); } catch (IOException) { }
            }
        }

        private const string SettingsKey = @"Software\VexBlox";

        private static void OfferDefenderException(ProgressForm form)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (key?.GetValue("DefenderException") as string == InstallRoot)
                    return;
            }

            using (var prompt = new DefenderPrompt(BaseUrl + "/defender/"))
            {
                if (prompt.ShowDialog(form) != DialogResult.Yes)
                    return;
            }

            Directory.CreateDirectory(InstallRoot);
            var command = $"Add-MpPreference -ExclusionPath '{InstallRoot.Replace("'", "''")}'";
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new Exception($"PowerShell exited with code {process.ExitCode}.");
                }

                using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    key.SetValue("DefenderException", InstallRoot);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(form, "The exception was not added because administrator permission was denied. VexBlox will try to install anyway.",
                    "VexBlox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, "The exception could not be added: " + ex.Message + "\n\nVexBlox will try to install anyway.",
                    "VexBlox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static async Task Download(string url, string path, ProgressForm form)
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        done += read;
                        var percent = total > 0 ? (int)(done * 100 / total) : 0;
                        form.SetStatus($"Downloading VexBlox... {done / (1024 * 1024)} / {total / (1024 * 1024)} MB", percent);
                    }
                }
            }
        }

        private static string InstallSelf()
        {
            var current = Process.GetCurrentProcess().MainModule.FileName;
            var target = Path.Combine(InstallRoot, "VexBloxLauncher.exe");
            Directory.CreateDirectory(InstallRoot);
            if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(current, target, true);
                var ini = Path.Combine(Path.GetDirectoryName(current), "VexBloxLauncher.ini");
                if (File.Exists(ini))
                    File.Copy(ini, Path.Combine(InstallRoot, "VexBloxLauncher.ini"), true);
            }
            return target;
        }

        private static void RegisterProtocol(string launcherPath)
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}"))
            {
                key.SetValue("", "URL:VexBlox Protocol");
                key.SetValue("URL Protocol", "");
                using (var icon = key.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{launcherPath}\",0");
                using (var command = key.CreateSubKey(@"shell\open\command"))
                    command.SetValue("", $"\"{launcherPath}\" \"%1\"");
            }
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyProtocolName}", false);
        }

        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VexBlox";

        private static void RegisterUninstall(string launcherPath)
        {
            long bytes = 0;
            try
            {
                bytes = new DirectoryInfo(InstallRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (IOException)
            {
            }

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "VexBlox");
                key.SetValue("DisplayIcon", $"\"{launcherPath}\",0");
                key.SetValue("Publisher", "VexBlox");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("InstallLocation", InstallRoot);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("URLInfoAbout", BaseUrl);
                key.SetValue("UninstallString", $"\"{launcherPath}\" --uninstall");
                key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void Uninstall()
        {
            if (MessageBox.Show("Do you want to uninstall VexBlox?", "VexBlox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var self = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != self.Id && process.MainModule.FileName.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception)
                {
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProtocolName}", false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyProtocolName}", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            string exclusion = null;
            using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                exclusion = key?.GetValue("DefenderException") as string;
            if (!string.IsNullOrEmpty(exclusion))
            {
                try
                {
                    using (var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Remove-MpPreference -ExclusionPath '{exclusion.Replace("'", "''")}'\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    }))
                    {
                        process.WaitForExit();
                    }
                }
                catch (Exception)
                {
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false);

            try
            {
                Directory.Delete(Path.Combine(InstallRoot, "Versions"), true);
            }
            catch (Exception)
            {
            }

            MessageBox.Show("VexBlox was uninstalled.", "VexBlox", MessageBoxButtons.OK, MessageBoxIcon.Information);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c for /l %i in (1,1,15) do (ping 127.0.0.1 -n 2 >nul & rmdir /s /q \"{InstallRoot}\" 2>nul & if not exist \"{InstallRoot}\" exit)",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }

        private static Process LaunchClient(string year, Dictionary<string, string> query)
        {
            var place = query["place"];
            var ticket = query["ticket"];
            var dir = ClientDirectory(year);

            var auth = $"{BaseUrl}/Login/Negotiate.ashx";
            var placeLauncher = $"{BaseUrl}/game/PlaceLauncher.ashx?placeid={place}&ticket={Uri.EscapeDataString(ticket)}&{year}=true";
            var arguments = $"--play -a \"{auth}\" -j \"{placeLauncher}\" -t \"{ticket}\"";

            return Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(dir, Clients[year]),
                Arguments = arguments,
                WorkingDirectory = dir,
                UseShellExecute = false,
            });
        }
    }

    internal sealed class DefenderPrompt : Form
    {
        public DefenderPrompt(string readWhyUrl)
        {
            Text = "VexBlox";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 110);

            var label = new Label
            {
                Text = "Add a Windows Defender exclusion for VexBlox?",
                AutoSize = false,
                Location = new Point(16, 16),
                Size = new Size(328, 40),
            };

            var why = new Button { Text = "Read why", Location = new Point(16, 68), Size = new Size(90, 28) };
            why.Click += (s, e) => Process.Start(new ProcessStartInfo { FileName = readWhyUrl, UseShellExecute = true });

            var yes = new Button { Text = "Yes", DialogResult = DialogResult.Yes, Location = new Point(176, 68), Size = new Size(80, 28) };
            var no = new Button { Text = "No", DialogResult = DialogResult.No, Location = new Point(264, 68), Size = new Size(80, 28) };

            Controls.Add(label);
            Controls.Add(why);
            Controls.Add(yes);
            Controls.Add(no);
            AcceptButton = yes;
            CancelButton = no;
        }
    }

    internal sealed class ProgressForm : Form
    {
        private readonly Label _status;
        private readonly ProgressBar _bar;

        public ProgressForm()
        {
            Text = "VexBlox";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 96);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            _status = new Label { Left = 16, Top = 16, Width = 388, Height = 20, Text = "Starting..." };
            _bar = new ProgressBar { Left = 16, Top = 48, Width = 388, Height = 22, Minimum = 0, Maximum = 100 };
            Controls.Add(_status);
            Controls.Add(_bar);
        }

        public void SetStatus(string text, int percent)
        {
            _status.Text = text;
            _bar.Value = Math.Max(0, Math.Min(100, percent));
        }
    }
}

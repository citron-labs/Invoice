using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace RevitFlexConduitInstaller;

internal static class Program301
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Installer301Form());
    }
}

internal sealed class Installer301Form : Form
{
    private const string ProductName = "Revit Flex Conduit 2025";
    private const string ProductVersion = "3.0.1";
    private const string DllFileName = "RevitFlexConduit.dll";
    private const string ManifestFileName = "RevitFlexConduit2025-v3.0.1.addin";

    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();

    private static readonly string ProductRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citron", "RevitFlexConduit2025");

    private static readonly string InstallDir = Path.Combine(ProductRoot, "v" + ProductVersion);

    private static readonly string AddinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", "2025");

    private static readonly string SystemAddinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Autodesk", "Revit", "Addins", "2025");

    internal Installer301Form()
    {
        Text = $"{ProductName} v{ProductVersion} Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 448);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);

        var header = new Panel { Dock = DockStyle.Top, Height = 98, BackColor = Color.FromArgb(35, 48, 74) };
        header.Controls.Add(new Label
        {
            Text = "Flex Conduit for Revit 2025",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 20F),
            AutoSize = true,
            Location = new Point(30, 17)
        });
        header.Controls.Add(new Label
        {
            Text = $"Version {ProductVersion} • connector-aware spline routing • persistent 3D control points",
            ForeColor = Color.FromArgb(215, 224, 239),
            AutoSize = true,
            Location = new Point(33, 59)
        });
        Controls.Add(header);

        var body = new Panel
        {
            Location = new Point(30, 124),
            Size = new Size(640, 216),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        body.Controls.Add(new Label
        {
            Text = $"Installation — v{ProductVersion}",
            Font = new Font("Segoe UI Semibold", 12F),
            AutoSize = true,
            Location = new Point(18, 15)
        });
        body.Controls.Add(new Label
        {
            Text = "Installs for the current Windows user only.\r\n\r\n" +
                   "• Fixes the Revit 2025 endpoint-dialog 'Corresponding button not found' error\r\n" +
                   "• Replaces the old icon with a Revit-style flexible metallic conduit icon\r\n" +
                   "• Removes older per-user Flex Conduit manifests and DLLs before installing\r\n" +
                   "• Keeps connector-aware spline routing and persistent XYZ control points\r\n" +
                   "• Attempts Systems-tab placement immediately after Conduit Fitting\r\n" +
                   "• Does not require administrator permissions\r\n" +
                   "• Close Revit 2025 before installing or uninstalling",
            AutoSize = true,
            Location = new Point(18, 48)
        });
        Controls.Add(body);

        _status.Text = GetInitialStatus();
        _status.AutoSize = true;
        _status.Location = new Point(33, 358);
        Controls.Add(_status);

        _progress.Location = new Point(33, 395);
        _progress.Size = new Size(292, 12);
        Controls.Add(_progress);

        var install = new Button { Text = "Install / Update", Size = new Size(120, 40), Location = new Point(333, 378) };
        install.Click += (_, _) => Install();
        Controls.Add(install);

        var uninstall = new Button { Text = "Uninstall", Size = new Size(100, 40), Location = new Point(460, 378) };
        uninstall.Click += (_, _) => Uninstall();
        Controls.Add(uninstall);

        var close = new Button { Text = "Close", Size = new Size(100, 40), Location = new Point(568, 378) };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
    }

    private static string GetInitialStatus()
    {
        string versionFile = Path.Combine(InstallDir, "version.txt");
        if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == ProductVersion)
            return $"Status: Installed v{ProductVersion}";
        return Directory.Exists(ProductRoot)
            ? $"Status: Older version detected — ready to update to v{ProductVersion}"
            : $"Status: Ready to install v{ProductVersion}";
    }

    private void Install()
    {
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                MessageBox.Show(this, "Close Autodesk Revit 2025 before installing or updating Flex Conduit.", ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _status.Text = "Status: Removing older versions...";
            _progress.Value = 10;
            Application.DoEvents();
            RemovePerUserManifests();
            if (Directory.Exists(ProductRoot)) Directory.Delete(ProductRoot, true);

            Directory.CreateDirectory(InstallDir);
            Directory.CreateDirectory(AddinDir);
            _status.Text = $"Status: Installing v{ProductVersion}...";
            _progress.Value = 35;
            Application.DoEvents();

            string dllPath = Path.Combine(InstallDir, DllFileName);
            using (Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.RevitFlexConduit.dll"))
            {
                if (src == null) throw new InvalidOperationException("Embedded Revit add-in payload is missing.");
                using var dst = File.Create(dllPath);
                src.CopyTo(dst);
            }
            File.WriteAllText(Path.Combine(InstallDir, "version.txt"), ProductVersion, new UTF8Encoding(false));

            _progress.Value = 72;
            string escaped = System.Security.SecurityElement.Escape(dllPath) ?? dllPath;
            string manifest =
                "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                $"    <Name>Revit Flex Conduit 2025 v{ProductVersion}</Name>\r\n" +
                $"    <Assembly>{escaped}</Assembly>\r\n" +
                "    <AddInId>4DBA337A-4F70-4B8B-A6EF-0D4DA6A29C55</AddInId>\r\n" +
                "    <FullClassName>RevitFlexConduit.App301</FullClassName>\r\n" +
                "    <VendorId>CTRN</VendorId>\r\n" +
                $"    <VendorDescription>Flex Conduit tools for Autodesk Revit 2025 — v{ProductVersion}</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";
            File.WriteAllText(Path.Combine(AddinDir, ManifestFileName), manifest, new UTF8Encoding(false));

            _progress.Value = 100;
            _status.Text = $"Status: Installed v{ProductVersion} successfully";

            string? systemDuplicate = FindSystemWideManifest();
            var msg = new StringBuilder();
            msg.AppendLine($"Flex Conduit v{ProductVersion} installed successfully.");
            msg.AppendLine();
            msg.AppendLine("Restart Revit 2025 completely.");
            msg.AppendLine("The Flex Conduit tooltip should report v3.0.1.");
            msg.AppendLine("The START/END dialog no longer assigns an invalid command-link default button.");
            if (!string.IsNullOrWhiteSpace(systemDuplicate))
            {
                msg.AppendLine();
                msg.AppendLine("A system-wide Flex Conduit manifest also exists and may load another copy:");
                msg.AppendLine(systemDuplicate);
            }
            MessageBox.Show(this, msg.ToString(), $"{ProductName} v{ProductVersion}", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Status: Installation failed";
            MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Uninstall()
    {
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                MessageBox.Show(this, "Close Autodesk Revit before uninstalling.", ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RemovePerUserManifests();
            if (Directory.Exists(ProductRoot)) Directory.Delete(ProductRoot, true);
            _progress.Value = 100;
            _status.Text = "Status: Uninstalled";
            MessageBox.Show(this, "All per-user Flex Conduit versions were removed from Revit 2025.", ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Status: Uninstall failed";
            MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RemovePerUserManifests()
    {
        if (!Directory.Exists(AddinDir)) return;
        foreach (string file in Directory.EnumerateFiles(AddinDir, "*.addin", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string text = File.ReadAllText(file);
                if (text.Contains("RevitFlexConduit", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Revit Flex Conduit 2025", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
            catch { }
        }
    }

    private static string? FindSystemWideManifest()
    {
        if (!Directory.Exists(SystemAddinDir)) return null;
        foreach (string file in Directory.EnumerateFiles(SystemAddinDir, "*.addin", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string text = File.ReadAllText(file);
                if (text.Contains("RevitFlexConduit", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Revit Flex Conduit 2025", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch { }
        }
        return null;
    }
}

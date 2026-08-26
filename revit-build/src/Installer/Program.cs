using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace RevitFlexConduitInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
    }
}

internal sealed class InstallerForm : Form
{
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();

    private const string ProductName = "Revit Flex Conduit 2025";
    private const string ProductVersion = "2.0.0";
    private const string DllFileName = "RevitFlexConduit.dll";
    private const string AddinFileName = "RevitFlexConduit2025-v2.addin";
    private const string VersionFileName = "version.txt";

    private static readonly string ProductRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citron",
        "RevitFlexConduit2025");

    private static readonly string InstallDir = Path.Combine(ProductRoot, "v" + ProductVersion);

    private static readonly string AddinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk",
        "Revit",
        "Addins",
        "2025");

    private static readonly string SystemAddinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Autodesk",
        "Revit",
        "Addins",
        "2025");

    public InstallerForm()
    {
        Text = $"{ProductName} v{ProductVersion} Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 430);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = Color.FromArgb(35, 48, 74)
        };
        header.Controls.Add(new Label
        {
            Text = "Flex Conduit for Revit 2025",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 20F),
            AutoSize = true,
            Location = new Point(28, 17)
        });
        header.Controls.Add(new Label
        {
            Text = $"Version {ProductVersion} • interactive routing • Systems ribbon integration",
            ForeColor = Color.FromArgb(215, 224, 239),
            AutoSize = true,
            Location = new Point(31, 58)
        });
        Controls.Add(header);

        var body = new Panel
        {
            Location = new Point(28, 122),
            Size = new Size(624, 206),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        body.Controls.Add(new Label
        {
            Text = $"Installation — v{ProductVersion}",
            Font = new Font("Segoe UI Semibold", 12F),
            AutoSize = true,
            Location = new Point(18, 16)
        });
        body.Controls.Add(new Label
        {
            Text = "Installs for the current Windows user only.\r\n\r\n" +
                   "• Removes older per-user Flex Conduit manifests and DLLs first\r\n" +
                   "• Uses Revit's standard Systems-tab API (not the old Electrical Tools tab)\r\n" +
                   "• Adds the Flex Conduit panel after the conduit-fitting/electrical area when available\r\n" +
                   "• Includes visible control points and interactive route updates\r\n" +
                   "• Does not require administrator permissions\r\n" +
                   "• Close Revit before installing or uninstalling",
            AutoSize = true,
            Location = new Point(18, 48)
        });
        Controls.Add(body);

        _status.Text = GetInitialStatus();
        _status.AutoSize = true;
        _status.Location = new Point(31, 347);
        Controls.Add(_status);

        _progress.Location = new Point(31, 382);
        _progress.Size = new Size(280, 12);
        Controls.Add(_progress);

        var install = new Button
        {
            Text = "Install / Update",
            Size = new Size(116, 40),
            Location = new Point(320, 365)
        };
        install.Click += (_, _) => Install();
        Controls.Add(install);

        var uninstall = new Button
        {
            Text = "Uninstall",
            Size = new Size(100, 40),
            Location = new Point(444, 365)
        };
        uninstall.Click += (_, _) => Uninstall();
        Controls.Add(uninstall);

        var close = new Button
        {
            Text = "Close",
            Size = new Size(100, 40),
            Location = new Point(552, 365)
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
    }

    private static string GetInitialStatus()
    {
        string versionPath = Path.Combine(InstallDir, VersionFileName);
        if (File.Exists(versionPath))
        {
            string installed = File.ReadAllText(versionPath).Trim();
            if (string.Equals(installed, ProductVersion, StringComparison.OrdinalIgnoreCase))
                return $"Status: Installed v{installed}";
        }

        if (Directory.Exists(ProductRoot))
            return $"Status: Older version detected — ready to update to v{ProductVersion}";

        return $"Status: Ready to install v{ProductVersion}";
    }

    private void Install()
    {
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                MessageBox.Show(
                    this,
                    "Please close Autodesk Revit before installing or updating Flex Conduit.",
                    ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _status.Text = "Status: Removing older versions...";
            _progress.Value = 10;
            Application.DoEvents();

            RemovePerUserFlexConduitManifests();
            if (Directory.Exists(ProductRoot))
                Directory.Delete(ProductRoot, true);

            _progress.Value = 30;
            _status.Text = $"Status: Installing v{ProductVersion}...";
            Application.DoEvents();

            Directory.CreateDirectory(InstallDir);
            Directory.CreateDirectory(AddinDir);
            string dllPath = Path.Combine(InstallDir, DllFileName);

            using (Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.RevitFlexConduit.dll"))
            {
                if (src == null)
                    throw new InvalidOperationException("Embedded add-in payload is missing.");

                using var dst = File.Create(dllPath);
                src.CopyTo(dst);
            }

            File.WriteAllText(Path.Combine(InstallDir, VersionFileName), ProductVersion, new UTF8Encoding(false));

            _progress.Value = 70;
            string escaped = System.Security.SecurityElement.Escape(dllPath) ?? dllPath;
            string addin =
                $"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                $"    <Name>Revit Flex Conduit 2025 v{ProductVersion}</Name>\r\n" +
                $"    <Assembly>{escaped}</Assembly>\r\n" +
                "    <AddInId>4DBA337A-4F70-4B8B-A6EF-0D4DA6A29C55</AddInId>\r\n" +
                "    <FullClassName>RevitFlexConduit.App</FullClassName>\r\n" +
                "    <VendorId>CTRN</VendorId>\r\n" +
                $"    <VendorDescription>Flex Conduit tools for Autodesk Revit 2025 — v{ProductVersion}</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";

            File.WriteAllText(Path.Combine(AddinDir, AddinFileName), addin, new UTF8Encoding(false));

            _progress.Value = 100;
            _status.Text = $"Status: Installed v{ProductVersion} successfully";

            string? systemDuplicate = FindSystemWideFlexConduitManifest();
            var message = new StringBuilder();
            message.AppendLine($"Flex Conduit v{ProductVersion} was installed successfully.");
            message.AppendLine();
            message.AppendLine("Restart Revit 2025 completely.");
            message.AppendLine();
            message.AppendLine("Expected after restart:");
            message.AppendLine("• This add-in no longer creates the old Electrical Tools tab.");
            message.AppendLine("• Flex Conduit appears on the Systems tab.");
            message.AppendLine($"• Hover the button and confirm the tooltip says v{ProductVersion}.");

            if (!string.IsNullOrWhiteSpace(systemDuplicate))
            {
                message.AppendLine();
                message.AppendLine("IMPORTANT: A second system-wide Flex Conduit manifest was detected:");
                message.AppendLine(systemDuplicate);
                message.AppendLine("That older system-wide copy may also need to be removed if Revit still shows the old tab.");
            }

            MessageBox.Show(
                this,
                message.ToString(),
                $"{ProductName} v{ProductVersion}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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
                MessageBox.Show(
                    this,
                    "Please close Autodesk Revit before uninstalling.",
                    ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            RemovePerUserFlexConduitManifests();
            if (Directory.Exists(ProductRoot))
                Directory.Delete(ProductRoot, true);

            _progress.Value = 100;
            _status.Text = "Status: Uninstalled";
            MessageBox.Show(
                this,
                "All per-user Flex Conduit versions were removed from Revit 2025.",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Status: Uninstall failed";
            MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RemovePerUserFlexConduitManifests()
    {
        if (!Directory.Exists(AddinDir)) return;

        foreach (string file in Directory.EnumerateFiles(AddinDir, "*.addin", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string text = File.ReadAllText(file);
                if (text.Contains("RevitFlexConduit.App", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("RevitFlexConduit.dll", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Revit Flex Conduit 2025", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Continue cleaning other legacy manifests. A locked file will be reported later
                // if it prevents the new manifest from being installed correctly.
            }
        }
    }

    private static string? FindSystemWideFlexConduitManifest()
    {
        if (!Directory.Exists(SystemAddinDir)) return null;

        foreach (string file in Directory.EnumerateFiles(SystemAddinDir, "*.addin", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string text = File.ReadAllText(file);
                if (text.Contains("RevitFlexConduit.App", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("RevitFlexConduit.dll", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Revit Flex Conduit 2025", StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch
            {
                // Ignore unreadable unrelated manifests.
            }
        }

        return null;
    }
}

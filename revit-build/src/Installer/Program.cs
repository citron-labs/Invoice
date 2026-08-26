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
    private const string DllFileName = "RevitFlexConduit.dll";
    private const string AddinFileName = "RevitFlexConduit2025.addin";
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Citron", "RevitFlexConduit2025");
    private static readonly string AddinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025");

    public InstallerForm()
    {
        Text = ProductName + " Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 390);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(35, 48, 74) };
        header.Controls.Add(new Label { Text = "Flex Conduit for Revit 2025", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 20F), AutoSize = true, Location = new Point(28, 18) });
        header.Controls.Add(new Label { Text = "Smooth electrical conduit workflow • ribbon integration", ForeColor = Color.FromArgb(215, 224, 239), AutoSize = true, Location = new Point(31, 57) });
        Controls.Add(header);

        var body = new Panel { Location = new Point(28, 118), Size = new Size(564, 190), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        body.Controls.Add(new Label { Text = "Installation", Font = new Font("Segoe UI Semibold", 12F), AutoSize = true, Location = new Point(18, 16) });
        body.Controls.Add(new Label { Text = "Installs for the current Windows user only.\r\n\r\n• Adds Electrical Tools > Flex Conduit to Revit 2025\r\n• Includes a dedicated ribbon icon\r\n• Does not require administrator permissions\r\n• Close Revit before installing or uninstalling", AutoSize = true, Location = new Point(18, 48) });
        Controls.Add(body);

        _status.Text = File.Exists(Path.Combine(InstallDir, DllFileName)) ? "Status: Installed" : "Status: Ready to install";
        _status.AutoSize = true; _status.Location = new Point(31, 323); Controls.Add(_status);
        _progress.Location = new Point(31, 348); _progress.Size = new Size(328, 12); Controls.Add(_progress);

        var install = new Button { Text = "Install", Size = new Size(104, 38), Location = new Point(372, 330) };
        install.Click += (_, _) => Install(); Controls.Add(install);
        var uninstall = new Button { Text = "Uninstall", Size = new Size(104, 38), Location = new Point(488, 330) };
        uninstall.Click += (_, _) => Uninstall(); Controls.Add(uninstall);
    }

    private void Install()
    {
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0) { MessageBox.Show(this, "Please close Autodesk Revit before installing.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _status.Text = "Status: Installing..."; _progress.Value = 15; Application.DoEvents();
            Directory.CreateDirectory(InstallDir); Directory.CreateDirectory(AddinDir);
            string dllPath = Path.Combine(InstallDir, DllFileName);
            using (Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.RevitFlexConduit.dll"))
            {
                if (src == null) throw new InvalidOperationException("Embedded add-in payload is missing.");
                using var dst = File.Create(dllPath); src.CopyTo(dst);
            }
            _progress.Value = 65;
            string escaped = System.Security.SecurityElement.Escape(dllPath) ?? dllPath;
            string addin = $"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>\r\n<RevitAddIns>\r\n  <AddIn Type=\"Application\">\r\n    <Name>Revit Flex Conduit 2025</Name>\r\n    <Assembly>{escaped}</Assembly>\r\n    <AddInId>4DBA337A-4F70-4B8B-A6EF-0D4DA6A29C55</AddInId>\r\n    <FullClassName>RevitFlexConduit.App</FullClassName>\r\n    <VendorId>CTRN</VendorId>\r\n    <VendorDescription>Flex Conduit tools for Autodesk Revit 2025</VendorDescription>\r\n  </AddIn>\r\n</RevitAddIns>\r\n";
            File.WriteAllText(Path.Combine(AddinDir, AddinFileName), addin, new UTF8Encoding(false));
            _progress.Value = 100; _status.Text = "Status: Installed successfully";
            MessageBox.Show(this, "Flex Conduit was installed successfully.\r\n\r\nOpen Revit 2025 and use Electrical Tools > Flex Conduit.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { _status.Text = "Status: Installation failed"; MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void Uninstall()
    {
        try
        {
            if (Process.GetProcessesByName("Revit").Length > 0) { MessageBox.Show(this, "Please close Autodesk Revit before uninstalling.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string addinPath = Path.Combine(AddinDir, AddinFileName); if (File.Exists(addinPath)) File.Delete(addinPath);
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
            _progress.Value = 100; _status.Text = "Status: Uninstalled";
            MessageBox.Show(this, "Flex Conduit was removed from Revit 2025.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { _status.Text = "Status: Uninstall failed"; MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

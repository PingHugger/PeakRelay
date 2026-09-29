using PeakRelay.Installer.Core;

namespace PeakRelay.Installer.Client;

/// <summary>Client installer wizard: game dir → relay endpoint → install.</summary>
public sealed class MainForm : Form
{
    private readonly TextBox _gameDir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _relayHost = new() { Text = "127.0.0.1" };
    private readonly NumericUpDown _relayPort = new() { Minimum = 1, Maximum = 65535, Value = 5055 };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 28, 36), ForeColor = Color.FromArgb(220, 228, 240),
    };
    private readonly Button _install = new() { Text = "Install", Dock = DockStyle.Bottom, Height = 36 };

    public MainForm()
    {
        Text = "PeakRelay client installer (players)";
        MinimumSize = new Size(620, 480);
        Size = new Size(680, 540);
        StartPosition = FormStartPosition.CenterScreen;

        var defaultDir = GameLocator.FindDefaultGameDir();
        if (defaultDir != null)
            _gameDir.Text = defaultDir;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "PEAK install folder (must contain PEAK.exe):", AutoSize = true }, 0, 0);
        var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirRow.Controls.Add(_gameDir, 0, 0);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the PEAK install folder" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _gameDir.Text = dialog.SelectedPath;
        };
        dirRow.Controls.Add(browse, 1, 0);
        layout.Controls.Add(dirRow, 0, 1);

        var relayRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        relayRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        relayRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        relayRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        relayRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        relayRow.Controls.Add(new Label { Text = "Relay host:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        relayRow.Controls.Add(_relayHost, 1, 0);
        relayRow.Controls.Add(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        relayRow.Controls.Add(_relayPort, 3, 0);
        layout.Controls.Add(relayRow, 0, 2);

        layout.Controls.Add(new Label
        {
            Text = "Adds the PeakRelay client plugin: routes the game to your relay and installs the\r\n" +
                   "in-game SERVERS browser. Launch PEAK normally (Steam running, no headless flags).\r\n" +
                   "Join via the SERVERS button or the usual 6-character room code.",
            AutoSize = true, ForeColor = Color.DimGray,
        }, 0, 3);
        layout.Controls.Add(_log, 0, 4);
        Controls.Add(layout);
        Controls.Add(_install);

        _install.Click += async (_, _) => await InstallAsync();
    }

    private async Task InstallAsync()
    {
        var gameDir = _gameDir.Text.Trim();
        if (!GameLocator.LooksLikeGameRoot(gameDir))
        {
            MessageBox.Show(this, $"'{gameDir}' does not contain PEAK.exe — pick the game install folder.",
                "Invalid game folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = false;
        _log.Clear();
        try
        {
            var request = new InstallRequest
            {
                GameDir = gameDir,
                Client = new ClientSettings(
                    string.IsNullOrWhiteSpace(_relayHost.Text) ? "127.0.0.1" : _relayHost.Text.Trim(),
                    (int)_relayPort.Value),
            };

            var lines = await Task.Run(() => InstallRunner.Run(request));
            foreach (var line in lines)
                _log.AppendText(line + Environment.NewLine);
            _log.AppendText(Environment.NewLine);
            foreach (var line in InstallRunner.Verify(request))
                _log.AppendText(line + Environment.NewLine);
            _log.AppendText(Environment.NewLine + "Done. Launch PEAK normally; the Join button becomes SERVERS." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.AppendText($"ERROR: {ex.Message}" + Environment.NewLine);
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _install.Enabled = true;
        }
    }
}

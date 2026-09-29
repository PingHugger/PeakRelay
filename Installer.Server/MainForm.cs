using PeakRelay.Installer.Core;

namespace PeakRelay.Installer.Server;

/// <summary>Dedicated-server installer wizard: game dir → room config → relay staging → install.</summary>
public sealed class MainForm : Form
{
    private readonly TextBox _gameDir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _room = new() { Text = "DBPEAK" };
    private readonly TextBox _displayName = new() { Text = "PeakRelay Dedicated" };
    private readonly TextBox _password = new() { Text = "" };
    private readonly NumericUpDown _maxPlayers = new() { Minimum = 1, Maximum = 100, Value = 20 };
    private readonly TextBox _relayHost = new() { Text = "127.0.0.1" };
    private readonly NumericUpDown _relayPort = new() { Minimum = 1, Maximum = 65535, Value = 5055 };
    private readonly TextBox _hostName = new() { Text = "DedicatedHost" };
    private readonly CheckBox _stageRelay = new() { Text = "Also copy the relay server files (PeakRelay.Server + start script)" };
    private readonly TextBox _relayDir = new() { Enabled = false, Text = "" };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 28, 36), ForeColor = Color.FromArgb(220, 228, 240),
    };
    private readonly Button _install = new() { Text = "Install", Dock = DockStyle.Bottom, Height = 36 };

    public MainForm()
    {
        Text = "PeakRelay server installer (dedicated host)";
        MinimumSize = new Size(640, 560);
        Size = new Size(720, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var defaultDir = GameLocator.FindDefaultGameDir();
        if (defaultDir != null)
            _gameDir.Text = defaultDir;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGameTab());
        tabs.TabPages.Add(BuildRoomTab());
        tabs.TabPages.Add(BuildRelayTab());

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(_install, 0, 1);

        Controls.Add(root);

        _stageRelay.CheckedChanged += (_, _) => _relayDir.Enabled = _stageRelay.Checked;
        _install.Click += async (_, _) => await InstallAsync();
        _relayDir.DoubleClick += (_, _) => PickRelayDir();
    }

    private TabPage BuildGameTab()
    {
        var page = new TabPage("1 · Game");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "PEAK install folder (must contain PEAK.exe):",
            AutoSize = true,
        }, 0, 0);
        var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirRow.Controls.Add(_gameDir, 0, 0);
        dirRow.Controls.Add(new Button { Text = "Browse…", AutoSize = true }, 1, 0);
        ((Button)dirRow.Controls[1]).Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the PEAK install folder" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _gameDir.Text = dialog.SelectedPath;
        };
        layout.Controls.Add(dirRow, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "Installs BepInEx 5.4.23.2 (if missing) and the PeakRelay.Dedicated plugin.\r\n" +
                   "After installing, run the game with -batchmode -nographics to host headless;\r\n" +
                   "Steam does not need to be running.",
            AutoSize = true, ForeColor = Color.DimGray,
        }, 0, 2);
        layout.Controls.Add(_log, 0, 3);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildRoomTab()
    {
        var page = new TabPage("2 · Room");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, grid.RowCount - 1);
            grid.Controls.Add(control, 1, grid.RowCount - 1);
            grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }
        Row("Room code (6 chars)", _room);
        Row("Display name", _displayName);
        Row("Password (optional)", _password);
        Row("Max players", _maxPlayers);
        Row("Host player name", _hostName);
        grid.Controls.Add(new Label
        {
            Text = "Room code must be exactly 6 characters for PEAK's join-by-code UI.\r\n" +
                   "Host player name is used when Steam cannot provide one (headless runs).",
            AutoSize = true, ForeColor = Color.DimGray,
        }, 0, grid.RowCount);
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildRelayTab()
    {
        var page = new TabPage("3 · Relay");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, grid.RowCount - 1);
            grid.Controls.Add(control, 1, grid.RowCount - 1);
            grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }
        Row("Relay host", _relayHost);
        Row("Relay port", _relayPort);
        grid.SetColumnSpan(_stageRelay, 2);
        grid.Controls.Add(_stageRelay, 0, grid.RowCount);
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.SetColumnSpan(_relayDir, 2);
        grid.Controls.Add(_relayDir, 0, grid.RowCount);
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        grid.Controls.Add(new Label
        {
            Text = "This server connects to the relay at host:port. Run the relay on the same\r\n" +
                   "machine (127.0.0.1) or a remote box; stage its files below if you need\r\n" +
                   "them on this machine too. Tick the box, then double-click the path field.",
            AutoSize = true, ForeColor = Color.DimGray,
        }, 0, grid.RowCount);
        page.Controls.Add(grid);
        return page;
    }

    private void PickRelayDir()
    {
        using var dialog = new FolderBrowserDialog { Description = "Where to copy the relay server files" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _relayDir.Text = dialog.SelectedPath;
    }

    private async Task InstallAsync()
    {
        var gameDir = _gameDir.Text.Trim();
        var room = _room.Text.Trim();
        if (room.Length != 6)
        {
            MessageBox.Show(this, "Room code must be exactly 6 characters (PEAK join-code format).",
                "Invalid room code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
            var settings = new ServerSettings(
                RoomName: room,
                DisplayName: string.IsNullOrWhiteSpace(_displayName.Text) ? room : _displayName.Text.Trim(),
                Mode: "standard",
                Password: _password.Text.Trim(),
                MaxPlayers: (int)_maxPlayers.Value,
                RelayHost: string.IsNullOrWhiteSpace(_relayHost.Text) ? "127.0.0.1" : _relayHost.Text.Trim(),
                RelayPort: (int)_relayPort.Value,
                AutoHost: true,
                HostName: string.IsNullOrWhiteSpace(_hostName.Text) ? "DedicatedHost" : _hostName.Text.Trim());

            var request = new InstallRequest
            {
                GameDir = gameDir,
                Server = settings,
                RelayStageDir = _stageRelay.Checked && !string.IsNullOrWhiteSpace(_relayDir.Text)
                    ? _relayDir.Text.Trim()
                    : null,
            };

            var lines = await Task.Run(() => InstallRunner.Run(request));
            foreach (var line in lines)
                _log.AppendText(line + Environment.NewLine);
            _log.AppendText(Environment.NewLine);
            foreach (var line in InstallRunner.Verify(request))
                _log.AppendText(line + Environment.NewLine);
            _log.AppendText(Environment.NewLine + "Done. Start the relay, then launch PEAK with -batchmode -nographics." + Environment.NewLine);
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

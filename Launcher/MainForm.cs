using PeakRelay.Installer.Core;
using PeakRelay.Launcher.Core;

namespace PeakRelay.Launcher;

/// <summary>
/// One-window launcher: doctor status on top, three verbs (Install/Update, Play, Host) at
/// the bottom, a run log underneath. Everything the buttons do goes through the same
/// Launcher.Core engine the CLI drives.
/// </summary>
public sealed class MainForm : Form
{
    private readonly LauncherState _state = LauncherState.Load();
    private readonly TextBox _gameDir = new() { Dock = DockStyle.Fill };
    private readonly ListView _status = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 28, 36), ForeColor = Color.FromArgb(220, 228, 240),
    };
    private readonly Button _install = new() { Dock = DockStyle.Fill, Height = 44, Enabled = false, Text = "Install" };
    private readonly Button _play = new()
    {
        Dock = DockStyle.Fill, Height = 44, Enabled = false,
        Text = "PLAY", Font = new Font("Segoe UI", 11f, FontStyle.Bold),
    };
    private readonly Button _host = new() { Dock = DockStyle.Fill, Height = 44, Text = "Host relay" };
    private readonly Label _hostState = new() { Text = "relay idle", AutoSize = true, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleCenter };
    private readonly RelayHost _relayHost = new();
    private readonly System.Windows.Forms.Timer _autoRefresh = new() { Interval = 20_000 };
    private bool _busy;

    public MainForm()
    {
        Text = $"PeakRelay launcher {ReleaseClient.RunningVersion()}";
        MinimumSize = new Size(720, 620);
        Size = new Size(800, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var defaultDir = string.IsNullOrWhiteSpace(_state.GameDir) ? GameLocator.FindDefaultGameDir() : _state.GameDir;
        if (defaultDir != null)
            _gameDir.Text = defaultDir;

        _status.Columns.Add("status", 70);
        _status.Columns.Add("check", 150);
        _status.Columns.Add("detail", 470);

        var dirRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 30 };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirRow.Controls.Add(_gameDir, 0, 0);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => Browse();
        dirRow.Controls.Add(browse, 1, 0);

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 50, Padding = new Padding(0, 6, 0, 0) };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.Controls.Add(_install, 0, 0);
        buttonRow.Controls.Add(_play, 1, 0);

        var hostRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 50 };
        hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        hostRow.Controls.Add(_host, 0, 0);
        hostRow.Controls.Add(_hostState, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // dir row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));    // status list
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // install / play
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // host row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));    // log
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // hint
        root.Controls.Add(dirRow, 0, 0);
        root.Controls.Add(_status, 0, 1);
        root.Controls.Add(buttonRow, 0, 2);
        root.Controls.Add(hostRow, 0, 3);
        root.Controls.Add(_log, 0, 4);
        root.Controls.Add(new Label
        {
            Text = "install/repair from GitHub Releases · doctor re-checks every 20 s · CLI: PeakRelay.Launcher.exe doctor|install|update|play|host",
            AutoSize = true, ForeColor = Color.DimGray,
        }, 0, 5);

        Controls.Add(root);

        _install.Click += async (_, _) => await RunAsync(InstallAsync).ConfigureAwait(true);
        _play.Click += async (_, _) => await RunAsync(PlayAsync).ConfigureAwait(true);
        _host.Click += async (_, _) => await RunAsync(HostAsync).ConfigureAwait(true);
        _gameDir.TextChanged += (_, _) => _ = RunAsync(RefreshAsync);
        _autoRefresh.Tick += (_, _) => _ = RunAsync(RefreshAsync);

        Shown += (_, _) =>
        {
            _ = RunAsync(RefreshAsync);
            _autoRefresh.Start();
        };
        FormClosing += (_, _) =>
        {
            _autoRefresh.Stop();
            _relayHost.Dispose();
        };
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the PEAK install folder (must contain PEAK.exe)" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _gameDir.Text = dialog.SelectedPath;
    }

    /// <summary>Serializes UI work; swallows and logs errors so the window never dies mid-click.</summary>
    private async Task RunAsync(Func<Task> work)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"error: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshAsync()
    {
        var gameDir = _gameDir.Text;
        var checks = await Task.Run(() => Doctor.Inspect(gameDir, _state)).ConfigureAwait(true);
        _status.BeginUpdate();
        _status.Items.Clear();
        foreach (var check in checks)
        {
            var mark = check.Status switch
            {
                CheckStatus.Pass => "OK",
                CheckStatus.Warn => "WARN",
                _ => "FAIL",
            };
            var color = check.Status switch
            {
                CheckStatus.Pass => Color.FromArgb(120, 200, 120),
                CheckStatus.Warn => Color.FromArgb(230, 190, 90),
                _ => Color.FromArgb(230, 110, 100),
            };
            var item = _status.Items.Add(mark);
            item.ForeColor = color;
            item.SubItems.Add(check.Name);
            item.SubItems.Add(check.Detail);
        }
        _status.EndUpdate();

        var ready = Doctor.PlayReady(checks);
        _play.Enabled = ready;
        _install.Enabled = GameLocator.LooksLikeGameRoot(gameDir);
        if (!_install.Enabled)
            _install.Text = "Install";
        else if (ready)
            _install.Text = _state.InstalledTag == null ? "Reinstall" : "Update";
    }

    private async Task InstallAsync()
    {
        var gameDir = _gameDir.Text;
        if (!GameLocator.LooksLikeGameRoot(gameDir))
        {
            Log($"error: '{gameDir}' is not a PEAK install (no PEAK.exe)");
            return;
        }
        _install.Enabled = false;
        Log("checking release channel…");
        using var client = new ReleaseClient();
        ReleaseInfo latest;
        try
        {
            latest = await client.LatestAsync().ConfigureAwait(true)
                     ?? throw new InvalidOperationException("no releases found — nothing to install");
        }
        catch (ReleaseChannelException ex)
        {
            Log($"error: {ex.Message}");
            if (PromptForToken())
            {
                Log("token saved — retrying…");
                client.Dispose();
                await InstallAsync().ConfigureAwait(true);
            }
            return;
        }
        var asset = ModApply.DefaultAssetFor(latest);
        Log($"latest: {latest.Tag} → {asset.Name}");

        foreach (var line in await ModApply.ApplyAsync(gameDir, asset, _state, latest.Tag,
                     (a, destination, t) => client.DownloadAsync(a, destination, t),
                     host: "127.0.0.1", port: 5055).ConfigureAwait(true))
            Log("  " + line);

        Log($"applied {latest.Tag}.");
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task PlayAsync()
    {
        var gameDir = _gameDir.Text;
        var checks = await Task.Run(() => Doctor.Inspect(gameDir, _state)).ConfigureAwait(true);
        if (!Doctor.PlayReady(checks))
        {
            Log("cannot start — fix the FAIL rows first (Install repairs them).");
            return;
        }
        var process = await Task.Run(() => PlayLauncher.Start(gameDir)).ConfigureAwait(true);
        Log($"PEAK started (pid {process.Id}).");
    }

    private async Task HostAsync()
    {
        if (_relayHost.State == RelayHostState.Running)
        {
            _relayHost.Stop();
            _host.Text = "Host relay";
            _hostState.Text = "relay idle";
            Log("relay stopped (ports released).");
            return;
        }
        try
        {
            await Task.Run(() => _relayHost.Start(5055, 5056)).ConfigureAwait(true);
            _host.Text = "Stop relay";
            _hostState.Text = $"relay: game 5055 · http://127.0.0.1:5056/rooms";
            Log($"relay running in-process (game 5055, directory 5056).");
        }
        catch (Exception ex)
        {
            _hostState.Text = "relay failed";
            Log($"relay failed: {ex.Message}");
        }
    }

    /// <summary>Paste-prompt for a GitHub token (private repo). True when one was saved.</summary>
    private bool PromptForToken()
    {
        string? token = null;
        var ok = InputDialog.Show(this,
            "GitHub token required",
            "The release channel is private. Paste a GitHub token with read access " +
            "(PEAKRELAY_GH_TOKEN also works):",
            usePassword: true, value: ref token);
        if (!ok || string.IsNullOrWhiteSpace(token))
            return false;
        TokenStore.SaveToFile(token);
        Log($"token saved to {LauncherPaths.TokenFile}");
        return true;
    }

    private void Log(string line) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
}

using System.Text;

namespace PeakRelay.Installer.Core;

/// <summary>What one installer run does, resolved from CLI flags or GUI fields.</summary>
public sealed class InstallRequest
{
    public string GameDir { get; init; } = "";
    public ServerSettings? Server { get; init; }
    public ClientSettings? Client { get; init; }
    public string? RelayStageDir { get; init; }
}

/// <summary>Executes an InstallRequest; the GUI shows the same steps in a progress list.</summary>
public static class InstallRunner
{
    /// <summary>Runs the install, invoking onStep for each completed action (returns step log).</summary>
    public static List<string> Run(InstallRequest request, Action<string>? onStep = null)
    {
        var log = new List<string>();
        void Say(string text)
        {
            log.Add(text);
            onStep?.Invoke(text);
        }

        if (!GameLocator.LooksLikeGameRoot(request.GameDir))
            throw new InvalidOperationException(
                $"'{request.GameDir}' does not look like a PEAK install (no PEAK.exe)");

        Say($"game dir: {request.GameDir}");
        foreach (var line in PluginDeployer.EnsureBepInEx(request.GameDir))
            Say(line);

        if (request.Server != null)
        {
            foreach (var line in PluginDeployer.DeployDedicated(request.GameDir))
                Say(line);
            var cfg = PluginDeployer.WriteServerConfig(request.GameDir, request.Server);
            Say($"server.json written: {cfg}");
        }

        if (request.Client != null)
        {
            foreach (var line in PluginDeployer.DeployClient(request.GameDir))
                Say(line);
            var cfg = PluginDeployer.WriteClientConfig(request.GameDir, request.Client);
            Say($"client config written: {cfg}");
        }

        if (request.RelayStageDir != null)
            Say($"relay staged: {RelayStager.Stage(request.RelayStageDir)}");

        return log;
    }

    /// <summary>Post-install verification lines.</summary>
    public static List<string> Verify(InstallRequest request)
    {
        var checks = request.Server != null
            ? Verifier.VerifyServerInstall(request.GameDir, request.Server)
            : Verifier.VerifyClientInstall(request.GameDir, request.Client!);
        return checks.Select(c => $"[{(c.Ok ? "OK" : "FAIL")}] {c.Name}: {c.Detail}").ToList();
    }
}


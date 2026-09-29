using System.Reflection;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// Build-time contract of the installer payload: every resource name PluginDeployer asks
/// for must exist in Installer.Core (payload embedding lives there now), and every
/// embedded payload resource must be named by the code. A drift breaks the test suite,
/// not an install.
/// </summary>
public class InstallerPayloadTests
{
    private static readonly Assembly Core = typeof(PeakRelay.Installer.Core.PayloadNames).Assembly;

    /// <summary>
    /// CI checkouts have no payload unless the staging script ran; the contract only means
    /// something when it was. Tests no-op (pass trivially) with a console note in that case.
    /// </summary>
    private static bool PayloadStaged()
    {
        var dir = Path.Combine(typeof(InstallerPayloadTests).Assembly.Location,
            "../../../../Installer.Core/Payload/payload");
        if (File.Exists(Path.GetFullPath(Path.Combine(dir, "bepinex.zip"))))
            return true;
        Console.WriteLine("[skip] installer payload not staged — run scripts/prepare-installer-payload.sh");
        return false;
    }

    [Fact]
    public void BepInEx_zip_is_embedded()
    {
        if (!PayloadStaged()) return;
        using var stream = Core.GetManifestResourceStream("PeakRelay.Installer.Payload.bepinex.zip");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 100_000, $"bepinex.zip looks truncated: {stream.Length} bytes");
    }

    [Fact]
    public void Doorstop_loader_is_embedded()
    {
        if (!PayloadStaged()) return;
        using var stream = Core.GetManifestResourceStream("PeakRelay.Installer.Payload.doorstop.winhttp.dll");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 10_000, $"doorstop winhttp.dll looks truncated: {stream!.Length} bytes");
    }

    [Fact]
    public void Relay_zip_is_embedded()
    {
        if (!PayloadStaged()) return;
        using var stream = Core.GetManifestResourceStream("PeakRelay.Installer.Payload.relay.relay.zip");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 10_000, $"relay.zip looks truncated: {stream.Length} bytes");
    }

    [Fact]
    public void Every_declared_plugin_dll_is_embedded()
    {
        if (!PayloadStaged()) return;
        foreach (var (set, dlls) in PeakRelay.Installer.Core.PayloadNames.PluginSets)
        foreach (var dll in dlls)
        {
            using var stream = Core.GetManifestResourceStream($"PeakRelay.Installer.Payload.{set.Replace('/', '.')}.{dll}");
            Assert.True(stream != null, $"missing embedded resource for {set}/{dll}");
            Assert.True(stream!.Length > 1000, $"{set}/{dll} looks truncated: {stream.Length} bytes");
        }
    }

    [Fact]
    public void No_unnamed_payload_resources()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        expected.Add("PeakRelay.Installer.Payload.bepinex.zip");
        expected.Add("PeakRelay.Installer.Payload.doorstop.winhttp.dll");
        expected.Add("PeakRelay.Installer.Payload.relay.relay.zip");
        foreach (var (set, dlls) in PeakRelay.Installer.Core.PayloadNames.PluginSets)
        foreach (var dll in dlls)
            expected.Add($"PeakRelay.Installer.Payload.{set.Replace('/', '.')}.{dll}");

        var actual = Core.GetManifestResourceNames()
            .Where(n => n.StartsWith("PeakRelay.Installer.Payload.", StringComparison.Ordinal))
            .ToHashSet();

        var extra = actual.Except(expected).ToList();
        var missing = expected.Except(actual).ToList();
        Assert.True(extra.Count == 0, $"embedded but unnamed in code: {string.Join(", ", extra)}");
        Assert.True(missing.Count == 0, $"named in code but not embedded: {string.Join(", ", missing)}");
    }
}

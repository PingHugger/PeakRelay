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

    [Fact]
    public void BepInEx_zip_is_embedded()
    {
        using var stream = Core.GetManifestResourceStream("PeakRelay.Installer.Payload.bepinex.zip");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 100_000, $"bepinex.zip looks truncated: {stream.Length} bytes");
    }

    [Fact]
    public void Relay_zip_is_embedded()
    {
        using var stream = Core.GetManifestResourceStream("PeakRelay.Installer.Payload.relay.relay.zip");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 10_000, $"relay.zip looks truncated: {stream.Length} bytes");
    }

    [Fact]
    public void Every_declared_plugin_dll_is_embedded()
    {
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

using BepInEx;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Realtime;

namespace PeakRelay.Client;

/// <summary>
/// PeakRelay M0: installs RelaySocket as Photon's UDP socket implementation and (optionally)
/// streams datagram summaries to a local trace collector. No gameplay behavior is changed.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RelayPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.peakrelay.client";
    public const string PluginName = "PeakRelay";
    public const string PluginVersion = "0.1.0";

    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(PluginName);
    private Harmony? _harmony;

    private void Awake()
    {
        RelayConfig.Bind(Config);

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(PunSocketPatches));

        Log.LogInfo($"PeakRelay {PluginVersion}: RelaySocket registered for UDP protocol " +
                    $"(relay={RelayConfig.RelayEnabled}, trace={RelayConfig.TraceEnabled}:{RelayConfig.TracePort})");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    /// <summary>
    /// Redirects every LoadBalancingPeer (game and voice) to RelaySocket by editing the public
    /// SocketImplementationConfig dictionary in the peer constructor, before any Connect runs.
    /// No PUN/Photon3 internals are patched. The flag is read live so the shim can stay
    /// passive (vanilla UDP) while tracing, per M0 scope.
    /// </summary>
    [HarmonyPatch(typeof(LoadBalancingPeer), MethodType.Constructor)]
    internal static class PunSocketPatches
    {
        [HarmonyPostfix]
        public static void InstallSocketImplementation(LoadBalancingPeer __instance)
        {
            var config = __instance.SocketImplementationConfig;
            if (config.TryGetValue(ConnectionProtocol.Udp, out var current) &&
                current == typeof(RelaySocket))
            {
                return; // already installed (peers can be constructed more than once)
            }
            config[ConnectionProtocol.Udp] = typeof(RelaySocket);
        }
    }
}

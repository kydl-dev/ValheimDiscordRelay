using KydlCore.Common;
using KydlCore.Core;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay;

/// <summary>Server up / down / restart notifications. Gate: EnableAll and [Server Notifications] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
internal sealed class ServerNotificationsFeature : IFeature
{
    internal const string FeatureId = "servernotifications";

    /// <summary>True between Start and Stop.</summary>
    internal static bool Running { get; private set; }

    public string Id { get { return FeatureId; } }

    public bool IsEnabled() { return Functions.IsOn(ModSettings.On, ServerConfig.ServerNotificationsEnabled != null && ServerConfig.ServerNotificationsEnabled.Value); }

    public bool Start()
    {
        Running = true;
        ModLog.Verbose("ServerNotificationsFeature: started");
        return Running;
    }

    public bool Stop()
    {
        Running = false;
        ModLog.Verbose("ServerNotificationsFeature: stopped");
        return !Running;
    }
}

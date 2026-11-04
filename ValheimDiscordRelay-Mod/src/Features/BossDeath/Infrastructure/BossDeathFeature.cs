using KydlCore.Common;
using KydlCore.Core;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay;

/// <summary>Boss kill announcements and boss death videos. Gate: EnableAll and [Boss Death] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
internal sealed class BossDeathFeature : IFeature
{
    internal const string FeatureId = "bossdeath";

    /// <summary>True between Start and Stop.</summary>
    internal static bool Running { get; private set; }

    public string Id { get { return FeatureId; } }

    public bool IsEnabled() { return Functions.IsOn(ModSettings.On, BossConfig.Enabled != null && BossConfig.Enabled.Value); }

    public bool Start()
    {
        Running = true;
        ModLog.Verbose("BossDeathFeature: started");
        return Running;
    }

    public bool Stop()
    {
        Running = false;
        ModLog.Verbose("BossDeathFeature: stopped");
        return !Running;
    }
}

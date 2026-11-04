using KydlCore.Common;
using KydlCore.Core;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay;

/// <summary>Screenshots sent to Discord by hotkey or the hub buttons. Gate: EnableAll and [Screenshots] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
internal sealed class ScreenshotsFeature : IFeature
{
    internal const string FeatureId = "screenshots";

    /// <summary>True between Start and Stop.</summary>
    internal static bool Running { get; private set; }

    public string Id { get { return FeatureId; } }

    public bool IsEnabled() { return Functions.IsOn(ModSettings.On, ClientConfig.ScreenshotServerEnabled != null && ClientConfig.ScreenshotServerEnabled.Value); }

    public bool Start()
    {
        Running = true;
        ModLog.Verbose("ScreenshotsFeature: started");
        return Running;
    }

    public bool Stop()
    {
        Running = false;
        ModLog.Verbose("ScreenshotsFeature: stopped");
        return !Running;
    }
}

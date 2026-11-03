using Kydl.Common;
using Kydl.Core.Features;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>Hotkey video clips. Gate: EnableAll and [Clips] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
    internal sealed class ClipsFeature : IFeature
    {
        internal const string FeatureId = "clips";

        /// <summary>True between Start and Stop.</summary>
        internal static bool Running { get; private set; }

        public string Id { get { return FeatureId; } }

        public bool IsEnabled() { return Functions.IsOn(ModSettings.On, ClipConfig.ServerEnabled != null && ClipConfig.ServerEnabled.Value); }

        public bool Start()
        {
            Running = true;
            ModLog.Verbose("ClipsFeature: started");
            return Running;
        }

        public bool Stop()
        {
            Running = false;
            ModLog.Verbose("ClipsFeature: stopped");
            return !Running;
        }
    }
}
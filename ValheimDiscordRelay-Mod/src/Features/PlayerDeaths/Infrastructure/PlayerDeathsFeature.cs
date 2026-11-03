using Kydl.Common;
using Kydl.Core.Features;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>Player death messages, death camera and death videos. Gate: EnableAll and [Player Deaths] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
    internal sealed class PlayerDeathsFeature : IFeature
    {
        internal const string FeatureId = "playerdeaths";

        /// <summary>True between Start and Stop.</summary>
        internal static bool Running { get; private set; }

        public string Id { get { return FeatureId; } }

        public bool IsEnabled() { return Functions.IsOn(ModSettings.On, ClientConfig.DeathServerEnabled != null && ClientConfig.DeathServerEnabled.Value); }

        public bool Start()
        {
            Running = true;
            ModLog.Verbose("PlayerDeathsFeature: started");
            return Running;
        }

        public bool Stop()
        {
            Running = false;
            ModLog.Verbose("PlayerDeathsFeature: stopped");
            return !Running;
        }
    }
}
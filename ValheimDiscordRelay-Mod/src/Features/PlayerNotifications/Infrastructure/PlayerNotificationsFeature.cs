using Kydl.Common;
using Kydl.Core.Features;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>Player arrival and leave notifications. Gate: EnableAll and [Player Notifications] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
    internal sealed class PlayerNotificationsFeature : IFeature
    {
        internal const string FeatureId = "playernotifications";

        /// <summary>True between Start and Stop.</summary>
        internal static bool Running { get; private set; }

        public string Id { get { return FeatureId; } }

        public bool IsEnabled() { return Functions.IsOn(ModSettings.On, PlayerNotificationConfig.Enabled != null && PlayerNotificationConfig.Enabled.Value); }

        public bool Start()
        {
            Running = true;
            ModLog.Verbose("PlayerNotificationsFeature: started");
            return Running;
        }

        public bool Stop()
        {
            Running = false;
            ModLog.Verbose("PlayerNotificationsFeature: stopped");
            return !Running;
        }
    }
}
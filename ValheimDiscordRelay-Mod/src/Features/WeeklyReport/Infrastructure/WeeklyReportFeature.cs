using Kydl.Common;
using Kydl.Core.Features;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Report;
using ValheimDiscordRelay.Server;

namespace ValheimDiscordRelay
{
    /// <summary>The weekly report (Discord post and in-game window). Gate: EnableAll and [Weekly Report] Enable. The code that does the work reads the gate itself; this class carries the switch state for the FeatureRegistry.</summary>
    internal sealed class WeeklyReportFeature : IFeature
    {
        internal const string FeatureId = "weeklyreport";

        /// <summary>True between Start and Stop.</summary>
        internal static bool Running { get; private set; }

        public string Id { get { return FeatureId; } }

        public bool IsEnabled() { return Functions.IsOn(ModSettings.On, ReportConfig.Enabled != null && ReportConfig.Enabled.Value); }

        public bool Start()
        {
            Running = true;
            ModLog.Verbose("WeeklyReportFeature: started");
            return Running;
        }

        public bool Stop()
        {
            Running = false;
            ModLog.Verbose("WeeklyReportFeature: stopped");
            return !Running;
        }
    }
}
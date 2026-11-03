using BepInEx.Configuration;
using KydlCore.Common;
using KydlCore.Core;
using ValheimDiscordRelay.Client;
using ValheimDiscordRelay.Presentation.Hub.Registration;

namespace ValheimDiscordRelay.Bootstrap
{
    /// <summary>Composition root: the mod's KY hub page (title, settings and screenshots tabs) and its features in the FeatureRegistry (MIGRATION_DECISIONS A4).</summary>
    internal sealed class ModInstaller
    {
        private readonly ConfigFile _config;
        private readonly ClientCaptureService _capture;
        private readonly bool _showHub;

        internal ModInstaller(ConfigFile config, ClientCaptureService capture, bool showHub)
        {
            _config = config;
            _capture = capture;
            _showHub = showHub;
            Features = new FeatureRegistry(line => ModLog.Verbose(line));
        }

        internal static ModInstaller Current { get; private set; }

        internal FeatureRegistry Features { get; }

        internal bool Install()
        {
            Current = this;
            bool page = !_showHub || HubTabs.RegisterModPage(_config, _capture);
            bool added = true;
            IFeature[] features =
            {
                new ChatFeature(),
                new ScreenshotsFeature(),
                new ClipsFeature(),
                new PlayerDeathsFeature(),
                new BossDeathFeature(),
                new ServerNotificationsFeature(),
                new PlayerNotificationsFeature(),
                new WeeklyReportFeature()
            };

            foreach (IFeature feature in features)
            {
                OperationResponse<int> add = Features.Add(feature);
                if (!add.IsSuccess)
                {
                    added = false;
                    ModLog.Error("ModInstaller: " + add.Reason);
                }
            }

            return page && added && Refresh();
        }

        internal bool Refresh()
        {
            OperationResponse<int> r = Features.Refresh();
            if (r.Status == ResponseStatus.Failed)
            {
                ModLog.Warn("ModInstaller: " + r.Reason);
            }
            else
            {
                ModLog.Verbose("ModInstaller: refresh - " + r);
            }

            return r.IsSuccess;
        }

        internal bool Uninstall()
        {
            OperationResponse<int> r = Features.StopAll();
            bool gone = !_showHub || HubTabs.UnregisterModPage();
            if (Current == this)
            {
                Current = null;
            }

            return r.IsSuccess && gone;
        }
    }
}

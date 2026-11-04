using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ValheimDiscordRelay.Tests.Integration;

/// <summary>
/// Secrecy contract of the single config: a webhook URL that only the server uses must NEVER be bound through SyncedConfig (ServerSync would send
/// it to every player); it is bound through AdminOnly.Bind, which hands it to admins only (AdminSettingsChannel). The four webhook URLs the
/// players' games upload to ARE synced on purpose (each game posts its own video to Discord). Read from src/Configuration/ModSettings.cs, like SettingsTests.
/// </summary>
[TestFixture]
public class ServerOnlySettingsTests
{
    static readonly string Src = File.ReadAllText(Path.Combine(TestPaths.Src, "Configuration", "ModSettings.cs"));

    // [section, key] -> the Bind helper used for it
    static Dictionary<string, string> Binds()
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Src, @"(?<how>SyncedConfig\.BindLock|SyncedConfig\.Bind|AdminOnly\.Bind|c\.Bind)\(\s*""(?<s>[^""]+)""\s*,\s*""(?<k>[^""]+)"""))
            d[m.Groups["s"].Value + "/" + m.Groups["k"].Value] = m.Groups["how"].Value;
        return d;
    }

    [Test]
    public void ServerOnlyWebhookUrls_AreAdminOnly_NeverSynced()
    {
        var binds = Binds();
        foreach (var id in new[] { "Chat/Server - Webhook URL", "Chat/Server - Normal Webhook URL", "Chat/Server - Shout Webhook URL",
                                   "Server Notifications/Server - Webhook URL", "Player Notifications/Server - Webhook URL", "Weekly Report/Server - Webhook URL" })
            Assert.That(binds[id], Is.EqualTo("AdminOnly.Bind"), id + " is a private webhook: it must not reach ordinary players");
    }

    [Test]
    public void UploadWebhookUrls_AreSynced_BecauseEachGameUploadsItself()
    {
        var binds = Binds();
        foreach (var id in new[] { "Screenshots/Server - Webhook URL", "Player Deaths/Server - Webhook URL", "Boss Death/Server - Webhook URL", "Clips/Server - Webhook URL" })
            Assert.That(binds[id], Is.EqualTo("SyncedConfig.Bind"), id);
    }

    [Test]
    public void EveryWebhookUrl_IsAccountedFor()
    {
        int urls = 0;
        foreach (var id in Binds().Keys) if (id.EndsWith("/Server - Webhook URL") || id.EndsWith("Normal Webhook URL") || id.EndsWith("Shout Webhook URL")) urls++;

        Assert.That(urls, Is.EqualTo(10), "a new webhook URL setting must be added to the two lists above (private = AdminOnly, upload = synced)");
    }

    [Test]
    public void ClientSettings_StayLocal_NeverSyncedOrAdminOnly()
    {
        foreach (var kv in Binds())
            if (kv.Key.Contains("/Client - "))
                Assert.That(kv.Value, Is.EqualTo("c.Bind"), kv.Key + " is each player's own setting");
    }

    [Test]
    public void AdminChannel_RefusesNonAdmins_AndTheLockEntryIsOneOnly()
    {
        var channel = File.ReadAllText(Path.Combine(TestPaths.ModDir, "src", "Shared", "Server", "AdminSettingsChannel.cs"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(channel, Does.Contain("IsAdminPeer(peer)"), "a request must be checked against the adminlist");
            Assert.That(Regex.Matches(Src, @"SyncedConfig\.BindLock"), Has.Count.EqualTo(1), "ServerSync allows exactly one locking entry");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimDiscordRelay.Server
{
    /// <summary>
    /// Collects per-player boss damage reported by the game client that owns the boss, and posts the
    /// "{Boss} has fallen!" embed to the [Boss Death] webhook when the death report arrives.
    ///
    /// Valheim does not keep per-player damage totals, so the boss owner's client reports each player's
    /// damage in small batches (BossDamage RPC) and then the death (BossDeath RPC). The server aggregates by
    /// boss ZDOID, so the totals survive the boss changing owner during the fight.
    /// </summary>
    internal static class BossAnnouncer
    {
        private sealed class Fight
        {
            public string Prefab;
            public readonly Dictionary<string, float> Damage = new(StringComparer.Ordinal);
            public DateTime LastUpdateUtc;
        }

        private const int MaxFights = 32;
        private const int MaxPlayersPerFight = 64;
        private const int MaxEntriesPerMessage = 64;
        private const int MaxNameChars = 64;
        private const float MaxDamagePerEntry = 100000000f;
        private const int MaxAnnouncedRemembered = 64;
        private static readonly TimeSpan StaleFightAge = TimeSpan.FromHours(3);

        private static readonly Dictionary<string, Fight> Fights = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Announced = new(StringComparer.Ordinal);
        private static readonly Queue<string> AnnouncedOrder = new();
        private static readonly object Sync = new();

        internal static string MakeKey(long user, long id)
        {
            return user.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
        }

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RelayProtocol.BossDamageRpcName, OnBossDamageRpc);
            rpc.Register<ZPackage>(RelayProtocol.BossDeathRpcName, OnBossDeathRpc);
            RelayDiagnostics.Info("Registered RPC " + RelayProtocol.BossDamageRpcName);
            RelayDiagnostics.Info("Registered RPC " + RelayProtocol.BossDeathRpcName);
        }

        // ---------------------------------------------------------------- RPC entry points

        private static void OnBossDamageRpc(long sender, ZPackage package)
        {
            if (!BossConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                if (ZNet.instance.GetPeer(sender) == null)
                {
                    RelayDiagnostics.Warning("Rejected boss damage report from unknown peer " + sender + ".");
                    return;
                }

                if (package.ReadInt() != RelayProtocol.ProtocolVersion)
                    return;

                long user = package.ReadLong();
                long id = package.ReadLong();
                string prefab = RelayProtocol.ReadSafeString(package, MaxNameChars);
                int count = package.ReadInt();

                if (count < 0 || count > MaxEntriesPerMessage)
                {
                    RelayDiagnostics.Warning("Rejected boss damage report from " + sender + ": bad entry count " + count + ".");
                    return;
                }

                string key = MakeKey(user, id);
                for (int i = 0; i < count; i++)
                {
                    string player = RelayProtocol.ReadSafeString(package, MaxNameChars);
                    float damage = package.ReadSingle();
                    AddDamage(key, prefab, player, damage);
                }
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to process boss damage report.", ex);
            }
        }

        private static void OnBossDeathRpc(long sender, ZPackage package)
        {
            if (!BossConfig.Enabled.Value)
                return;

            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return;

                if (ZNet.instance.GetPeer(sender) == null)
                {
                    RelayDiagnostics.Warning("Rejected boss death report from unknown peer " + sender + ".");
                    return;
                }

                if (package.ReadInt() != RelayProtocol.ProtocolVersion)
                    return;

                long user = package.ReadLong();
                long id = package.ReadLong();
                string prefab = RelayProtocol.ReadSafeString(package, MaxNameChars);
                string hoverName = RelayProtocol.ReadSafeString(package, MaxNameChars);

                HandleDeath(MakeKey(user, id), prefab, hoverName);
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Error("Failed to process boss death report.", ex);
            }
        }

        // ---------------------------------------------------------------- shared logic (RPC path and same-process path)

        /// <summary>
        /// Adds damage dealt by one player to one boss fight. Ignores anything implausible.
        /// </summary>
        internal static void AddDamage(string key, string prefab, string player, float damage)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(player))
                return;

            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f)
                return;

            if (damage > MaxDamagePerEntry)
                damage = MaxDamagePerEntry;

            player = BossCatalog.StripTags(player);
            if (player.Length == 0)
                return;

            lock (Sync)
            {
                if (Announced.Contains(key))
                    return;

                PurgeStaleFightsLocked();

                Fight fight;
                if (!Fights.TryGetValue(key, out fight))
                {
                    if (Fights.Count >= MaxFights)
                        return;

                    fight = new Fight();
                    Fights[key] = fight;
                }

                fight.Prefab = prefab;
                fight.LastUpdateUtc = DateTime.UtcNow;

                float existing;
                if (fight.Damage.TryGetValue(player, out existing))
                {
                    fight.Damage[player] = Math.Min(MaxDamagePerEntry * 10f, existing + damage);
                }
                else if (fight.Damage.Count < MaxPlayersPerFight)
                {
                    fight.Damage[player] = damage;
                }
            }
        }

        /// <summary>
        /// Builds and queues the death embed for a boss, using the damage collected so far.
        /// </summary>
        /// <param name="key">Boss identity from MakeKey.</param>
        /// <param name="prefab">Boss prefab name.</param>
        /// <param name="hoverName">The game's display name, used only when the boss is not in the catalog.</param>
        internal static void HandleDeath(string key, string prefab, string hoverName)
        {
            if (!BossConfig.Enabled.Value || string.IsNullOrWhiteSpace(key))
                return;

            // A non-final phase of a multi-phase boss (Kall Fimbulbringer) is not a kill. Its damage stays in Fights
            // and is merged into the announcement of the final phase. Also guards against clients that still report it.
            if (BossCatalog.IsIntermediatePhase(prefab))
            {
                RelayDiagnostics.Info("Boss phase ended (prefab '" + prefab + "'); waiting for the final phase before announcing.");
                return;
            }

            Dictionary<string, float> merged = new Dictionary<string, float>(StringComparer.Ordinal);

            lock (Sync)
            {
                if (!Announced.Add(key))
                    return;

                AnnouncedOrder.Enqueue(key);
                while (AnnouncedOrder.Count > MaxAnnouncedRemembered)
                    Announced.Remove(AnnouncedOrder.Dequeue());

                Fight fight;
                if (Fights.TryGetValue(key, out fight))
                {
                    MergeDamage(merged, fight.Damage);
                    Fights.Remove(key);
                }

                // Pull in the damage dealt in the earlier phases of the same boss (each phase is a separate object).
                List<string> sameBoss = null;
                foreach (KeyValuePair<string, Fight> other in Fights)
                {
                    if (BossCatalog.IsSamePhaseFamily(other.Value.Prefab, prefab))
                    {
                        if (sameBoss == null)
                            sameBoss = new List<string>();
                        sameBoss.Add(other.Key);
                    }
                }

                if (sameBoss != null)
                {
                    foreach (string otherKey in sameBoss)
                    {
                        MergeDamage(merged, Fights[otherKey].Damage);
                        Fights.Remove(otherKey);
                    }
                }
            }

            List<KeyValuePair<string, float>> players = new List<KeyValuePair<string, float>>(merged);

            string url = BossConfig.WebhookUrl.Value == null ? string.Empty : BossConfig.WebhookUrl.Value.Trim();
            if (url.Length == 0)
            {
                RelayDiagnostics.Warning("Boss death detected ('" + prefab + "'), but [Boss Death] Server - Webhook URL is empty on the server; nothing was sent.");
                return;
            }

            BossInfo info;
            bool known = BossCatalog.TryGet(prefab, out info);

            string bossName;
            if (known)
            {
                bossName = info.DisplayName;
            }
            else
            {
                string fallback = BossCatalog.StripTags(hoverName);
                if (fallback.Length == 0)
                    fallback = BossCatalog.CleanPrefabName(prefab);
                if (fallback.Length == 0)
                    fallback = "Boss";
                bossName = BossCatalog.CapitalizeFirst(fallback);

                RelayDiagnostics.Warning("Boss death for prefab '" + prefab + "' is not in the boss catalog; sending the announcement without images.");
            }

            string description = BuildDescription(bossName, players);
            string username = ServerRelay.LimitUsername(ServerRelay.SanitizeUsername(BossConfig.WebhookUsername.Value));

            string json = DiscordFormatting.BuildEmbedWebhookJson(
                username,
                known ? info.AvatarUrl : null,
                bossName + " has fallen!",
                description,
                known ? info.AvatarUrl : null,
                known ? BossCatalog.CenteredPhotoUrl(info.PhotoUrl) : null);

            RelayDiagnostics.Info("Boss death: prefab='" + prefab + "', name='" + bossName + "', damage dealers=" + players.Count + ".");

            ServerRelay.EnqueueJson(url, json, "boss death (" + bossName + ")");
        }

        private static void MergeDamage(Dictionary<string, float> target, Dictionary<string, float> source)
        {
            foreach (KeyValuePair<string, float> entry in source)
            {
                float existing;
                target.TryGetValue(entry.Key, out existing);
                target[entry.Key] = Math.Min(MaxDamagePerEntry * 10f, existing + entry.Value);
            }
        }

        /// <summary>
        /// Party (2+ players dealt damage): a random party quote naming the top damage dealer.
        /// Solo (1 player): a random solo quote with that player's damage.
        /// No recorded damage: plain fallback.
        /// The quotes themselves live in BossQuotes.cs.
        /// </summary>
        private static string BuildDescription(string bossName, List<KeyValuePair<string, float>> players)
        {
            string boss = BossCatalog.EscapeDiscordMarkdown(BossCatalog.CapitalizeFirst(bossName));

            if (players.Count >= 2)
            {
                KeyValuePair<string, float> top = players[0];
                for (int i = 1; i < players.Count; i++)
                {
                    if (players[i].Value > top.Value)
                        top = players[i];
                }

                string name = BossCatalog.EscapeDiscordMarkdown(BossCatalog.CapitalizeFirst(top.Key));
                return BossQuotes.PickParty(boss, name, FormatDamage(top.Value));
            }

            if (players.Count == 1)
                return BossQuotes.PickSolo(boss, players[0].Key, FormatDamage(players[0].Value));

            return boss + " has been defeated.";
        }

        private static string FormatDamage(float damage)
        {
            return Math.Round(damage).ToString("N0", CultureInfo.InvariantCulture);
        }

        private static void PurgeStaleFightsLocked()
        {
            if (Fights.Count == 0)
                return;

            DateTime cutoff = DateTime.UtcNow - StaleFightAge;
            List<string> stale = null;

            foreach (KeyValuePair<string, Fight> pair in Fights)
            {
                if (pair.Value.LastUpdateUtc < cutoff)
                {
                    if (stale == null)
                        stale = new List<string>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
                return;

            foreach (string key in stale)
                Fights.Remove(key);
        }
    }
}

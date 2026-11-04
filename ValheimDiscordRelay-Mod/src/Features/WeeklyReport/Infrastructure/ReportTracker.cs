using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report;

/// <summary>
/// Measures what the weekly report needs, in the game that has the authority to see it:
///
///  - Damage dealt to creatures, and mob kills: Valheim only applies damage on the game that OWNS the creature, so that
///    game measures how much health each player's hits removed (after resistances) and who landed the killing blow.
///  - Boss kills: the same measurement, reported as its own event (the final phase only, for multi-phase bosses).
///  - The local player's own deaths.
///
/// Damage and mob kills are batched and sent to the server every few seconds; boss kills and deaths are sent at once.
/// When this game IS the server (dedicated server, or the host of a hosted world) the numbers go straight to ReportStats.
///
/// A player's highest hit is batched too, and only sent when it beats the best one this game already sent for that
/// player (see QueueHighestHit); the server tells every game when a new statistics period starts so that memory is cleared.
///
/// The best hit each player lands on each kind of mob (never a boss) is batched the same way (see ReportMobHit): only a hit that
/// beats the one this game already sent for that player and kind of mob goes out, at most every five seconds.
///
/// Names (mobs, killers, enemies, weapons) are English: this game resolves them when it can, and the server finishes the
/// job (see EnglishNames), so a report never shows a prefab name such as "Greydwarf_Elite" or another player's language.
///
/// What counts: hits by a player on a creature that is not a player, not tamed, and (for the mob-kill number) not a boss.
/// Creatures killed by a tamed animal, by another creature or by the environment are not credited to anyone.
/// Like boss damage, it is only measured when the game that owns the creature runs this mod.
/// </summary>
internal static partial class ReportTracker
{

    private const float FlushIntervalSeconds = 5f;
    private const int MaxPendingPlayers = 32;
    private const int MaxRememberedBestHits = 64;

    // One entry per (player, kind of mob): a handful of players times the creatures of the game.
    private const int MaxPendingMobHits = 64;
    private const int MaxRememberedMobHits = 512;

    /// <summary>The weapon of a player with nothing in their hands (the game's own name for the fists).</summary>
    private const string UnarmedKey = "$item_unarmed";

    /// <summary>The cause of death recorded when another player landed the killing blow.</summary>
    private const string PlayerKillCause = "Killed by a player";

    private static HitState _current;
    private static readonly Dictionary<string, PendingActivity> Pending = new(StringComparer.Ordinal);

    // Highest hits. SentBestHit remembers, per player, the best hit this game has sent to the server in this session and
    // statistics period: a hit that does not beat it cannot change the server's record, so it is never sent. It is cleared
    // when the server says a new period started (OnPeriodReset) and when a new connection begins (ResetBestHitMemory).
    private static readonly Dictionary<string, PendingHit> PendingHits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> SentBestHit = new(StringComparer.OrdinalIgnoreCase);
    private static float _nextFlushTime;

    // The best hit each boss received, by BossCatalog.InstanceKey. Same idea as the highest hits above: SentBossBest is what
    // this game has already told the server, so a weaker hit is never sent. Unlike the players' highest hits it is NOT
    // cleared when a statistics period starts: the server keeps the fights of bosses that are still alive across the
    // period change (see ReportStats), so this game must not forget what it sent for them. It is cleared on a new connection.
    private static readonly Dictionary<string, PendingBossHit> PendingBossHits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> SentBossBest = new(StringComparer.Ordinal);

    // The best hit each player landed on each kind of mob (never a boss), by player + prefab (MobHitKey). Same idea as the
    // highest hits above: SentMobBest is what this game has already told the server, so a weaker hit is never sent. Cleared
    // when the server starts a new statistics period and when a new connection begins.
    private static readonly Dictionary<string, PendingMobHit> PendingMobHits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> SentMobBest = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- hooks called by the Harmony patches

    /// <summary>
    /// Called before Character.RPC_Damage: remembers the creature's health so the damage can be measured afterwards.
    /// Does nothing unless a player is hitting a creature this game owns.
    /// </summary>
    internal static void BeginDamage(Character character, HitData hit)
    {
        _current = null;

        if (character == null || character is Player || !Collecting())
            return;

        try
        {
            if (!character.IsOwner() || character.IsDead() || character.IsTamed())
                return;
        }
        catch
        {
            return;
        }

        string attacker = BossKillTracker.ResolvePlayerAttacker(hit);
        if (attacker == null)
            return;

        HitState state = new HitState
        {
            Target = character,
            Attacker = attacker,
            HealthBefore = character.GetHealth(),
            Frame = Time.frameCount,
            Weapon = ResolveHitWeapon(hit),
            Hit = hit
        };

        try { state.IsBoss = character.IsBoss(); }
        catch { state.IsBoss = false; }

        // Read everything about the creature now: killing it can destroy the object before the postfix runs.
        try { state.Prefab = BossCatalog.CleanPrefabName(character.gameObject.name); }
        catch { state.Prefab = BossCatalog.CleanPrefabName(character.name); }

        if (state.IsBoss)
        {
            long user;
            long id;
            string prefab;
            if (BossKillTracker.TryGetIdentity(character, out state.Key, out user, out id, out prefab))
            {
                state.Prefab = prefab;
                state.InstanceKey = BossCatalog.InstanceKey(prefab, state.Key);
            }
            else
            {
                state.Key = null;
                state.InstanceKey = null;
            }
        }

        state.Enemy = ResolveEnemyName(character, state.Prefab);

        _current = state;
    }

    /// <summary>
    /// The creature's name for the report. A known boss uses its catalogue name; anything else is the creature's name key
    /// (Character.m_name), turned into English text when this game can, otherwise passed on for the server to finish.
    /// Unlike GetHoverName it does not depend on the language of this game.
    /// </summary>
    private static string ResolveEnemyName(Character character, string prefab)
    {
        try
        {
            BossInfo boss;
            if (BossCatalog.TryGet(prefab, out boss))
                return boss.DisplayName;

            return EnglishNames.CreatureFromGame(character.m_name, prefab);
        }
        catch
        {
            return EnglishNames.CreatureFromGame(null, prefab);
        }
    }

    /// <summary>
    /// Called after Character.RPC_Damage: credits the damage if the creature survived the hit.
    /// </summary>
    internal static void EndDamage(Character character)
    {
        HitState state = _current;
        _current = null;

        if (state == null || state.Finalized || !ReferenceEquals(state.Target, character))
            return;

        state.Finalized = true;

        // A creature that died was already handled by OnNativeDeath; one that vanished some other way is not credited.
        if (state.Target == null)
            return;

        Process(state, false);
    }

    /// <summary>
    /// Called when a creature is about to die. For a creature killed by the hit being applied right now this credits
    /// the damage and the kill (the object may be destroyed before RPC_Damage's postfix runs).
    /// </summary>
    internal static void OnNativeDeath(Character character)
    {
        if (character == null || character is Player)
            return;

        HitState state = _current;
        if (state == null || state.Finalized || !ReferenceEquals(state.Target, character) || state.Frame != Time.frameCount)
            return;

        state.Finalized = true;
        Process(state, true);
    }

    /// <summary>
    /// Called when the local player has died (once per death).
    /// </summary>
    /// <param name="death">What is known about the death (see <see cref="DeathReport"/>).</param>
    internal static void OnLocalPlayerDeath(DeathReport death)
    {
        string playerName = death.PlayerName, killerPrefab = death.KillerPrefab, killerNameKey = death.KillerNameKey, deathText = death.DeathText;
        bool killedByPlayer = death.KilledByPlayer;
        string killerObjectKey = death.KillerObjectKey, finishingType = death.FinishingType, environmentCause = death.EnvironmentCause;
        float finishingDamage = death.FinishingDamage;

        try
        {
            if (!Collecting())
                return;

            if (killedByPlayer)
            {
                killerPrefab = null;
                killerObjectKey = null;
            }

            string killerName = string.IsNullOrWhiteSpace(killerPrefab) ? string.Empty : EnglishNames.CreatureFromGame(killerNameKey, killerPrefab);
            string cause = killedByPlayer ? PlayerKillCause : ResolveDeathCause(killerName, deathText, environmentCause, playerName);

            RelayDiagnostics.Info("[Report] Death reported: player='" + playerName + "', cause='" + cause + "', killer prefab='" + (killerPrefab ?? string.Empty) +
                                  "', finishing hit=" + (finishingDamage > 0f ? Math.Round(finishingDamage).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (finishingType ?? string.Empty) : "unknown") + ".");

            if (IsServerProcess())
            {
                ReportStats.RecordDeath(playerName);
                ReportStats.RecordMobDeath(playerName, killerPrefab, killerName);
                ReportStats.RecordDeathCause(playerName, cause, killerPrefab);
                ReportStats.RecordDeathHit(playerName, cause, killerPrefab, finishingDamage, finishingType);

                if (!string.IsNullOrWhiteSpace(killerObjectKey))
                    ReportStats.RecordBossPlayerKill(killerPrefab, killerObjectKey);

                return;
            }

            // The last field is the killer's object key for a boss death (empty otherwise); a server of an older build ignores it.
            SendEvent(ReportProtocol.EventPlayerDeath, killerPrefab ?? string.Empty, cause, killerObjectKey ?? string.Empty);

            // The finishing hit of that death, as its own event (a server of an older build ignores it). The cause is sent exactly as
            // above, so the server files the hit under the same cause.
            if (finishingDamage > 0f)
                SendDeathHit(cause, finishingType, finishingDamage, killerPrefab);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("[Report] Could not report the player's death.", ex);
        }
    }

    /// <summary>
    /// The cause of death for the report: the killer's name (already English, or a key for the server to finish), or for a death
    /// without a killer the environmental cause the game's hit type gave ("Drowning", "Fall damage"...). Only when neither is
    /// known is the death text searched for a keyword, as a last resort (with the player's own name taken out of it first, so a
    /// player called "Firebeard" cannot turn a drowning into a fire death).
    /// </summary>
    private static string ResolveDeathCause(string killerName, string deathText, string environmentCause, string playerName)
    {
        string mob = BossCatalog.StripTags(killerName ?? string.Empty).Trim();
        if (mob.Length > 0)
            return BossCatalog.CapitalizeFirst(mob);

        string environment = BossCatalog.StripTags(environmentCause ?? string.Empty).Trim();
        if (environment.Length > 0)
            return BossCatalog.CapitalizeFirst(environment);

        string text = deathText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(playerName))
            text = text.Replace(playerName, " ");
        text = text.ToLowerInvariant();

        if (text.Contains("drown")) return "Drowning";
        if (text.Contains("tree")) return "Falling tree";
        if (text.Contains("fall") || text.Contains("fell")) return "Fall damage";
        if (text.Contains("poison")) return "Poison";
        if (text.Contains("burn") || text.Contains("fire")) return "Burning";
        if (text.Contains("smoke")) return "Smoke";
        if (text.Contains("freez") || text.Contains("frost")) return "Freezing";
        if (text.Contains("starv")) return "Starvation";
        if (text.Contains("impact")) return "Impact damage";
        return "Unknown / environment";
    }

    /// <summary>
    /// Sends the batched damage / kills / highest hits to the server about every five seconds. A damage / kills batch is
    /// only taken out of the queue when there is a server connection to send it on.
    /// </summary>
    internal static void Tick()
    {
        if (Pending.Count == 0 && PendingHits.Count == 0 && PendingBossHits.Count == 0 && PendingMobHits.Count == 0)
            return;

        float now = Time.unscaledTime;
        if (now < _nextFlushTime)
            return;

        _nextFlushTime = now + FlushIntervalSeconds;
        FlushNow();
    }

    /// <summary>
    /// Sends whatever is still batched right away (used when the game session is being torn down).
    /// </summary>
    internal static void FlushNow()
    {
        FlushActivity();
        FlushHighestHits();
        FlushBossHits();
        FlushMobHits();
    }

    /// <summary>
    /// The server started a new statistics period (see ReportWindowRpc): every player's highest hit starts from zero
    /// again, so the hits this game remembered sending must not hold back the first hits of the new period.
    /// </summary>
    internal static void OnPeriodReset()
    {
        SentBestHit.Clear();
        PendingHits.Clear();
        SentMobBest.Clear();
        PendingMobHits.Clear();
        RelayDiagnostics.Debug("[Report] New statistics period: forgot the highest hits sent so far.");
    }

    /// <summary>
    /// A new connection to a server begins: nothing is known about what that server has recorded.
    /// </summary>
    internal static void ResetBestHitMemory()
    {
        SentBestHit.Clear();
        PendingHits.Clear();
        SentBossBest.Clear();
        PendingBossHits.Clear();
        SentMobBest.Clear();
        PendingMobHits.Clear();

        // Damage and kills that could not be sent on the previous connection belong to that server's statistics.
        Pending.Clear();
    }

    // ---------------------------------------------------------------- internals

    private static bool Collecting()
    {
        return ReportConfig.On &&
               ReportConfig.ClientOn;
    }

    private static bool IsServerProcess()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    /// <summary>
    /// Credits the health this hit removed to the attacking player, and the kill if the creature died.
    /// </summary>
    /// <param name="state">The hit being applied.</param>
    /// <param name="dying">True when called from the OnDeath patch, i.e. the creature is certainly dying.</param>
    private static void Process(HitState state, bool dying)
    {
        try
        {
            float healthAfter = 0f;
            try { healthAfter = state.Target.GetHealth(); }
            catch { healthAfter = 0f; }

            float dealt = Mathf.Max(0f, state.HealthBefore - Mathf.Max(0f, healthAfter));
            bool died = dying || healthAfter <= 0f;

            // The size of the hit, for the "highest hit" numbers. For a hit that did not kill, it is the health removed. A killing
            // blow can only remove what the creature had left (a 200 damage hit on a creature with 30 health left removes 30), so
            // for a kill the full damage of the hit counts, like the damage number the player sees. Total Damage keeps counting
            // the health really removed.
            float hitSize = dealt;
            if (died)
                hitSize = Mathf.Max(dealt, FullHitDamage(state));

            // Before the kill is reported: the server then has the boss's best hit when it records the kill.
            if (state.IsBoss && hitSize > 0f)
                ReportBossHit(state, hitSize);
            else if (!state.IsBoss && hitSize > 0f)
                ReportMobHit(state, hitSize);

            int mobKills = 0;
            if (died)
            {
                if (state.IsBoss)
                    ReportBossKill(state);
                else
                {
                    mobKills = 1;
                    ReportMobKill(state.Attacker, state.Prefab, state.Enemy);
                }
            }

            if (hitSize > 0f)
            {
                if (IsServerProcess())
                    ReportStats.RecordHighestHit(state.Attacker, hitSize, state.Weapon, state.Enemy);
                else
                    QueueHighestHit(state.Attacker, hitSize, state.Weapon, state.Enemy);
            }

            AddActivity(state.Attacker, dealt, mobKills);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Error("[Report] Could not track damage.", ex);
        }
    }

    /// <summary>
    /// The total damage of the hit after the game applied its modifiers (0 when it cannot be read). Only meaningful once the
    /// creature has been hit; see HitState.Hit.
    /// </summary>
    private static float FullHitDamage(HitState state)
    {
        try
        {
            if (state.Hit == null)
                return 0f;

            float total = state.Hit.GetTotalDamage();
            return float.IsNaN(total) || float.IsInfinity(total) || total < 0f ? 0f : total;
        }
        catch
        {
            return 0f;
        }
    }

    private static void AddActivity(string player, float damage, int mobKills)
    {
        if (damage <= 0f && mobKills == 0)
            return;

        if (IsServerProcess())
        {
            ReportStats.RecordActivity(player, damage, mobKills);
            return;
        }

        PendingActivity pending;
        if (!Pending.TryGetValue(player, out pending))
        {
            if (Pending.Count >= MaxPendingPlayers)
                return;

            pending = new PendingActivity();
            Pending[player] = pending;
        }

        pending.Damage += damage;
        pending.Kills += mobKills;
    }

    /// <summary>
    /// Remembers a player's hit if it can matter: only a hit above the best one this game has already sent for that
    /// player can change the server's record, and of several such hits within one flush interval only the best is kept.
    /// The event goes out at the next flush (see FlushHighestHits), so a long fight costs at most one small message every
    /// few seconds, and none at all once the player's best hit is known to the server.
    /// </summary>
    private static void QueueHighestHit(string player, float damage, string weapon, string enemy)
    {
        if (string.IsNullOrEmpty(player) || damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage))
            return;

        float sentBest;
        if (SentBestHit.TryGetValue(player, out sentBest) && damage <= sentBest)
            return;

        PendingHit pending;
        if (!PendingHits.TryGetValue(player, out pending))
        {
            if (PendingHits.Count >= MaxPendingPlayers)
                return;

            pending = new PendingHit();
            PendingHits[player] = pending;
        }

        if (damage <= pending.Damage)
            return;

        pending.Damage = damage;
        pending.Weapon = weapon;
        pending.Enemy = enemy;
    }

}

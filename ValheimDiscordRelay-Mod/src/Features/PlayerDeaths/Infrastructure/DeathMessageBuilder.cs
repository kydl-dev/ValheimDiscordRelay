using System;
using System.Reflection;
using UnityEngine;

namespace ValheimDiscordRelay.Client;

/// <summary>
/// The two renderings of one death message: plain/rich text for in-game chat, and Discord markdown.
/// </summary>
internal sealed class DeathMessage
{
    /// <summary>Text for the in-game shout (Unity rich text allowed, no emoji/markdown).</summary>
    internal string Game;

    /// <summary>Text for the Discord webhook (markdown, emoji stars and ANSI blocks allowed).</summary>
    internal string Discord;

    /// <summary>
    /// The creature or player blamed for the death (the same one the message names), or null for environmental
    /// and unknown deaths. Used by the death camera to turn towards the killer.
    /// </summary>
    internal AttackerInfo Killer;

    /// <summary>
    /// The total damage of the hit that killed the player (the finishing hit), or 0 when it is unknown. Set by
    /// <see cref="DeathMessageBuilder.AttachFinishingHit"/>; used by the weekly report's Death causes tab.
    /// </summary>
    internal float FinishingDamage;

    /// <summary>The damage type that made up most of the finishing hit ("Slash", "Fire"...); null/empty for plain damage or unknown.</summary>
    internal string FinishingType;

    /// <summary>
    /// The English name of an environmental cause of death ("Drowning", "Fall damage", "Falling tree"...), taken from the type of
    /// the killing hit, never from the wording of the message. Null when a creature or player is blamed (the Killer names the
    /// cause then) or when the cause is not known. Used by the weekly report's Death causes tab.
    /// </summary>
    internal string CauseName;

    internal DeathMessage(string game, string discord)
    {
        Game = game;
        Discord = discord;
    }

    internal static DeathMessage Same(string text)
    {
        return new DeathMessage(text, text);
    }
}

/// <summary>
/// Builds the human-readable death-cause sentence for the in-game shout and Discord message.
/// </summary>
internal static class DeathMessageBuilder
{
    private const string StarGlyph = "\u2B50"; // ⭐
    private const string Esc = "\u001b";

    /// <summary>Discord ANSI code used for coloured (tagged) enemy names: dim red.</summary>
    private const string AnsiColorStart = Esc + "[2;31m";
    private const string AnsiReset = Esc + "[0m";

    /// <summary>
    /// How long after the last attributed hit a damage-over-time death (burning, poison) is still
    /// blamed on that attacker. Only used when the killing tick itself carries no attacker.
    /// </summary>
    private const float DamageOverTimeAttributionSeconds = 5f;

    /// <summary>
    /// Used when the killing hit names an attacker whose Character is already gone and nothing
    /// better is known (attacker id matches the snapshot).
    /// </summary>
    private const float SnapshotMaxAgeSeconds = 60f;

    /// <summary>
    /// Used when no hit information exists at all.
    /// </summary>
    private const float NoHitSnapshotMaxAgeSeconds = 10f;

    private static FieldInfo _lastHitField;
    private static bool _lastHitFieldSearched;

    /// <summary>
    /// Resolves the most likely death-cause message for the local player.
    /// </summary>
    /// <param name="fallbackHit">The mod's own last-recorded hit, used only if Valheim's own record is unavailable.</param>
    /// <param name="playerName">The local player's display name.</param>
    /// <param name="recentAttacker">Snapshot of the last creature that hit the player, captured at hit time.</param>
    /// <returns>The death message in in-game and Discord form.</returns>
    internal static DeathMessage GetDeathMessage(HitData fallbackHit, string playerName, AttackerInfo recentAttacker)
    {
        string player = string.IsNullOrWhiteSpace(playerName) ? "Valheim Player" : playerName.Replace("`", "'");

        try
        {
            Player playerObject = Player.m_localPlayer;
            HitData hit = GetNativeLastHit(playerObject) ?? fallbackHit;

            string source;
            AttackerInfo attacker = ResolveAttacker(hit, recentAttacker, playerObject, out source);

            RelayDiagnostics.Debug(
                "[Death] hitType=" + (hit == null ? "none" : hit.m_hitType.ToString()) +
                ", haveAttacker=" + (hit != null && hit.HaveAttacker()) +
                ", attacker=" + (attacker == null ? "none" : "'" + attacker.Name + "' L" + attacker.Level +
                                 " prefab=" + (attacker.PrefabName ?? "?") +
                                 (attacker.IsAnimal ? " animal" : string.Empty) +
                                 (attacker.IsBoss ? " boss" : string.Empty)) +
                ", source=" + source + ".");

            if (attacker != null)
            {
                DeathMessage built = attacker.IsBoss
                    ? BuildBossMessage(player, attacker)
                    : BuildCreatureMessage(player, attacker);

                if (built != null)
                    built.Killer = attacker;

                return built;
            }

            string environmental = ResolveEnvironmentalDeath(hit, player);
            if (!string.IsNullOrWhiteSpace(environmental))
            {
                DeathMessage environmentalMessage = DeathMessage.Same(environmental);
                environmentalMessage.CauseName = EnvironmentalCauseName(hit.m_hitType);
                return environmentalMessage;
            }

            if (playerObject != null)
            {
                FieldInfo deathMessageField = typeof(Player).GetField(
                    "m_deathMessage",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (deathMessageField != null)
                {
                    string message = deathMessageField.GetValue(playerObject) as string;
                    if (!string.IsNullOrWhiteSpace(message))
                        return DeathMessage.Same(player + " " + SanitizePlainText(message));
                }
            }
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not resolve death cause: " + ex.Message);
        }

        return DeathMessage.Same(player + " got killed by Unknown.");
    }

    /// <summary>
    /// Picks the attacker to blame, trying (in order): the live attacker on the killing hit, the
    /// hit-time snapshot when that attacker has since despawned, and - for damage-over-time deaths
    /// whose killing tick carries no attacker - the most recent creature that hit the player.
    /// </summary>
    private static AttackerInfo ResolveAttacker(
        HitData hit, AttackerInfo snapshot, Player playerObject, out string source)
    {
        float now = Time.unscaledTime;
        bool snapshotUsable = snapshot != null;

        if (hit != null && hit.HaveAttacker())
        {
            Character live = null;
            try
            {
                if (ZNetScene.instance != null)
                    live = hit.GetAttacker();
            }
            catch (Exception ex)
            {
                RelayDiagnostics.Debug("Could not resolve live attacker: " + ex.Message);
            }

            if (live != null && !ReferenceEquals(live, playerObject))
            {
                AttackerInfo info = AttackerInfo.FromCharacter(live, hit.m_attacker);
                if (info != null)
                {
                    source = "live";
                    return info;
                }
            }

            // The attacker is known by id, but its Character is gone (killed, despawned, out of range).
            if (snapshotUsable &&
                snapshot.AttackerId == hit.m_attacker &&
                now - snapshot.CapturedAt <= SnapshotMaxAgeSeconds)
            {
                source = "snapshot (attacker gone)";
                return snapshot;
            }

            source = "attacker id set but unresolved";
            return null;
        }

        if (hit == null)
        {
            if (snapshotUsable && now - snapshot.CapturedAt <= NoHitSnapshotMaxAgeSeconds)
            {
                source = "snapshot (no hit data)";
                return snapshot;
            }

            source = "none";
            return null;
        }

        // Killing hit has no attacker. Damage-over-time ticks (burning, poison) commonly look like this
        // even when the fire/poison was applied by a creature.
        if (IsDamageOverTime(hit.m_hitType) &&
            snapshotUsable &&
            now - snapshot.CapturedAt <= DamageOverTimeAttributionSeconds)
        {
            source = "snapshot (damage over time)";
            return snapshot;
        }

        source = "no attacker on hit";
        return null;
    }

    /// <summary>
    /// Records on <paramref name="message"/> how hard the hit that killed the local player was and what kind of damage it was.
    /// Uses the game's own last hit (final once the player is dead), or the mod's own record when the game's is unavailable.
    /// Does nothing when there is no hit to read.
    /// </summary>
    internal static void AttachFinishingHit(DeathMessage message, HitData fallbackHit)
    {
        if (message == null)
            return;

        try
        {
            HitData hit = GetNativeLastHit(Player.m_localPlayer) ?? fallbackHit;
            if (hit == null)
                return;

            float total = hit.GetTotalDamage();
            if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0f)
                return;

            message.FinishingDamage = total;
            message.FinishingType = DominantDamageType(hit);
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read the finishing hit: " + ex.Message);
        }
    }

    /// <summary>
    /// The damage type that makes up most of the hit, in English. Empty when plain (generic) damage dominates, which is what
    /// falling, drowning and similar environmental hits do: their cause already says what hurt.
    /// </summary>
    private static string DominantDamageType(HitData hit)
    {
        HitData.DamageTypes d = hit.m_damage;

        float best = 0f;
        string type = string.Empty;
        PickLarger(ref best, ref type, d.m_blunt, "Blunt");
        PickLarger(ref best, ref type, d.m_slash, "Slash");
        PickLarger(ref best, ref type, d.m_pierce, "Pierce");
        PickLarger(ref best, ref type, d.m_chop, "Chop");
        PickLarger(ref best, ref type, d.m_pickaxe, "Pickaxe");
        PickLarger(ref best, ref type, d.m_fire, "Fire");
        PickLarger(ref best, ref type, d.m_frost, "Frost");
        PickLarger(ref best, ref type, d.m_lightning, "Lightning");
        PickLarger(ref best, ref type, d.m_poison, "Poison");
        PickLarger(ref best, ref type, d.m_spirit, "Spirit");

        return d.m_damage > best ? string.Empty : type;
    }

    private static void PickLarger(ref float best, ref string type, float amount, string name)
    {
        if (amount > best)
        {
            best = amount;
            type = name;
        }
    }

    private static bool IsDamageOverTime(HitData.HitType type)
    {
        return type == HitData.HitType.Burning || type == HitData.HitType.Poisoned;
    }

    /// <summary>
    /// Death message for a killer that is not a boss: animals (by star count) and every other enemy each have
    /// their own quote list in <see cref="DeathQuotes"/>.
    /// </summary>
    private static DeathMessage BuildCreatureMessage(string player, AttackerInfo attacker)
    {
        return DeathQuotes.PickCreature(player, attacker);
    }

    /// <summary>
    /// Boss message. Also used for creatures whose display name carries a colour tag, which Valheim
    /// uses for its special/boss-like enemies.
    /// </summary>
    private static DeathMessage BuildBossMessage(string player, AttackerInfo boss)
    {
        string name = boss.Name;
        int stars = boss.StarCount;

        // In-game chat: plain name only. Chat shows rich-text tags literally, so none are emitted.
        string gameStars = stars == 0
            ? string.Empty
            : " (" + stars + (stars == 1 ? " star)" : " stars)");
        string game = name + gameStars + " managed to get one over " + player + ". " +
                      name + " roams triumphant, for now...You got this!";

        // Discord: colour-tagged names get the ANSI colour (only takes effect inside an ansi code block).
        string coloredName = boss.HasColorTag ? AnsiColorStart + name + AnsiReset : name;
        string discordStars = stars == 0 ? string.Empty : " " + boss.BuildStars(StarGlyph);
        string discord = coloredName + discordStars + " managed to get one over " + player + ". " +
                         coloredName + " roams triumphant, for now...You got this!";

        return new DeathMessage(game, discord);
    }

    /// <summary>
    /// Wraps a death message in a Discord code block. Uses the ansi language when the text carries
    /// ANSI colour codes (otherwise Discord would show the raw escape characters), plain otherwise.
    /// </summary>
    /// <param name="text">The unwrapped Discord death message.</param>
    /// <returns>The message inside a code block.</returns>
    internal static string WrapForDiscord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string body = text.Replace("```", "'''").Trim();
        bool hasAnsi = body.IndexOf(Esc, StringComparison.Ordinal) >= 0;
        return (hasAnsi ? "```ansi\n" : "```\n") + body + "\n```";
    }

    /// <summary>
    /// Finds Character.m_lastHit, which lives on the Character base class, so the whole type
    /// hierarchy is searched (Type.GetField on Player alone misses private base fields).
    /// </summary>
    private static FieldInfo FindLastHitField()
    {
        if (_lastHitFieldSearched)
            return _lastHitField;

        _lastHitFieldSearched = true;

        BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.NonPublic |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly;

        for (Type type = typeof(Player); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField("m_lastHit", flags);
            if (field != null)
            {
                _lastHitField = field;
                break;
            }
        }

        if (_lastHitField == null)
            RelayDiagnostics.Warning("Could not locate Character.m_lastHit; death cause falls back to the mod's own hit tracking.");

        return _lastHitField;
    }

    /// <summary>
    /// Reads Character.m_lastHit directly, since it always reflects the blow currently killing the player.
    /// </summary>
    /// <param name="playerObject">The local player object.</param>
    /// <returns>The most recent hit, or null if unavailable.</returns>
    private static HitData GetNativeLastHit(Player playerObject)
    {
        if (playerObject == null)
            return null;

        try
        {
            FieldInfo lastHitField = FindLastHitField();
            return lastHitField == null ? null : lastHitField.GetValue(playerObject) as HitData;
        }
        catch (Exception ex)
        {
            RelayDiagnostics.Debug("Could not read native last hit: " + ex.Message);
            return null;
        }
    }

    private static string ResolveEnvironmentalDeath(HitData hit, string player)
    {
        if (hit == null)
            return null;

        switch (hit.m_hitType)
        {
            case HitData.HitType.Drowning:
            case HitData.HitType.Burning:
            case HitData.HitType.Freezing:
            case HitData.HitType.Poisoned:
            case HitData.HitType.Water:
            case HitData.HitType.Smoke:
            case HitData.HitType.EdgeOfWorld:
            case HitData.HitType.CinderFire:
            case HitData.HitType.AshlandsOcean:
            case HitData.HitType.AshlandsLava:
            case HitData.HitType.Incinerator:
            case HitData.HitType.Fall:
            case HitData.HitType.Self:
                return DeathQuotes.PickEnvironmental(player, DescribeCause(hit.m_hitType));

            case HitData.HitType.Tree:
                return player + " got crushed by a falling tree.";
            case HitData.HitType.Cart:
                return player + " got crushed by a cart.";
            case HitData.HitType.Boat:
                return player + " got crushed by a boat.";
            case HitData.HitType.Structural:
                return player + " got crushed by a collapsing structure.";
            case HitData.HitType.Stalagtite:
                return player + " got crushed by a stalagmite.";
            case HitData.HitType.DrawBridge:
                return player + " got crushed by a drawbridge.";
            case HitData.HitType.Turret:
                return player + " got crushed by a turret.";
            case HitData.HitType.Catapult:
                return player + " got crushed by a catapult.";
            case HitData.HitType.Impact:
                return player + " got crushed by an impact.";
            default:
                return null;
        }
    }

    /// <summary>
    /// The English name the weekly report files an environmental death under, from the type of the killing hit. The wording of
    /// the death message is never used for this: it holds the player's name and random jokes, so searching it for words such as
    /// "fall" or "fire" misfiled deaths (a falling tree became "Fall damage", lava and carts became "Unknown").
    /// Null for a hit type that is not environmental.
    /// </summary>
    internal static string EnvironmentalCauseName(HitData.HitType type)
    {
        switch (type)
        {
            case HitData.HitType.Drowning: return "Drowning";
            case HitData.HitType.Burning: return "Burning";
            case HitData.HitType.Freezing: return "Freezing";
            case HitData.HitType.Poisoned: return "Poison";
            case HitData.HitType.Water: return "Water damage";
            case HitData.HitType.Smoke: return "Smoke";
            case HitData.HitType.EdgeOfWorld: return "Edge of the world";
            case HitData.HitType.CinderFire: return "Cinder fire";
            case HitData.HitType.AshlandsOcean: return "Ashlands ocean";
            case HitData.HitType.AshlandsLava: return "Lava";
            case HitData.HitType.Incinerator: return "Incinerator";
            case HitData.HitType.Fall: return "Fall damage";
            case HitData.HitType.Self: return "Self-inflicted damage";
            case HitData.HitType.Tree: return "Falling tree";
            case HitData.HitType.Cart: return "Cart";
            case HitData.HitType.Boat: return "Boat";
            case HitData.HitType.Structural: return "Collapsing structure";
            case HitData.HitType.Stalagtite: return "Falling stalactite";
            case HitData.HitType.DrawBridge: return "Drawbridge";
            case HitData.HitType.Turret: return "Turret";
            case HitData.HitType.Catapult: return "Catapult";
            case HitData.HitType.Impact: return "Impact damage";
            default: return null;
        }
    }

    /// <summary>
    /// Short lower-case phrase for an environmental hit type; fills {Cause} in the environmental quotes
    /// ("...one of Valheim's many creative ways to die, {Cause}.").
    /// </summary>
    private static string DescribeCause(HitData.HitType type)
    {
        switch (type)
        {
            case HitData.HitType.Drowning: return "drowning";
            case HitData.HitType.Burning: return "burning";
            case HitData.HitType.Freezing: return "freezing";
            case HitData.HitType.Poisoned: return "poisoning";
            case HitData.HitType.Water: return "a swim gone wrong";
            case HitData.HitType.Smoke: return "smoke inhalation";
            case HitData.HitType.EdgeOfWorld: return "the edge of the world";
            case HitData.HitType.CinderFire: return "cinder fire";
            case HitData.HitType.AshlandsOcean: return "taking a dip in the Ashlands ocean";
            case HitData.HitType.AshlandsLava: return "taking a dip in lava";
            case HitData.HitType.Incinerator: return "getting incinerated";
            case HitData.HitType.Fall: return "falling";
            case HitData.HitType.Self: return "self-inflicted damage";
            default: return "the elements";
        }
    }

    private static string SanitizePlainText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        string s = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 120)
            s = s.Substring(0, 119) + "…";
        return s;
    }
}

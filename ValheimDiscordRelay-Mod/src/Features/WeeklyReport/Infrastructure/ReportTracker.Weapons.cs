using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimDiscordRelay.Client;

namespace ValheimDiscordRelay.Report;

internal static partial class ReportTracker
{
    /// <summary>
    /// The weapon of the attacking player, as a name for the report: English when this game can say it in English,
    /// otherwise the item's key, which the server turns into English when it records the hit (see EnglishNames).
    ///
    /// Character.RPC_Damage runs on the game that owns the creature, and the attacker is often another player. The
    /// equipment of a player is only loaded on that player's own game; on any other game the player's object has no
    /// weapon in hand, so asking it (GetCurrentWeapon) would always answer "unarmed". For another player the game reads
    /// what that player's game published in the player's ZDO instead: the items in the right and left hand.
    /// </summary>
    private static string ResolveHitWeapon(HitData hit)
    {
        try
        {
            if (hit == null || !hit.HaveAttacker())
                return "Unknown weapon";

            Player attacker = hit.GetAttacker() as Player;

            // The attacker is this game's own player: the game knows exactly what is held.
            if (attacker != null && ReferenceEquals(attacker, Player.m_localPlayer))
            {
                ItemDrop.ItemData weapon = attacker.GetCurrentWeapon();
                if (weapon != null && weapon.m_shared != null)
                {
                    string prefab = null;
                    try { prefab = weapon.m_dropPrefab != null ? weapon.m_dropPrefab.name : null; }
                    catch { }

                    string name = EnglishNames.FromGame(weapon.m_shared.m_name, prefab);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            else
            {
                string name = ResolveWeaponFromZdo(hit.m_attacker);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }
        return "Unknown weapon";
    }

    /// <summary>
    /// The weapon another player holds, read from the item hashes that player's game keeps in the player's ZDO
    /// (the same values the game uses to show the item in the player's hands). Like Humanoid.GetCurrentWeapon, the
    /// right hand counts first, then the left (a bow is held in the left hand); with no weapon in either hand the player
    /// is fighting with the fists. Returns null when the player or the items cannot be found.
    /// </summary>
    private static string ResolveWeaponFromZdo(ZDOID attackerId)
    {
        ZDOMan zdoMan = ZDOMan.instance;
        if (zdoMan == null)
            return null;

        ZDO zdo = zdoMan.GetZDO(attackerId);
        if (zdo == null)
            return null;

        int right = zdo.GetInt(ZDOVars.s_rightItem, 0);
        int left = zdo.GetInt(ZDOVars.s_leftItem, 0);

        string name = WeaponNameFromHash(right);
        if (name == null)
            name = WeaponNameFromHash(left);

        if (name != null)
            return name;

        // Something is held but the item is not known here (a modded item, or the item database is not ready): do not
        // call it fists.
        if (right != 0 || left != 0)
            return null;

        return EnglishNames.FromGame(UnarmedKey, null);
    }

    /// <summary>
    /// The report name of the weapon whose prefab has this stable hash, or null when the hash is 0, unknown, or an item
    /// that is not a weapon (a shield, for example).
    /// </summary>
    private static string WeaponNameFromHash(int hash)
    {
        if (hash == 0)
            return null;

        ObjectDB db = ObjectDB.instance;
        if (db == null)
            return null;

        GameObject prefab = db.GetItemPrefab(hash);
        if (prefab == null)
            return null;

        ItemDrop drop = prefab.GetComponent<ItemDrop>();
        if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null || !drop.m_itemData.IsWeapon())
            return null;

        string name = EnglishNames.FromGame(drop.m_itemData.m_shared.m_name, prefab.name);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

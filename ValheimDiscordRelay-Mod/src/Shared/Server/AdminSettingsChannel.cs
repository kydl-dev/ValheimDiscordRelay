using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using KydlCore.Common;

namespace ValheimDiscordRelay.Server;

/// <summary>
/// Tag of an entry that only the server uses (a private webhook URL, a server notification switch): it is shown in the KY hub only to
/// whoever may change it (the host, single player, or an admin of the server). Read by name by the hub's Settings page (member `Browsable`).
/// </summary>
internal sealed class AdminOnlyTag
{
    public bool? Browsable { get { return AdminSettingsChannel.CanEdit; } }
}

/// <summary>Binds the settings that only the server uses. They are never synced to players (ServerSync would send them to everybody); see <see cref="AdminSettingsChannel"/>.</summary>
internal static class AdminOnly
{
    internal static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        return Bind(section, key, defaultValue, new ConfigDescription(description));
    }

    internal static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description)
    {
        var tagged = new ConfigDescription(description.Description, description.AcceptableValues, new AdminOnlyTag());
        ConfigEntry<T> entry = ModSettings.File.Bind(section, key, defaultValue, tagged);
        AdminSettingsChannel.Track(entry);
        return entry;
    }
}

/// <summary>
/// Lets an admin see and change, from the game, the settings that only the server uses. They are NOT synced to everybody (a private
/// webhook URL must not reach ordinary players): the server sends them only to players on its adminlist.txt, and accepts a change only from
/// such a player. On the host / in single player the entries are simply edited in place. Three routed RPCs: Get (admin game -> server),
/// Values (server -> admin game), Set (admin game -> server).
/// </summary>
internal static class AdminSettingsChannel
{
    internal const string GetRpc = "ValheimDiscordRelay.AdminSettingsGet.v1";
    internal const string SetRpc = "ValheimDiscordRelay.AdminSettingsSet.v1";
    internal const string ValuesRpc = "ValheimDiscordRelay.AdminSettingsValues.v1";

    private const int ProtocolVersion = 1;
    private const int MaxEntries = 64;
    private const int MaxValueChars = 2048;

    private static readonly List<ConfigEntryBase> Entries = new List<ConfigEntryBase>();
    private static readonly HashSet<ConfigEntryBase> Dirty = new HashSet<ConfigEntryBase>();   // changed outside this channel, waiting for the main thread
    private static ZRoutedRpc _registeredRpc;
    private static bool _subscribed;
    private static bool _applying;
    private static bool _asked;

    /// <summary>May this game see and change the server-only settings? The host, single player and an admin of the server may (ServerSync's own admin state).</summary>
    internal static bool CanEdit
    {
        get
        {
            try { return SyncedConfig.Sync.IsAdmin; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>The uid of the server this game is connected to (0 when there is none).</summary>
    private static long ServerUid()
    {
        ZNetPeer peer = ZNet.instance == null ? null : ZNet.instance.GetServerPeer();
        return peer == null ? 0L : peer.m_uid;
    }

    private static bool IsServer { get { return ZNet.instance != null && ZNet.instance.IsServer(); } }

    internal static void Track(ConfigEntryBase entry)
    {
        Entries.Add(entry);
        if (!_subscribed)
        {
            _subscribed = true;
            ModSettings.File.SettingChanged += OnSettingChanged;
        }
    }

    // May come from the config file watcher thread: only note it, the main thread (Tick) sends.
    private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
    {
        if (_applying || !Entries.Contains(e.ChangedSetting))
            return;
        lock (Dirty)
            Dirty.Add(e.ChangedSetting);
    }

    /// <summary>Called when a world / connection starts (Game.Start): registers the three RPCs of this connection and asks again.</summary>
    internal static void Register()
    {
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        _asked = false;
        if (rpc == null || rpc == _registeredRpc)
            return;

        _registeredRpc = rpc;
        rpc.Register<ZPackage>(GetRpc, OnGet);
        rpc.Register<ZPackage>(SetRpc, OnSet);
        rpc.Register<ZPackage>(ValuesRpc, OnValues);
        ModLog.Verbose("AdminSettingsChannel: registered, " + Entries.Count + " server-only setting(s)");
    }

    /// <summary>Main thread, every frame: sends what changed and, on an admin's game, asks the server for the values once.</summary>
    internal static void Tick()
    {
        try
        {
            if (_registeredRpc == null || ZRoutedRpc.instance != _registeredRpc || ZNet.instance == null)
                return;

            List<ConfigEntryBase> changed = TakeDirty();
            if (IsServer)
            {
                if (changed.Count > 0)
                    PushToAdmins();
                return;
            }

            if (!_asked && CanEdit && !ZNet.instance.IsDedicated())
            {
                _asked = true;
                long server = ServerUid();
                if (server != 0L) ZRoutedRpc.instance.InvokeRoutedRPC(server, GetRpc, new object[] { Header() });
                ModLog.Verbose("AdminSettingsChannel: asked the server for the server-only settings");
            }

            if (changed.Count > 0 && CanEdit)
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), SetRpc, new object[] { Pack(changed) });
        }
        catch (Exception ex)
        {
            ModLog.Error("AdminSettingsChannel: tick failed. " + ex);
        }
    }

    private static List<ConfigEntryBase> TakeDirty()
    {
        lock (Dirty)
        {
            var list = new List<ConfigEntryBase>(Dirty);
            Dirty.Clear();
            return list;
        }
    }

    private static ZPackage Header()
    {
        var p = new ZPackage();
        p.Write(ProtocolVersion);
        return p;
    }

    private static ZPackage Pack(IList<ConfigEntryBase> entries)
    {
        ZPackage p = Header();
        p.Write(entries.Count);
        foreach (ConfigEntryBase e in entries)
        {
            p.Write(Id(e));
            p.Write(e.GetSerializedValue());
        }
        return p;
    }

    private static string Id(ConfigEntryBase e) { return e.Definition.Section + "/" + e.Definition.Key; }

    // ---- server side -----------------------------------------------------------------------------------------------------
    private static void OnGet(long sender, ZPackage package)
    {
        try
        {
            if (!IsServer || !AcceptsFrom(sender, package))
                return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ValuesRpc, new object[] { Pack(Entries) });
        }
        catch (Exception ex)
        {
            ModLog.Error("AdminSettingsChannel: could not answer a request. " + ex);
        }
    }

    private static void OnSet(long sender, ZPackage package)
    {
        try
        {
            if (!IsServer || !AcceptsFrom(sender, package))
                return;
            int applied = Apply(package, "from admin " + sender);
            if (applied > 0)
                PushToAdmins();
        }
        catch (Exception ex)
        {
            ModLog.Error("AdminSettingsChannel: could not apply a change. " + ex);
        }
    }

    private static bool AcceptsFrom(long sender, ZPackage package)
    {
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !IsAdminPeer(peer))
        {
            ModLog.Warn("AdminSettingsChannel: refused a request from " + sender + " (not on the adminlist).");
            return false;
        }
        return package.ReadInt() == ProtocolVersion;
    }

    private static void PushToAdmins()
    {
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer == null || peer.m_uid == 0L || !IsAdminPeer(peer))
                continue;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, ValuesRpc, new object[] { Pack(Entries) });
        }
    }

    // The same adminlist.txt test ServerSync uses for its own admin flag.
    private static bool IsAdminPeer(ZNetPeer peer)
    {
        try
        {
            ZNet znet = ZNet.instance;
            object list = AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(znet);
            string host = peer.m_rpc.GetSocket().GetHostName();
            MethodInfo contains = AccessTools.Method(typeof(ZNet), "ListContainsId");
            if (contains != null)
                return (bool)contains.Invoke(znet, new object[] { list, host });
            return (bool)AccessTools.Method(list.GetType(), "Contains").Invoke(list, new object[] { host });
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- admin's game ----------------------------------------------------------------------------------------------------
    private static void OnValues(long sender, ZPackage package)
    {
        try
        {
            if (IsServer || sender != ServerUid() || package.ReadInt() != ProtocolVersion)
                return;
            Apply(package, "from the server");
        }
        catch (Exception ex)
        {
            ModLog.Error("AdminSettingsChannel: could not read the server's values. " + ex);
        }
    }

    // ---- both ------------------------------------------------------------------------------------------------------------
    private static int Apply(ZPackage package, string source)
    {
        int count = package.ReadInt();
        if (count < 0 || count > MaxEntries)
        {
            ModLog.Warn("AdminSettingsChannel: bad entry count " + count + " " + source);
            return 0;
        }

        int applied = 0;
        _applying = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                string id = package.ReadString();
                string value = package.ReadString();
                ConfigEntryBase entry = Entries.Find(e => Id(e) == id);
                if (entry == null || value == null || value.Length > MaxValueChars)
                    continue;
                if (entry.GetSerializedValue() == value)
                    continue;
                entry.SetSerializedValue(value);
                applied++;
            }
        }
        finally
        {
            _applying = false;
        }
        ModLog.Verbose("AdminSettingsChannel: " + applied + " setting(s) changed " + source);
        return applied;
    }
}

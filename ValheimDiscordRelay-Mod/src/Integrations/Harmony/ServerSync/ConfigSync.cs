// ---------------------------------------------------------------------------
// ServerSync (https://github.com/blaxxun-boop/ServerSync)
// Copyright 2021 Tykea. Permission notice: see LICENSE.txt in this folder.
//
// Vendored copy of upstream ConfigSync.cs @ c57c2aa (2025-04-06), so the mod
// needs no ILRepack/NuGet step. Local changes (everything else is upstream):
//   1. Two C# 12 primary constructors rewritten as normal constructors, so the
//      project can stay on LangVersion 10.
//   2. VersionCheck.ShowConnectionError (the "server expects you to have <mod>"
//      text on the connect-failed dialog) is wrapped in
//      #if SERVERSYNC_CONNECT_ERROR_UI. It needs Unity.TextMeshPro.dll, which
//      is not in References/. Nothing in this mod depends on it.
//   3. '#nullable enable' added because the file uses nullable annotations.
//   4. Strict version enforcement (ValheimDiscordRelay): two new fields on ConfigSync,
//      AnnounceVersion and RequireExactVersion (copied into VersionCheck). A game with
//      AnnounceVersion always sends its version to the server, even though ModRequired
//      is false (so it still joins servers that do not have the mod). A server with
//      RequireExactVersion refuses every client that did not announce the mod's version
//      or announced a different one. Search for "RequireExactVersion" to see every change.
// ---------------------------------------------------------------------------
#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ServerSync;

[PublicAPI]
public abstract class OwnConfigEntryBase
{
    public object? LocalBaseValue;
    public abstract ConfigEntryBase BaseConfig { get; }

    public bool SynchronizedConfig = true;
}

[PublicAPI]
public class SyncedConfigEntry<T> : OwnConfigEntryBase
{
    public SyncedConfigEntry(ConfigEntry<T> sourceConfig)
    {
        SourceConfig = sourceConfig;
    }

    public override ConfigEntryBase BaseConfig => SourceConfig;
    public readonly ConfigEntry<T> SourceConfig;

    public T Value
    {
        get => SourceConfig.Value;
        set => SourceConfig.Value = value;
    }

    public void AssignLocalValue(T value)
    {
        if (LocalBaseValue == null)
        {
            Value = value;
        }
        else
        {
            LocalBaseValue = value;
        }
    }
}

public abstract class CustomSyncedValueBase
{
    public event Action? ValueChanged;

    public object? LocalBaseValue;

    public readonly string Identifier;
    public readonly Type Type;

    private object? boxedValue;

    public object? BoxedValue
    {
        get => boxedValue;
        set
        {
            boxedValue = value;
            ValueChanged?.Invoke();
        }
    }

    protected bool localIsOwner;
    public readonly int Priority;

    protected CustomSyncedValueBase(ConfigSync configSync, string identifier, Type type, int priority)
    {
        Priority = priority;
        Identifier = identifier;
        Type = type;
        configSync.AddCustomValue(this);
        localIsOwner = configSync.IsSourceOfTruth;
        configSync.SourceOfTruthChanged += truth => localIsOwner = truth;
    }
}

[PublicAPI]
public sealed class CustomSyncedValue<T> : CustomSyncedValueBase
{
    public T Value
    {
        get => (T)BoxedValue!;
        set => BoxedValue = value;
    }

    public CustomSyncedValue(ConfigSync configSync, string identifier, T value = default!, int priority = 0) : base(configSync, identifier, typeof(T), priority)
    {
        Value = value;
    }

    public void AssignLocalValue(T value)
    {
        if (localIsOwner)
        {
            Value = value;
        }
        else
        {
            LocalBaseValue = value;
        }
    }
}

internal class ConfigurationManagerAttributes
{
    [UsedImplicitly] public bool? ReadOnly = false;
}

[PublicAPI]
public partial class ConfigSync
{
    public static bool ProcessingServerUpdate = false;

    public readonly string Name;
    public string? DisplayName;
    public string? CurrentVersion;
    public string? MinimumRequiredVersion;
    public bool ModRequired = false;

    /// <summary>
    /// ValheimDiscordRelay: send this mod's version to the other side even when ModRequired is false. Needed on clients, so a
    /// server that enforces the version can tell a client that has the mod from one that does not.
    /// </summary>
    public bool AnnounceVersion = false;

    /// <summary>
    /// ValheimDiscordRelay: server side. When true, a client is refused unless it announced exactly CurrentVersion
    /// (no announcement = the mod is not installed; an older or a newer version is refused as well). Does not change what a
    /// client does when it connects to a server: ModRequired stays false, so a client still joins servers without the mod.
    /// </summary>
    public bool RequireExactVersion = false;

    private bool? forceConfigLocking;

    public bool IsLocked
    {
        get => (forceConfigLocking ?? lockedConfig != null && ((IConvertible)lockedConfig.BaseConfig.BoxedValue).ToInt32(CultureInfo.InvariantCulture) != 0) && !lockExempt;
        set => forceConfigLocking = value;
    }

    public bool IsAdmin => lockExempt || isSourceOfTruth;

    private bool isSourceOfTruth = true;

    public bool IsSourceOfTruth
    {
        get => isSourceOfTruth;
        private set
        {
            if (value != isSourceOfTruth)
            {
                isSourceOfTruth = value;
                SourceOfTruthChanged?.Invoke(value);
            }
        }
    }

    public bool InitialSyncDone { get; private set; } = false;

    public event Action<bool>? SourceOfTruthChanged;

    private static readonly HashSet<ConfigSync> configSyncs = new();

    private readonly HashSet<OwnConfigEntryBase> allConfigs = new();
    private HashSet<CustomSyncedValueBase> allCustomValues = new();

    private static bool isServer;

    private static bool lockExempt = false;

    private OwnConfigEntryBase? lockedConfig = null;
    private event Action? lockedConfigChanged;

    static ConfigSync()
    {
        RuntimeHelpers.RunClassConstructor(typeof(VersionCheck).TypeHandle);
    }

    public ConfigSync(string name)
    {
        Name = name;
        configSyncs.Add(this);
        _ = new VersionCheck(this);
    }

    public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry)
    {
        if (configData(configEntry) is not SyncedConfigEntry<T> syncedEntry)
        {
            syncedEntry = new SyncedConfigEntry<T>(configEntry);
            AccessTools.DeclaredField(typeof(ConfigDescription), "<Tags>k__BackingField").SetValue(configEntry.Description, new object[] { new ConfigurationManagerAttributes() }.Concat(configEntry.Description.Tags ?? Array.Empty<object>()).Concat(new[] { syncedEntry }).ToArray());
            configEntry.SettingChanged += (_, _) =>
            {
                if (!ProcessingServerUpdate && syncedEntry.SynchronizedConfig)
                {
                    Broadcast(ZRoutedRpc.Everybody, configEntry);
                }
            };
            allConfigs.Add(syncedEntry);
        }

        return syncedEntry;
    }

    public SyncedConfigEntry<T> AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible
    {
        if (lockedConfig != null)
        {
            throw new Exception("Cannot initialize locking ConfigEntry twice");
        }

        lockedConfig = AddConfigEntry(lockingConfig);
        lockingConfig.SettingChanged += (_, _) => lockedConfigChanged?.Invoke();

        return (SyncedConfigEntry<T>)lockedConfig;
    }

    internal void AddCustomValue(CustomSyncedValueBase customValue)
    {
        if (allCustomValues.Select(v => v.Identifier).Concat(new[] { "serverversion" }).Contains(customValue.Identifier))
        {
            throw new Exception("Cannot have multiple settings with the same name or with a reserved name (serverversion)");
        }

        allCustomValues.Add(customValue);
        allCustomValues = new HashSet<CustomSyncedValueBase>(allCustomValues.OrderByDescending(v => v.Priority));
        customValue.ValueChanged += () =>
        {
            if (!ProcessingServerUpdate)
            {
                Broadcast(ZRoutedRpc.Everybody, customValue);
            }
        };
    }

    [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
    private static class SnatchCurrentlyHandlingRPC
    {
        public static ZRpc? currentRpc;

        [HarmonyPrefix]
        private static void Prefix(ZRpc __instance) => currentRpc = __instance;
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class RegisterRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance)
        {
            isServer = __instance.IsServer();
            foreach (ConfigSync configSync in configSyncs)
            {
                ZRoutedRpc.instance.Register<ZPackage>(configSync.Name + " ConfigSync", configSync.RPC_FromOtherClientConfigSync);
                if (isServer)
                {
                    configSync.InitialSyncDone = true;
                    Debug.Log($"Registered '{configSync.Name} ConfigSync' RPC - waiting for incoming connections");
                }
            }

            IEnumerator WatchAdminListChanges()
            {
                MethodInfo? listContainsId = AccessTools.DeclaredMethod(typeof(ZNet), "ListContainsId");
                SyncedList adminList = (SyncedList)AccessTools.DeclaredField(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
                List<string> CurrentList = new(adminList.GetList());
                for (;;)
                {
                    yield return new WaitForSeconds(30);
                    if (!adminList.GetList().SequenceEqual(CurrentList))
                    {
                        CurrentList = new List<string>(adminList.GetList());

                        void SendAdmin(List<ZNetPeer> peers, bool isAdmin)
                        {
                            ZPackage package = ConfigsToPackage(packageEntries: new[]
                            {
                                new PackageEntry { section = "Internal", key = "lockexempt", type = typeof(bool), value = isAdmin },
                            });

                            if (configSyncs.First() is { } configSync)
                            {
                                ZNet.instance.StartCoroutine(configSync.sendZPackage(peers, package));
                            }
                        }

                        List<ZNetPeer> adminPeer = ZNet.instance.GetPeers().Where(p =>
                        {
                            string client = p.m_rpc.GetSocket().GetHostName();
                            return listContainsId is null ? adminList.Contains(client) : (bool)listContainsId.Invoke(ZNet.instance, new object[] { adminList, client });
                        }).ToList();
                        List<ZNetPeer> nonAdminPeer = ZNet.instance.GetPeers().Except(adminPeer).ToList();
                        SendAdmin(nonAdminPeer, false);
                        SendAdmin(adminPeer, true);
                    }
                }
                // ReSharper disable once IteratorNeverReturns
            }

            if (isServer)
            {
                __instance.StartCoroutine(WatchAdminListChanges());
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class RegisterClientRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer())
            {
                foreach (ConfigSync configSync in configSyncs)
                {
                    peer.m_rpc.Register<ZPackage>(configSync.Name + " ConfigSync", configSync.RPC_FromServerConfigSync);
                }
            }
        }
    }

    private const byte PARTIAL_CONFIGS = 1;
    private const byte FRAGMENTED_CONFIG = 2;
    private const byte COMPRESSED_CONFIG = 4;

    private readonly Dictionary<string, SortedDictionary<int, byte[]>> configValueCache = new();
    private readonly List<KeyValuePair<long, string>> cacheExpirations = new(); // avoid leaking memory

    [HarmonyPatch(typeof(ZNet), "Shutdown")]
    private class ResetConfigsOnShutdown
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ProcessingServerUpdate = true;
            foreach (ConfigSync serverSync in configSyncs)
            {
                serverSync.resetConfigsFromServer();
                serverSync.IsSourceOfTruth = true;
                serverSync.InitialSyncDone = false;
            }
            ProcessingServerUpdate = false;
        }
    }

    private static bool isWritableConfig(OwnConfigEntryBase config)
    {
        if (configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config)) is not { } configSync)
        {
            return true;
        }

        return configSync.IsSourceOfTruth || !config.SynchronizedConfig || config.LocalBaseValue == null || (!configSync.IsLocked && (config != configSync.lockedConfig || lockExempt));
    }

    private void serverLockedSettingChanged()
    {
        foreach (OwnConfigEntryBase configEntryBase in allConfigs)
        {
            configAttribute<ConfigurationManagerAttributes>(configEntryBase.BaseConfig).ReadOnly = !isWritableConfig(configEntryBase);
        }
    }

    private void resetConfigsFromServer()
    {
        ConfigFile? configFile = null;
        bool originalSaveOnConfigSet = false;
        foreach (OwnConfigEntryBase config in allConfigs.Where(config => config.LocalBaseValue != null))
        {
            if (configFile is null)
            {
                configFile = config.BaseConfig.ConfigFile;
                originalSaveOnConfigSet = configFile.SaveOnConfigSet;
                configFile.SaveOnConfigSet = false;
            }
            config.BaseConfig.BoxedValue = config.LocalBaseValue;
            config.LocalBaseValue = null;
        }
        if (configFile is not null)
        {
            configFile.SaveOnConfigSet = originalSaveOnConfigSet;
        }

        foreach (CustomSyncedValueBase config in allCustomValues.Where(config => config.LocalBaseValue != null))
        {
            config.BoxedValue = config.LocalBaseValue;
            config.LocalBaseValue = null;
        }

        lockedConfigChanged -= serverLockedSettingChanged;
        serverLockedSettingChanged();
    }

    private class PackageEntry
    {
        public string section = null!;
        public string key = null!;
        public Type type = null!;
        public object? value;
    }

    private void Broadcast(long target, params ConfigEntryBase[] configs)
    {
        if (!IsLocked || isServer)
        {
            ZPackage package = ConfigsToPackage(configs);
            ZNet.instance?.StartCoroutine(sendZPackage(target, package));
        }
    }

    private void Broadcast(long target, params CustomSyncedValueBase[] customValues)
    {
        if (!IsLocked || isServer)
        {
            ZPackage package = ConfigsToPackage(customValues: customValues);
            ZNet.instance?.StartCoroutine(sendZPackage(target, package));
        }
    }

    private static OwnConfigEntryBase? configData(ConfigEntryBase config)
    {
        return config.Description.Tags?.OfType<OwnConfigEntryBase>().SingleOrDefault();
    }

    public static SyncedConfigEntry<T>? ConfigData<T>(ConfigEntry<T> config)
    {
        return config.Description.Tags?.OfType<SyncedConfigEntry<T>>().SingleOrDefault();
    }

    private static T configAttribute<T>(ConfigEntryBase config)
    {
        return config.Description.Tags.OfType<T>().First();
    }

    private static Type configType(ConfigEntryBase config) => configType(config.SettingType);

    private static Type configType(Type type) => type.IsEnum ? Enum.GetUnderlyingType(type) : type;

    [HarmonyPatch(typeof(ConfigEntryBase), nameof(ConfigEntryBase.GetSerializedValue))]
    private static class PreventSavingServerInfo
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, ref string __result)
        {
            if (configData(__instance) is not { } data || isWritableConfig(data))
            {
                return true;
            }

            __result = TomlTypeConverter.ConvertToString(data.LocalBaseValue, __instance.SettingType);
            return false;
        }
    }

    [HarmonyPatch(typeof(ConfigEntryBase), nameof(ConfigEntryBase.SetSerializedValue))]
    private static class PreventConfigRereadChangingValues
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, string value)
        {
            if (configData(__instance) is not { } data || data.LocalBaseValue == null)
            {
                return true;
            }

            try
            {
                data.LocalBaseValue = TomlTypeConverter.ConvertToValue(value, __instance.SettingType);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Config value of setting \"{__instance.Definition}\" could not be parsed and will be ignored. Reason: {e.Message}; Value: {value}");
            }
            return false;
        }
    }

}

[PublicAPI]
[HarmonyPatch]
public class VersionCheck
{
    private static readonly HashSet<VersionCheck> versionChecks = new();
    private static readonly Dictionary<string, string> notProcessedNames = new();

    public string Name;

    private string? displayName;

    public string DisplayName
    {
        get => displayName ?? Name;
        set => displayName = value;
    }

    private string? currentVersion;

    public string CurrentVersion
    {
        get => currentVersion ?? "0.0.0";
        set => currentVersion = value;
    }

    private string? minimumRequiredVersion;

    public string MinimumRequiredVersion
    {
        get => minimumRequiredVersion ?? (ModRequired ? CurrentVersion : "0.0.0");
        set => minimumRequiredVersion = value;
    }

    public bool ModRequired = true;

    // ValheimDiscordRelay: see ConfigSync.AnnounceVersion / ConfigSync.RequireExactVersion.
    public bool AnnounceVersion = false;
    public bool RequireExactVersion = false;

    private string? ReceivedCurrentVersion;

    private string? ReceivedMinimumRequiredVersion;

    // Tracks which clients have passed the version check (only for servers).
    private readonly List<ZRpc> ValidatedClients = new();

    // ValheimDiscordRelay: the version each client announced (only for servers), so a refusal is logged with the right version.
    private readonly Dictionary<ZRpc, string> ClientVersions = new();

    // Optional backing field to use ConfigSync values (will override other fields).
    private ConfigSync? ConfigSync;

    private static void PatchServerSync()
    {
        if (PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ZNet), "Awake"))?.Postfixes.Count(p => p.PatchMethod.DeclaringType == typeof(ConfigSync.RegisterRPCPatch)) > 0)
        {
            return;
        }

        Harmony harmony = new("org.bepinex.helpers.ServerSync");
        foreach (Type type in typeof(ConfigSync).GetNestedTypes(BindingFlags.NonPublic).Concat(new[] { typeof(VersionCheck) }).Where(t => t.IsClass))
        {
            harmony.PatchAll(type);
        }
    }

    static VersionCheck()
    {
        typeof(ThreadingHelper).GetMethod("StartSyncInvoke")!.Invoke(ThreadingHelper.Instance, new object[] { (Action)PatchServerSync });
    }

    public VersionCheck(string name)
    {
        Name = name;
        ModRequired = true;
        versionChecks.Add(this);
    }

    public VersionCheck(ConfigSync configSync)
    {
        ConfigSync = configSync;
        Name = ConfigSync.Name;
        versionChecks.Add(this);
    }

    public void Initialize()
    {
        ReceivedCurrentVersion = null;
        ReceivedMinimumRequiredVersion = null;
        if (ConfigSync == null)
        {
            return;
        }
        Name = ConfigSync.Name;
        DisplayName = ConfigSync.DisplayName!;
        CurrentVersion = ConfigSync.CurrentVersion!;
        MinimumRequiredVersion = ConfigSync.MinimumRequiredVersion!;
        ModRequired = ConfigSync.ModRequired;
        AnnounceVersion = ConfigSync.AnnounceVersion;
        RequireExactVersion = ConfigSync.RequireExactVersion;
    }

    /// <summary>
    /// ValheimDiscordRelay: for a server that has RequireExactVersion on, the announced version must be exactly ours
    /// (compared as text, so a malformed version can never throw here). Otherwise the normal minimum-version rule.
    /// </summary>
    private bool IsClientAccepted(ZRpc? rpc)
    {
        if (RequireExactVersion)
        {
            return rpc != null && ClientVersions.TryGetValue(rpc, out string? announced) && string.Equals(announced, CurrentVersion, StringComparison.Ordinal);
        }

        return IsVersionOk();
    }

    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    private bool IsVersionOk()
    {
        if (ReceivedMinimumRequiredVersion == null || ReceivedCurrentVersion == null)
        {
            return !ModRequired;
        }
        bool myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
        bool otherVersionOk = new System.Version(ReceivedCurrentVersion) >= new System.Version(MinimumRequiredVersion);
        return myVersionOk && otherVersionOk;
    }

    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    private string ErrorClient()
    {
        if (ReceivedMinimumRequiredVersion == null)
        {
            return $"{DisplayName} is not installed on the server.";
        }
        bool myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
        return myVersionOk ? $"{DisplayName} may not be higher than version {ReceivedCurrentVersion}. You have version {CurrentVersion}." : $"{DisplayName} needs to be at least version {ReceivedMinimumRequiredVersion}. You have version {CurrentVersion}.";
    }

    private string ErrorServer(ZRpc rpc)
    {
        if (RequireExactVersion)
        {
            string found = !ClientVersions.TryGetValue(rpc, out string? announced) ? "has not installed it (or runs a build without the version check)" : $"has version {announced}";
            return $"Disconnect: The client ({rpc.GetSocket().GetHostName()}) {found}; this server requires {DisplayName} version {CurrentVersion} exactly.";
        }

        return $"Disconnect: The client ({rpc.GetSocket().GetHostName()}) doesn't have the correct {DisplayName} version {MinimumRequiredVersion}";
    }

    private string Error(ZRpc? rpc = null)
    {
        return rpc == null ? ErrorClient() : ErrorServer(rpc);
    }

    private static VersionCheck[] GetFailedClient()
    {
        return versionChecks.Where(check => !check.IsVersionOk()).ToArray();
    }

    private static VersionCheck[] GetFailedServer(ZRpc rpc)
    {
        return versionChecks.Where(check => (check.ModRequired || check.RequireExactVersion) && !check.ValidatedClients.Contains(rpc)).ToArray();
    }

    private static void Logout()
    {
        Game.instance.Logout();
        AccessTools.DeclaredField(typeof(ZNet), "m_connectionStatus").SetValue(null, ZNet.ConnectionStatus.ErrorVersion);
    }

    private static void DisconnectClient(ZRpc rpc)
    {
        rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
    }

    private static void CheckVersion(ZRpc rpc, ZPackage pkg) => CheckVersion(rpc, pkg, null);

    private static void CheckVersion(ZRpc rpc, ZPackage pkg, Action<ZRpc, ZPackage>? original)
    {
        string guid = pkg.ReadString();
        string minimumRequiredVersion = pkg.ReadString();
        string currentVersion = pkg.ReadString();

        bool matched = false;

        foreach (VersionCheck check in versionChecks)
        {
            if (guid != check.Name)
            {
                continue;
            }

            Debug.Log($"Received {check.DisplayName} version {currentVersion} and minimum version {minimumRequiredVersion} from the {(ZNet.instance.IsServer() ? "client" : "server")}.");

            check.ReceivedMinimumRequiredVersion = minimumRequiredVersion;
            check.ReceivedCurrentVersion = currentVersion;
            if (ZNet.instance.IsServer())
            {
                check.ClientVersions[rpc] = currentVersion;
            }
            if (ZNet.instance.IsServer() && check.IsClientAccepted(rpc))
            {
                check.ValidatedClients.Add(rpc);
            }

            matched = true;
        }

        if (!matched)
        {
            pkg.SetPos(0);
            if (original is not null)
            {
                original(rpc, pkg);
                if (pkg.GetPos() == 0)
                {
                    notProcessedNames.Add(guid, currentVersion);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo"), HarmonyPrefix]
    private static bool RPC_PeerInfo(ZRpc rpc, ZNet __instance)
    {
        VersionCheck[] failedChecks = __instance.IsServer() ? GetFailedServer(rpc) : GetFailedClient();
        if (failedChecks.Length == 0)
        {
            return true;
        }

        foreach (VersionCheck check in failedChecks)
        {
            Debug.LogWarning(check.Error(rpc));
        }

        if (__instance.IsServer())
        {
            DisconnectClient(rpc);
        }
        else
        {
            Logout();
        }
        return false;
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPrefix]
    private static void RegisterAndCheckVersion(ZNetPeer peer, ZNet __instance)
    {
        notProcessedNames.Clear();

        IDictionary rpcFunctions = (IDictionary)typeof(ZRpc).GetField("m_functions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer.m_rpc);
        if (rpcFunctions.Contains("ServerSync VersionCheck".GetStableHashCode()))
        {
            object function = rpcFunctions["ServerSync VersionCheck".GetStableHashCode()];
            Action<ZRpc, ZPackage> action = (Action<ZRpc, ZPackage>)function.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(function);
            peer.m_rpc.Register<ZPackage>("ServerSync VersionCheck", (rpc, pkg) => CheckVersion(rpc, pkg, action));
        }
        else
        {
            peer.m_rpc.Register<ZPackage>("ServerSync VersionCheck", CheckVersion);
        }

        foreach (VersionCheck check in versionChecks)
        {
            check.Initialize();
            // If the mod is not required, then it's enough for only one side to do the check.
            if (!check.ModRequired && !check.AnnounceVersion && !__instance.IsServer())
            {
                continue;
            }

            Debug.Log($"Sending {check.DisplayName} version {check.CurrentVersion} and minimum version {check.MinimumRequiredVersion} to the {(__instance.IsServer() ? "client" : "server")}.");

            ZPackage zpackage = new();
            zpackage.Write(check.Name);
            zpackage.Write(check.MinimumRequiredVersion);
            zpackage.Write(check.CurrentVersion);
            peer.m_rpc.Invoke("ServerSync VersionCheck", zpackage);
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPrefix]
    private static void RemoveDisconnected(ZNetPeer peer, ZNet __instance)
    {
        if (!__instance.IsServer())
        {
            return;
        }
        foreach (VersionCheck check in versionChecks)
        {
            check.ValidatedClients.Remove(peer.m_rpc);
            check.ClientVersions.Remove(peer.m_rpc);
        }
    }

#if SERVERSYNC_CONNECT_ERROR_UI
    [HarmonyPatch(typeof(FejdStartup), "ShowConnectError"), HarmonyPostfix]
    private static void ShowConnectionError(FejdStartup __instance)
    {
        if (!__instance.m_connectionFailedPanel.activeSelf || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.ErrorVersion)
        {
            return;
        }
        bool failedCheck = false;
        VersionCheck[] failedChecks = GetFailedClient();
        if (failedChecks.Length > 0)
        {
            string error = string.Join("\n", failedChecks.Select(check => check.Error()));
            __instance.m_connectionFailedError.text += "\n" + error;
            failedCheck = true;
        }

        foreach (KeyValuePair<string, string> kv in notProcessedNames.OrderBy(kv => kv.Key))
        {
            if (!__instance.m_connectionFailedError.text.Contains(kv.Key))
            {
                __instance.m_connectionFailedError.text += $"\nServer expects you to have {kv.Key} (Version: {kv.Value}) installed.";
                failedCheck = true;
            }
        }

        if (failedCheck)
        {
            RectTransform panel = __instance.m_connectionFailedPanel.transform.Find("Image").GetComponent<RectTransform>();
            panel.sizeDelta = panel.sizeDelta with { x = 675 };
            __instance.m_connectionFailedError.ForceMeshUpdate();
            float newHeight = __instance.m_connectionFailedError.renderedHeight + 105;
            RectTransform button = panel.transform.Find("ButtonOk").GetComponent<RectTransform>();
            button.anchoredPosition = new Vector2(button.anchoredPosition.x, button.anchoredPosition.y - (newHeight - panel.sizeDelta.y) / 2);
            panel.sizeDelta = panel.sizeDelta with { y = newHeight };
        }
    }
#endif
}

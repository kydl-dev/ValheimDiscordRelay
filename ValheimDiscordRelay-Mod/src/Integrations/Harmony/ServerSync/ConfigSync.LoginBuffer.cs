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

public partial class ConfigSync
{
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private class SendConfigsAfterLogin
    {
        private class BufferingSocket : ZPlayFabSocket, ISocket
        {
            public BufferingSocket(ISocket original)
            {
                Original = original;
            }

            public volatile bool finished = false;
            public volatile int versionMatchQueued = -1;
            public readonly List<ZPackage> Package = new();
            public readonly ISocket Original;

            public new bool IsConnected() => Original.IsConnected();
            public new ZPackage Recv() => Original.Recv();
            public new int GetSendQueueSize() => Original.GetSendQueueSize();
            public new int GetCurrentSendRate() => Original.GetCurrentSendRate();
            public new bool IsHost() => Original.IsHost();
            public new void Dispose() => Original.Dispose();
            public new bool GotNewData() => Original.GotNewData();
            public new void Close() => Original.Close();
            public new string GetEndPointString() => Original.GetEndPointString();
            public new void GetAndResetStats(out int totalSent, out int totalRecv) => Original.GetAndResetStats(out totalSent, out totalRecv);
            public new void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) => Original.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
            public new ISocket Accept() => Original.Accept();
            public new int GetHostPort() => Original.GetHostPort();
            public new bool Flush() => Original.Flush();
            public new string GetHostName() => Original.GetHostName();

            public new void VersionMatch()
            {
                if (finished)
                {
                    Original.VersionMatch();
                }
                else
                {
                    versionMatchQueued = Package.Count;
                }
            }

            public new void Send(ZPackage pkg)
            {
                int oldPos = pkg.GetPos();
                pkg.SetPos(0);
                int methodHash = pkg.ReadInt();
                if ((methodHash == "PeerInfo".GetStableHashCode() || methodHash == "RoutedRPC".GetStableHashCode() || methodHash == "ZDOData".GetStableHashCode()) && !finished)
                {
                    ZPackage newPkg = new(pkg.GetArray());
                    newPkg.SetPos(oldPos);
                    Package.Add(newPkg); // the original ZPackage gets reused, create a new one
                }
                else
                {
                    pkg.SetPos(oldPos);
                    Original.Send(pkg);
                }
            }
        }

        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        private static void Prefix(ref Dictionary<Assembly, BufferingSocket>? __state, ZNet __instance, ZRpc rpc)
        {
            if (__instance.IsServer())
            {
                BufferingSocket bufferingSocket = new(rpc.GetSocket());
                AccessTools.DeclaredField(typeof(ZRpc), "m_socket").SetValue(rpc, bufferingSocket);
                // Don't replace on steam sockets, RPC_PeerInfo does peer.m_socket as ZSteamSocket - which will cause a nullref when replaced
                if (AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) }).Invoke(__instance, new object[] { rpc }) is ZNetPeer peer && ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
                {
                    FieldInfo field = AccessTools.DeclaredField(typeof(ZNetPeer), "m_socket");
                    if (field.GetValue(peer) is ZPlayFabSocket playFabSocket)
                    {
                        typeof(ZPlayFabSocket).GetField("m_remotePlayerId").SetValue(bufferingSocket, playFabSocket.m_remotePlayerId);
                    }
                    field.SetValue(peer, bufferingSocket);
                }

                __state ??= new Dictionary<Assembly, BufferingSocket>();
                __state[Assembly.GetExecutingAssembly()] = bufferingSocket;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Dictionary<Assembly, BufferingSocket> __state, ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer())
            {
                return;
            }

            void SendBufferedData()
            {
                if (rpc.GetSocket() is BufferingSocket bufferingSocket)
                {
                    AccessTools.DeclaredField(typeof(ZRpc), "m_socket").SetValue(rpc, bufferingSocket.Original);
                    if (AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) }).Invoke(__instance, new object[] { rpc }) is ZNetPeer peer)
                    {
                        AccessTools.DeclaredField(typeof(ZNetPeer), "m_socket").SetValue(peer, bufferingSocket.Original);
                    }
                }

                bufferingSocket = __state[Assembly.GetExecutingAssembly()];
                bufferingSocket.finished = true;

                for (int i = 0; i < bufferingSocket.Package.Count; ++i)
                {
                    if (i == bufferingSocket.versionMatchQueued)
                    {
                        bufferingSocket.Original.VersionMatch();
                    }
                    bufferingSocket.Original.Send(bufferingSocket.Package[i]);
                }
                if (bufferingSocket.Package.Count == bufferingSocket.versionMatchQueued)
                {
                    bufferingSocket.Original.VersionMatch();
                }
            }

            if (AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) }).Invoke(__instance, new object[] { rpc }) is not ZNetPeer peer)
            {
                SendBufferedData();
                return;
            }

            IEnumerator sendAsync()
            {
                foreach (ConfigSync configSync in configSyncs)
                {
                    List<PackageEntry> entries = new();
                    if (configSync.CurrentVersion != null)
                    {
                        entries.Add(new PackageEntry { section = "Internal", key = "serverversion", type = typeof(string), value = configSync.CurrentVersion });
                    }

                    MethodInfo? listContainsId = AccessTools.DeclaredMethod(typeof(ZNet), "ListContainsId");
                    SyncedList adminList = (SyncedList)AccessTools.DeclaredField(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
                    entries.Add(new PackageEntry { section = "Internal", key = "lockexempt", type = typeof(bool), value = listContainsId is null ? adminList.Contains(rpc.GetSocket().GetHostName()) : listContainsId.Invoke(ZNet.instance, new object[] { adminList, rpc.GetSocket().GetHostName() }) });

                    ZPackage package = ConfigsToPackage(configSync.allConfigs.Select(c => c.BaseConfig), configSync.allCustomValues, entries, false);

                    yield return __instance.StartCoroutine(configSync.sendZPackage(new List<ZNetPeer> { peer }, package));

                }

                SendBufferedData();
            }

            __instance.StartCoroutine(sendAsync());
        }
    }
}

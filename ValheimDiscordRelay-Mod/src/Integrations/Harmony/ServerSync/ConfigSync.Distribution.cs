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
    private static long packageCounter = 0;

    private IEnumerator<bool> distributeConfigToPeers(ZNetPeer peer, ZPackage package)
    {
        if (ZRoutedRpc.instance is not { } rpc)
        {
            yield break;
        }

        const int packageSliceSize = 250000;
        const int maximumSendQueueSize = 20000;

        IEnumerable<bool> waitForQueue()
        {
            float timeout = Time.time + 30;
            while (peer.m_socket.GetSendQueueSize() > maximumSendQueueSize)
            {
                if (Time.time > timeout)
                {
                    Debug.Log($"Disconnecting {peer.m_uid} after 30 seconds config sending timeout");
                    peer.m_rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                    yield break;
                }

                yield return false;
            }
        }

        void SendPackage(ZPackage pkg)
        {
            string method = Name + " ConfigSync";
            if (isServer)
            {
                peer.m_rpc.Invoke(method, pkg);
            }
            else
            {
                rpc.InvokeRoutedRPC(peer.m_server ? 0 : peer.m_uid, method, pkg);
            }
        }

        if (package.GetArray() is { LongLength: > packageSliceSize } data)
        {
            int fragments = (int)(1 + (data.LongLength - 1) / packageSliceSize);
            long packageIdentifier = ++packageCounter;
            for (int fragment = 0; fragment < fragments; ++fragment)
            {
                foreach (bool wait in waitForQueue())
                {
                    yield return wait;
                }

                if (!peer.m_socket.IsConnected())
                {
                    yield break;
                }

                ZPackage fragmentedPackage = new();
                fragmentedPackage.Write(FRAGMENTED_CONFIG);
                fragmentedPackage.Write(packageIdentifier);
                fragmentedPackage.Write(fragment);
                fragmentedPackage.Write(fragments);
                fragmentedPackage.Write(data.Skip(packageSliceSize * fragment).Take(packageSliceSize).ToArray());
                SendPackage(fragmentedPackage);

                if (fragment != fragments - 1)
                {
                    yield return true;
                }
            }
        }
        else
        {
            foreach (bool wait in waitForQueue())
            {
                yield return wait;
            }

            SendPackage(package);
        }
    }

    private IEnumerator sendZPackage(long target, ZPackage package)
    {
        if (!ZNet.instance)
        {
            return Enumerable.Empty<object>().GetEnumerator();
        }

        List<ZNetPeer> peers = (List<ZNetPeer>)AccessTools.DeclaredField(typeof(ZRoutedRpc), "m_peers").GetValue(ZRoutedRpc.instance);
        if (target != ZRoutedRpc.Everybody)
        {
            peers = peers.Where(p => p.m_uid == target).ToList();
        }

        return sendZPackage(peers, package);
    }

    private IEnumerator sendZPackage(List<ZNetPeer> peers, ZPackage package)
    {
        if (!ZNet.instance)
        {
            yield break;
        }

        const int compressMinSize = 10000;

        if (package.GetArray() is { LongLength: > compressMinSize } rawData)
        {
            ZPackage compressedPackage = new();
            compressedPackage.Write(COMPRESSED_CONFIG);
            MemoryStream output = new();
            using (DeflateStream deflateStream = new(output, CompressionLevel.Optimal))
            {
                deflateStream.Write(rawData, 0, rawData.Length);
            }
            compressedPackage.Write(output.ToArray());
            package = compressedPackage;
        }

        List<IEnumerator<bool>> writers = peers.Where(peer => peer.IsReady()).Select(p => distributeConfigToPeers(p, package)).ToList();
        writers.RemoveAll(writer => !writer.MoveNext());
        while (writers.Count > 0)
        {
            yield return null;
            writers.RemoveAll(writer => !writer.MoveNext());
        }
    }
}

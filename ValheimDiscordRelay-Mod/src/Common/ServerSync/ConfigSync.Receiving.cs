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
	private void RPC_FromServerConfigSync(ZRpc rpc, ZPackage package)
	{
		lockedConfigChanged += serverLockedSettingChanged;
		IsSourceOfTruth = false;

		if (HandleConfigSyncRPC(0, package, false))
		{
			InitialSyncDone = true;
		}
	}

	private void RPC_FromOtherClientConfigSync(long sender, ZPackage package) => HandleConfigSyncRPC(sender, package, true);

	private bool HandleConfigSyncRPC(long sender, ZPackage package, bool clientUpdate)
	{
		try
		{
			if (isServer && IsLocked && SnatchCurrentlyHandlingRPC.currentRpc?.GetSocket()?.GetHostName() is { } client)
			{
				MethodInfo? listContainsId = AccessTools.DeclaredMethod(typeof(ZNet), "ListContainsId");
				SyncedList adminList = (SyncedList)AccessTools.DeclaredField(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
				bool exempt = listContainsId is null ? adminList.Contains(client) : (bool)listContainsId.Invoke(ZNet.instance, new object[] { adminList, client });
				if (!exempt)
				{
					return false;
				}
			}

			cacheExpirations.RemoveAll(kv =>
			{
				if (kv.Key < DateTimeOffset.Now.Ticks)
				{
					configValueCache.Remove(kv.Value);
					return true;
				}

				return false;
			});

			byte packageFlags = package.ReadByte();

			if ((packageFlags & FRAGMENTED_CONFIG) != 0)
			{
				long uniqueIdentifier = package.ReadLong();
				string cacheKey = sender.ToString() + uniqueIdentifier;
				if (!configValueCache.TryGetValue(cacheKey, out SortedDictionary<int, byte[]> dataFragments))
				{
					dataFragments = new SortedDictionary<int, byte[]>();
					configValueCache[cacheKey] = dataFragments;
					cacheExpirations.Add(new KeyValuePair<long, string>(DateTimeOffset.Now.AddSeconds(60).Ticks, cacheKey));
				}

				int fragment = package.ReadInt();
				int fragments = package.ReadInt();

				dataFragments.Add(fragment, package.ReadByteArray());

				if (dataFragments.Count < fragments)
				{
					return false;
				}

				configValueCache.Remove(cacheKey);

				package = new ZPackage(dataFragments.Values.SelectMany(a => a).ToArray());
				packageFlags = package.ReadByte();
			}

			ProcessingServerUpdate = true;

			if ((packageFlags & COMPRESSED_CONFIG) != 0)
			{
				byte[] data = package.ReadByteArray();

				MemoryStream input = new(data);
				MemoryStream output = new();
				using (DeflateStream deflateStream = new(input, CompressionMode.Decompress))
				{
					deflateStream.CopyTo(output);
				}

				package = new ZPackage(output.ToArray());
				packageFlags = package.ReadByte();
			}

			if ((packageFlags & PARTIAL_CONFIGS) == 0)
			{
				resetConfigsFromServer();
			}

			ParsedConfigs configs = ReadConfigsFromPackage(package);

			ConfigFile? configFile = null;
			bool originalSaveOnConfigSet = false;
			foreach (KeyValuePair<OwnConfigEntryBase, object?> configKv in configs.configValues)
			{
				if (!isServer && configKv.Key.LocalBaseValue == null)
				{
					configKv.Key.LocalBaseValue = configKv.Key.BaseConfig.BoxedValue;
				}

				if (configFile is null)
				{
					configFile = configKv.Key.BaseConfig.ConfigFile;
					originalSaveOnConfigSet = configFile.SaveOnConfigSet;
					configFile.SaveOnConfigSet = false;
				}
				configKv.Key.BaseConfig.BoxedValue = configKv.Value;
			}
			if (configFile is not null)
			{
				configFile.SaveOnConfigSet = originalSaveOnConfigSet;
				configFile.Save();
			}

			foreach (KeyValuePair<CustomSyncedValueBase, object?> configKv in configs.customValues)
			{
				if (!isServer)
				{
					configKv.Key.LocalBaseValue ??= configKv.Key.BoxedValue;
				}

				configKv.Key.BoxedValue = configKv.Value;
			}

			Debug.Log($"Received {configs.configValues.Count} configs and {configs.customValues.Count} custom values from {(isServer || clientUpdate ? $"client {sender}" : "the server")} for mod {DisplayName ?? Name}");

			if (!isServer)
			{
				serverLockedSettingChanged(); // Re-evaluate for intial locking
			}

			return true;
		}
		finally
		{
			ProcessingServerUpdate = false;
		}
	}

	private class ParsedConfigs
	{
		public readonly Dictionary<OwnConfigEntryBase, object?> configValues = new();
		public readonly Dictionary<CustomSyncedValueBase, object?> customValues = new();
	}

	private ParsedConfigs ReadConfigsFromPackage(ZPackage package)
	{
		ParsedConfigs configs = new();
		Dictionary<string, OwnConfigEntryBase> configMap = allConfigs.Where(c => c.SynchronizedConfig).ToDictionary(c => c.BaseConfig.Definition.Section + "_" + c.BaseConfig.Definition.Key, c => c);

		Dictionary<string, CustomSyncedValueBase> customValueMap = allCustomValues.ToDictionary(c => c.Identifier, c => c);

		int valueCount = package.ReadInt();
		for (int i = 0; i < valueCount; ++i)
		{
			string groupName = package.ReadString();
			string configName = package.ReadString();
			string typeName = package.ReadString();

			Type? type = Type.GetType(typeName);
			if (typeName == "" || type != null)
			{
				object? value;
				try
				{
					value = typeName == "" ? null : ReadValueWithTypeFromZPackage(package, type!);
				}
				catch (InvalidDeserializationTypeException e)
				{
					Debug.LogWarning($"Got unexpected struct internal type {e.received} for field {e.field} struct {typeName} for {configName} in section {groupName} for mod {DisplayName ?? Name}, expecting {e.expected}");
					continue;
				}
				if (groupName == "Internal")
				{
					if (configName == "serverversion")
					{
						if (value?.ToString() != CurrentVersion)
						{
							Debug.LogWarning($"Received server version is not equal: server version = {value?.ToString() ?? "null"}; local version = {CurrentVersion ?? "unknown"}");
						}
					}
					else if (configName == "lockexempt")
					{
						if (value is bool exempt)
						{
							lockExempt = exempt;
						}
					}
					else if (customValueMap.TryGetValue(configName, out CustomSyncedValueBase config))
					{
						if ((typeName == "" && (!config.Type.IsValueType || Nullable.GetUnderlyingType(config.Type) != null)) || GetZPackageTypeString(config.Type) == typeName)
						{
							configs.customValues[config] = value;
						}
						else
						{
							Debug.LogWarning($"Got unexpected type {typeName} for internal value {configName} for mod {DisplayName ?? Name}, expecting {config.Type.AssemblyQualifiedName}");
						}
					}
				}
				else if (configMap.TryGetValue(groupName + "_" + configName, out OwnConfigEntryBase config))
				{
					Type expectedType = configType(config.BaseConfig);
					if ((typeName == "" && (!expectedType.IsValueType || Nullable.GetUnderlyingType(expectedType) != null)) || GetZPackageTypeString(expectedType) == typeName)
					{
						configs.configValues[config] = value;
					}
					else
					{
						Debug.LogWarning($"Got unexpected type {typeName} for {configName} in section {groupName} for mod {DisplayName ?? Name}, expecting {expectedType.AssemblyQualifiedName}");
					}
				}
				else
				{
					Debug.LogWarning($"Received unknown config entry {configName} in section {groupName} for mod {DisplayName ?? Name}. This may happen if client and server versions of the mod do not match.");
				}
			}
			else
			{
				Debug.LogWarning($"Got invalid type {typeName}, abort reading of received configs");
				return new ParsedConfigs();
			}
		}

		return configs;
	}
}

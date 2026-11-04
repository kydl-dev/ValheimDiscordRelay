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
    private static ZPackage ConfigsToPackage(IEnumerable<ConfigEntryBase>? configs = null, IEnumerable<CustomSyncedValueBase>? customValues = null, IEnumerable<PackageEntry>? packageEntries = null, bool partial = true)
    {
        List<ConfigEntryBase> configList = configs?.Where(config => configData(config)!.SynchronizedConfig).ToList() ?? new List<ConfigEntryBase>();
        List<CustomSyncedValueBase> customValueList = customValues?.ToList() ?? new List<CustomSyncedValueBase>();
        ZPackage package = new();
        package.Write(partial ? PARTIAL_CONFIGS : (byte)0);
        package.Write(configList.Count + customValueList.Count + (packageEntries?.Count() ?? 0));
        foreach (PackageEntry packageEntry in packageEntries ?? Array.Empty<PackageEntry>())
        {
            AddEntryToPackage(package, packageEntry);
        }
        foreach (CustomSyncedValueBase customValue in customValueList)
        {
            AddEntryToPackage(package, new PackageEntry { section = "Internal", key = customValue.Identifier, type = customValue.Type, value = customValue.BoxedValue });
        }
        foreach (ConfigEntryBase config in configList)
        {
            AddEntryToPackage(package, new PackageEntry { section = config.Definition.Section, key = config.Definition.Key, type = configType(config), value = config.BoxedValue });
        }

        return package;
    }

    private static void AddEntryToPackage(ZPackage package, PackageEntry entry)
    {
        package.Write(entry.section);
        package.Write(entry.key);
        package.Write(entry.value == null ? "" : GetZPackageTypeString(entry.type));
        AddValueToZPackage(package, entry.value);
    }

    private static string GetZPackageTypeString(Type type) => type.AssemblyQualifiedName!;

    private static void AddValueToZPackage(ZPackage package, object? value)
    {
        Type? type = value?.GetType();
        if (value is Enum)
        {
            value = ((IConvertible)value).ToType(Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
        }
        else if (value is ICollection collection)
        {
            package.Write(collection.Count);
            foreach (object item in collection)
            {
                AddValueToZPackage(package, item);
            }
            return;
        }
        else if (type is { IsValueType: true, IsPrimitive: false })
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            package.Write(fields.Length);
            foreach (FieldInfo field in fields)
            {
                package.Write(GetZPackageTypeString(field.FieldType));
                AddValueToZPackage(package, field.GetValue(value));
            }
            return;
        }

        ZRpc.Serialize(new[] { value }, ref package);
    }

    private static object ReadValueWithTypeFromZPackage(ZPackage package, Type type)
    {
        if (type is { IsValueType: true, IsPrimitive: false, IsEnum: false })
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int fieldCount = package.ReadInt();
            if (fieldCount != fields.Length)
            {
                throw new InvalidDeserializationTypeException { received = $"(field count: {fieldCount})", expected = $"(field count: {fields.Length})" };
            }

            object value = FormatterServices.GetUninitializedObject(type);
            foreach (FieldInfo field in fields)
            {
                string typeName = package.ReadString();
                if (typeName != GetZPackageTypeString(field.FieldType))
                {
                    throw new InvalidDeserializationTypeException { received = typeName, expected = GetZPackageTypeString(field.FieldType), field = field.Name };
                }
                field.SetValue(value, ReadValueWithTypeFromZPackage(package, field.FieldType));
            }
            return value;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            int entriesCount = package.ReadInt();
            IDictionary dict = (IDictionary)Activator.CreateInstance(type);
            Type kvType = typeof(KeyValuePair<,>).MakeGenericType(type.GenericTypeArguments);
            FieldInfo keyField = kvType.GetField("key", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo valueField = kvType.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int i = 0; i < entriesCount; ++i)
            {
                object kv = ReadValueWithTypeFromZPackage(package, kvType);
                dict.Add(keyField.GetValue(kv), valueField.GetValue(kv));
            }
            return dict;
        }
        if (type != typeof(List<string>) && type.IsGenericType && typeof(ICollection<>).MakeGenericType(type.GenericTypeArguments[0]) is { } collectionType && collectionType.IsAssignableFrom(type))
        {
            int entriesCount = package.ReadInt();
            object list = Activator.CreateInstance(type);
            MethodInfo adder = collectionType.GetMethod("Add")!;
            for (int i = 0; i < entriesCount; ++i)
            {
                adder.Invoke(list, new[] { ReadValueWithTypeFromZPackage(package, type.GenericTypeArguments[0]) });
            }
            return list;
        }

        ParameterInfo param = (ParameterInfo)FormatterServices.GetUninitializedObject(typeof(ParameterInfo));
        AccessTools.DeclaredField(typeof(ParameterInfo), "ClassImpl").SetValue(param, type);
        List<object> data = new();
        ZRpc.Deserialize(new[] { null, param }, package, ref data);
        return data.First();
    }

    private class InvalidDeserializationTypeException : Exception
    {
        public string expected = null!;
        public string received = null!;
        public string field = "";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Phoretell.Editor
{
    internal sealed class SaveDataTypeInfo
    {
        public Type DataType { get; }
        public IReadOnlyList<Type> ProviderTypes { get; }
        public IReadOnlyList<MonoScript> ProviderScripts { get; }
        public MonoScript Script { get; }
        public string AssetPath { get; }
        public bool HasExactDataScript { get; }
        public bool ProvidersAreExclusive { get; }

        public string ClassName => DataType.Name;
        public string Namespace => string.IsNullOrEmpty(DataType.Namespace)
            ? "(global namespace)"
            : DataType.Namespace;
        public string BaseTypeName => DataType.BaseType == null
            ? "None"
            : GetFriendlyTypeName(DataType.BaseType);
        public string ContractName =>
            $"{typeof(ISaveLoad<>).Namespace}.{nameof(ISaveLoad<object>).Split('`')[0]}" +
            $"<{GetFriendlyTypeName(DataType)}>";
        public bool CanDelete =>
            HasExactDataScript &&
            !string.IsNullOrEmpty(AssetPath) &&
            AssetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
            ProvidersAreExclusive &&
            ProviderTypes.Count == ProviderScripts.Count &&
            ProviderScripts.All(providerScript =>
                AssetDatabase.GetAssetPath(providerScript).StartsWith(
                    "Assets/",
                    StringComparison.Ordinal));

        public SaveDataTypeInfo(
            Type dataType,
            IReadOnlyList<Type> providerTypes,
            IReadOnlyList<MonoScript> providerScripts,
            MonoScript script,
            string assetPath,
            bool hasExactDataScript,
            bool providersAreExclusive)
        {
            DataType = dataType;
            ProviderTypes = providerTypes;
            ProviderScripts = providerScripts;
            Script = script;
            AssetPath = assetPath;
            HasExactDataScript = hasExactDataScript;
            ProvidersAreExclusive = providersAreExclusive;
        }

        public string GetProviderSummary()
        {
            return string.Join(
                ", ",
                ProviderTypes.Select(GetFriendlyTypeName));
        }

        public IReadOnlyList<string> GetDeletionAssetPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal)
            {
                AssetPath
            };

            foreach (MonoScript providerScript in ProviderScripts)
            {
                paths.Add(AssetDatabase.GetAssetPath(providerScript));
            }

            return paths.OrderBy(path => path).ToArray();
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
            {
                name = name.Substring(0, tickIndex);
            }

            return name + "<" + string.Join(
                ", ",
                type.GetGenericArguments().Select(GetFriendlyTypeName)) + ">";
        }
    }

    internal static class SaveDataTypeDiscovery
    {
        public static List<SaveDataTypeInfo> Discover()
        {
            var providersByDataType = new Dictionary<Type, HashSet<Type>>();

            // TypeCache is populated by Unity after compilation and avoids scanning
            // every loaded assembly whenever the window repaints.
            foreach (Type providerType in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
            {
                if (providerType.IsAbstract || providerType.ContainsGenericParameters)
                {
                    continue;
                }

                foreach (Type interfaceType in providerType.GetInterfaces())
                {
                    if (!interfaceType.IsGenericType ||
                        interfaceType.GetGenericTypeDefinition() != typeof(ISaveLoad<>))
                    {
                        continue;
                    }

                    Type dataType = interfaceType.GetGenericArguments()[0];
                    if (!IsValidDataType(dataType))
                    {
                        continue;
                    }

                    HashSet<Type> providers;
                    if (!providersByDataType.TryGetValue(dataType, out providers))
                    {
                        providers = new HashSet<Type>();
                        providersByDataType.Add(dataType, providers);
                    }

                    providers.Add(providerType);
                }
            }

            Dictionary<Type, MonoScript> scriptsByType = BuildScriptMap();
            var result = new List<SaveDataTypeInfo>();

            foreach (KeyValuePair<Type, HashSet<Type>> pair in providersByDataType)
            {
                MonoScript script;
                bool hasExactDataScript = scriptsByType.TryGetValue(pair.Key, out script);
                var providerScripts = new List<MonoScript>();

                foreach (Type providerType in pair.Value)
                {
                    MonoScript providerScript;
                    if (scriptsByType.TryGetValue(providerType, out providerScript))
                    {
                        providerScripts.Add(providerScript);
                    }
                }

                if (script == null)
                {
                    foreach (MonoScript providerScript in providerScripts)
                    {
                        script = providerScript;
                        break;
                    }
                }

                string assetPath = script == null
                    ? string.Empty
                    : AssetDatabase.GetAssetPath(script);
                bool providersAreExclusive = pair.Value.All(providerType =>
                    providerType.GetInterfaces().Count(interfaceType =>
                        interfaceType.IsGenericType &&
                        interfaceType.GetGenericTypeDefinition() == typeof(ISaveLoad<>)) == 1);

                result.Add(new SaveDataTypeInfo(
                    pair.Key,
                    pair.Value.OrderBy(type => type.FullName).ToArray(),
                    providerScripts
                        .Distinct()
                        .OrderBy(AssetDatabase.GetAssetPath)
                        .ToArray(),
                    script,
                    assetPath,
                    hasExactDataScript,
                    providersAreExclusive));
            }

            result.Sort((left, right) => string.Compare(
                left.DataType.FullName,
                right.DataType.FullName,
                StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static bool IsValidDataType(Type type)
        {
            return type != null &&
                   type.IsClass &&
                   !type.IsAbstract &&
                   !type.ContainsGenericParameters &&
                   type.IsSerializable &&
                   type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static Dictionary<Type, MonoScript> BuildScriptMap()
        {
            var result = new Dictionary<Type, MonoScript>();

            foreach (MonoScript script in MonoImporter.GetAllRuntimeMonoScripts())
            {
                if (script == null)
                {
                    continue;
                }

                Type type = script.GetClass();
                if (type != null && !result.ContainsKey(type))
                {
                    result.Add(type, script);
                }
            }

            return result;
        }
    }
}

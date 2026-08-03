using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Phoretell
{
    /// <summary>
    /// Builds and caches exact-name, exact-type field bindings between a project
    /// save-data class and a MonoBehaviour selected by a generated save provider.
    /// </summary>
    /// <typeparam name="TData">A project-owned serializable reference type.</typeparam>
    public sealed class SaveDataFieldMapper<TData> where TData : class
    {
        private const BindingFlags DeclaredInstanceFields =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        private readonly MonoBehaviour provider;
        private readonly List<FieldBinding> bindings = new List<FieldBinding>();
        private bool missingRuntimeTargetWarningReported;

        public MonoBehaviour Target { get; }
        public int BindingCount => bindings.Count;

        /// <summary>
        /// Resolves the target and reflects all field metadata once. Construct this
        /// from the provider's Awake method, then reuse it for every save and load.
        /// </summary>
        public SaveDataFieldMapper(
            MonoBehaviour provider,
            MonoBehaviour manuallyAssignedTarget)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            this.provider = provider;

            FieldCatalog dataFields = BuildFieldCatalog(typeof(TData));
            WarnAboutDuplicateDataFields(dataFields);

            if (dataFields.UniqueFields.Count == 0)
            {
                Warn(
                    $"Save data type '{GetTypeName(typeof(TData))}' has no supported fields. " +
                    "Add public fields or private [SerializeField] fields before using its provider.");
            }

            Target = ResolveTarget(manuallyAssignedTarget, dataFields);
            if (Target == null)
            {
                Warn(
                    $"No matching target MonoBehaviour was found on GameObject " +
                    $"'{provider.gameObject.name}' for save data type " +
                    $"'{GetTypeName(typeof(TData))}'. Assign Target manually or add a component " +
                    "with fields whose names and types match the save data.");
                return;
            }

            BuildBindings(dataFields, BuildFieldCatalog(Target.GetType()));
        }

        /// <summary>Copies cached target-field values into the supplied data object.</summary>
        public void CopyTargetToData(TData data)
        {
            if (data == null)
            {
                Warn("Cannot save into a null data object.");
                return;
            }

            if (!EnsureRuntimeTarget())
            {
                return;
            }

            foreach (FieldBinding binding in bindings)
            {
                try
                {
                    binding.DataField.SetValue(
                        data,
                        binding.TargetField.GetValue(Target));
                }
                catch (Exception exception)
                {
                    WarnBindingFailure("save", binding, exception);
                }
            }
        }

        /// <summary>Copies cached data-field values back into the selected target.</summary>
        public void CopyDataToTarget(TData data)
        {
            if (data == null)
            {
                Warn("Cannot load from a null data object.");
                return;
            }

            if (!EnsureRuntimeTarget())
            {
                return;
            }

            foreach (FieldBinding binding in bindings)
            {
                try
                {
                    binding.TargetField.SetValue(
                        Target,
                        binding.DataField.GetValue(data));
                }
                catch (Exception exception)
                {
                    WarnBindingFailure("load", binding, exception);
                }
            }
        }

        private MonoBehaviour ResolveTarget(
            MonoBehaviour manuallyAssignedTarget,
            FieldCatalog dataFields)
        {
            if (manuallyAssignedTarget != null)
            {
                if (ReferenceEquals(manuallyAssignedTarget, provider))
                {
                    Warn(
                        "The provider cannot target itself. Automatic target detection will be used.");
                }
                else if (manuallyAssignedTarget.gameObject != provider.gameObject)
                {
                    Warn(
                        $"Manually assigned target '{GetTypeName(manuallyAssignedTarget.GetType())}' " +
                        "is not on the provider's GameObject. Automatic target detection will be used.");
                }
                else
                {
                    return manuallyAssignedTarget;
                }
            }

            MonoBehaviour[] components = provider.GetComponents<MonoBehaviour>();
            var candidates = new List<TargetCandidate>();

            for (int index = 0; index < components.Length; index++)
            {
                MonoBehaviour component = components[index];
                if (component == null || ReferenceEquals(component, provider))
                {
                    continue;
                }

                FieldCatalog targetFields = BuildFieldCatalog(component.GetType());
                candidates.Add(new TargetCandidate(
                    component,
                    index,
                    CountExactMatches(dataFields, targetFields),
                    targetFields));
            }

            int highestScore = candidates.Count == 0
                ? 0
                : candidates.Max(candidate => candidate.MatchCount);
            if (highestScore <= 0)
            {
                WarnAutomaticDetectionTypeMismatches(dataFields, candidates);
                return null;
            }

            List<TargetCandidate> bestCandidates = candidates
                .Where(candidate => candidate.MatchCount == highestScore)
                .ToList();

            if (bestCandidates.Count > 1)
            {
                string candidateNames = string.Join(
                    ", ",
                    bestCandidates.Select(candidate => candidate.GetDisplayName()));
                Warn(
                    $"Automatic target detection is ambiguous: {bestCandidates.Count} components " +
                    $"each have {highestScore} matching fields ({candidateNames}). " +
                    $"Using {bestCandidates[0].GetDisplayName()}; assign Target manually to override this choice.");
            }

            return bestCandidates[0].Component;
        }

        private void BuildBindings(
            FieldCatalog dataFields,
            FieldCatalog targetFields)
        {
            foreach (FieldInfo dataField in dataFields.UniqueFields)
            {
                List<FieldInfo> sameNameTargetFields;
                if (!targetFields.ByName.TryGetValue(
                        dataField.Name,
                        out sameNameTargetFields))
                {
                    Warn(
                        $"Target '{GetTypeName(Target.GetType())}' is missing field " +
                        $"'{dataField.Name}' required by '{GetTypeName(typeof(TData))}'.");
                    continue;
                }

                List<FieldInfo> exactMatches = sameNameTargetFields
                    .Where(field => field.FieldType == dataField.FieldType)
                    .ToList();

                if (exactMatches.Count == 0)
                {
                    Warn(
                        $"Field type mismatch for '{dataField.Name}': save data uses " +
                        $"'{GetTypeName(dataField.FieldType)}', but target " +
                        $"'{GetTypeName(Target.GetType())}' exposes " +
                        $"{FormatFieldTypes(sameNameTargetFields)}.");
                    continue;
                }

                if (exactMatches.Count > 1)
                {
                    Warn(
                        $"Target '{GetTypeName(Target.GetType())}' contains multiple eligible " +
                        $"fields named '{dataField.Name}' with type " +
                        $"'{GetTypeName(dataField.FieldType)}'. The ambiguous field is not mapped.");
                    continue;
                }

                bindings.Add(new FieldBinding(dataField, exactMatches[0]));
            }
        }

        private void WarnAboutDuplicateDataFields(FieldCatalog dataFields)
        {
            foreach (KeyValuePair<string, List<FieldInfo>> pair in dataFields.ByName)
            {
                if (pair.Value.Count <= 1)
                {
                    continue;
                }

                Warn(
                    $"Save data type '{GetTypeName(typeof(TData))}' contains multiple eligible " +
                    $"fields named '{pair.Key}'. Duplicate or hidden fields are ambiguous and will not be mapped.");
            }
        }

        private void WarnAutomaticDetectionTypeMismatches(
            FieldCatalog dataFields,
            List<TargetCandidate> candidates)
        {
            var mismatches = new List<string>();

            foreach (TargetCandidate candidate in candidates)
            {
                foreach (FieldInfo dataField in dataFields.UniqueFields)
                {
                    List<FieldInfo> targetFields;
                    if (!candidate.Fields.ByName.TryGetValue(dataField.Name, out targetFields) ||
                        targetFields.Any(field => field.FieldType == dataField.FieldType))
                    {
                        continue;
                    }

                    mismatches.Add(
                        $"{candidate.GetDisplayName()}.{dataField.Name}: expected " +
                        $"{GetTypeName(dataField.FieldType)}, found {FormatFieldTypes(targetFields)}");
                }
            }

            if (mismatches.Count > 0)
            {
                Warn(
                    "Automatic target detection found same-name fields with incompatible types: " +
                    string.Join("; ", mismatches));
            }
        }

        private bool EnsureRuntimeTarget()
        {
            if (Target != null)
            {
                return true;
            }

            if (!missingRuntimeTargetWarningReported)
            {
                Warn(
                    "Save/load field copying was skipped because no valid target is available.");
                missingRuntimeTargetWarningReported = true;
            }

            return false;
        }

        private void WarnBindingFailure(
            string operation,
            FieldBinding binding,
            Exception exception)
        {
            Warn(
                $"Could not {operation} mapped field '{binding.DataField.Name}' between " +
                $"'{GetTypeName(typeof(TData))}' and '{GetTypeName(Target.GetType())}'. " +
                $"{exception.GetBaseException().Message}");
        }

        private static int CountExactMatches(
            FieldCatalog dataFields,
            FieldCatalog targetFields)
        {
            int count = 0;

            foreach (FieldInfo dataField in dataFields.UniqueFields)
            {
                List<FieldInfo> fields;
                if (!targetFields.ByName.TryGetValue(dataField.Name, out fields))
                {
                    continue;
                }

                if (fields.Count(field => field.FieldType == dataField.FieldType) == 1)
                {
                    count++;
                }
            }

            return count;
        }

        private static FieldCatalog BuildFieldCatalog(Type type)
        {
            var fieldsByName = new Dictionary<string, List<FieldInfo>>(
                StringComparer.Ordinal);

            for (Type current = type;
                 current != null && current != typeof(object);
                 current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(DeclaredInstanceFields))
                {
                    if (!IsSupportedField(field))
                    {
                        continue;
                    }

                    List<FieldInfo> sameNameFields;
                    if (!fieldsByName.TryGetValue(field.Name, out sameNameFields))
                    {
                        sameNameFields = new List<FieldInfo>();
                        fieldsByName.Add(field.Name, sameNameFields);
                    }

                    sameNameFields.Add(field);
                }
            }

            return new FieldCatalog(fieldsByName);
        }

        private static bool IsSupportedField(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral || field.IsNotSerialized)
            {
                return false;
            }

            return field.IsPublic || field.IsDefined(typeof(SerializeField), true);
        }

        private static string FormatFieldTypes(IEnumerable<FieldInfo> fields)
        {
            return string.Join(
                ", ",
                fields
                    .Select(field => $"'{GetTypeName(field.FieldType)}'")
                    .Distinct());
        }

        private static string GetTypeName(Type type)
        {
            return type.FullName ?? type.Name;
        }

        private void Warn(string message)
        {
            Debug.LogWarning(
                $"[{provider.GetType().Name}] {message}",
                provider);
        }

        private sealed class FieldBinding
        {
            public FieldInfo DataField { get; }
            public FieldInfo TargetField { get; }

            public FieldBinding(FieldInfo dataField, FieldInfo targetField)
            {
                DataField = dataField;
                TargetField = targetField;
            }
        }

        private sealed class FieldCatalog
        {
            public Dictionary<string, List<FieldInfo>> ByName { get; }
            public IReadOnlyList<FieldInfo> UniqueFields { get; }

            public FieldCatalog(Dictionary<string, List<FieldInfo>> byName)
            {
                ByName = byName;
                UniqueFields = byName.Values
                    .Where(fields => fields.Count == 1)
                    .Select(fields => fields[0])
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        private sealed class TargetCandidate
        {
            public MonoBehaviour Component { get; }
            public int ComponentIndex { get; }
            public int MatchCount { get; }
            public FieldCatalog Fields { get; }

            public TargetCandidate(
                MonoBehaviour component,
                int componentIndex,
                int matchCount,
                FieldCatalog fields)
            {
                Component = component;
                ComponentIndex = componentIndex;
                MatchCount = matchCount;
                Fields = fields;
            }

            public string GetDisplayName()
            {
                return $"{GetTypeName(Component.GetType())} (component index {ComponentIndex})";
            }
        }
    }
}

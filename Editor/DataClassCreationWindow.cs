using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Phoretell.Editor
{
    internal sealed class DataClassCreationWindow : EditorWindow
    {
        private const string LastFolderPreference =
            "Phoretell.DataPersistence.LastDataClassFolder";
        private const string LastNamespacePreference =
            "Phoretell.DataPersistence.LastDataClassNamespace";
        private const string DefaultFolder = "Assets/Save Data";

        private static readonly HashSet<string> ReservedKeywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
            "lock", "long", "namespace", "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public", "readonly", "ref",
            "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
            "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while"
        };

        private DataPersistenceWindow owner;
        private HashSet<string> existingFullTypeNames;
        private string className = "NewGameData";
        private string providerClassName = "NewGameDataProvider";
        private string classNamespace;
        private string destinationFolder;
        private string description = "Serializable data saved for this game system.";
        private string statusMessage;
        private MessageType statusType;
        private Vector2 scrollPosition;
        private bool providerNameWasAutomatic = true;

        public static void ShowWindow(DataPersistenceWindow owner)
        {
            DataClassCreationWindow window = CreateInstance<DataClassCreationWindow>();
            window.owner = owner;
            window.titleContent = new GUIContent("Add Save Data Class");
            window.minSize = new Vector2(500f, 440f);
            window.maxSize = new Vector2(720f, 650f);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            classNamespace = EditorPrefs.GetString(
                LastNamespacePreference,
                "Phoretell");
            destinationFolder = EditorPrefs.GetString(
                LastFolderPreference,
                DefaultFolder);
            existingFullTypeNames = CollectExistingTypeNames();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.LabelField("Create Save Data Class", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The runtime discovers a data class through ISaveLoad<TData>. This creates " +
                "a serializable data class and a companion MonoBehaviour provider component.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            string nextClassName = EditorGUILayout.TextField("Class Name", className);
            if (EditorGUI.EndChangeCheck())
            {
                className = nextClassName.Trim();
                if (providerNameWasAutomatic)
                {
                    providerClassName = className + "Provider";
                }
            }

            EditorGUI.BeginChangeCheck();
            providerClassName = EditorGUILayout.TextField(
                "Provider Class",
                providerClassName).Trim();
            if (EditorGUI.EndChangeCheck())
            {
                providerNameWasAutomatic =
                    providerClassName == className + "Provider";
            }

            classNamespace = EditorGUILayout.TextField(
                "Namespace",
                classNamespace).Trim();

            EditorGUILayout.BeginHorizontal();
            destinationFolder = EditorGUILayout.TextField(
                "Destination Folder",
                destinationFolder).Trim().Replace('\\', '/');
            if (GUILayout.Button("Browse...", GUILayout.Width(80f)))
            {
                BrowseForFolder();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Description");
            description = EditorGUILayout.TextArea(
                description,
                GUILayout.MinHeight(70f));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated Files", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                Path.Combine(destinationFolder, className + ".cs"),
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                Path.Combine(destinationFolder, providerClassName + ".cs"),
                EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.HelpBox(statusMessage, statusType);
            }

            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }

            GUI.enabled = !EditorApplication.isCompiling;
            if (GUILayout.Button("Create", GUILayout.Height(26f)))
            {
                CreateScripts();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void BrowseForFolder()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string currentAbsolute = string.IsNullOrEmpty(destinationFolder)
                ? Application.dataPath
                : Path.GetFullPath(Path.Combine(projectRoot, destinationFolder));
            string selected = EditorUtility.OpenFolderPanel(
                "Choose Save Data Folder",
                currentAbsolute,
                string.Empty);

            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            string relative = FileUtil.GetProjectRelativePath(selected);
            if (string.IsNullOrEmpty(relative) ||
                !relative.StartsWith("Assets", StringComparison.Ordinal))
            {
                SetStatus(
                    "Save-data classes are project-owned and must be created inside this " +
                    "project's Assets folder.",
                    MessageType.Error);
                return;
            }

            destinationFolder = relative.Replace('\\', '/').TrimEnd('/');
        }

        private void CreateScripts()
        {
            string validationError = ValidateInput();
            if (!string.IsNullOrEmpty(validationError))
            {
                SetStatus(validationError, MessageType.Error);
                return;
            }

            EnsureAssetFolder(destinationFolder);
            string dataAssetPath = destinationFolder + "/" + className + ".cs";
            string providerAssetPath = destinationFolder + "/" + providerClassName + ".cs";
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string dataFullPath = Path.Combine(projectRoot, dataAssetPath);
            string providerFullPath = Path.Combine(projectRoot, providerAssetPath);

            try
            {
                File.WriteAllText(
                    dataFullPath,
                    BuildDataClassSource(),
                    new UTF8Encoding(false));
                File.WriteAllText(
                    providerFullPath,
                    BuildProviderSource(),
                    new UTF8Encoding(false));

                EditorPrefs.SetString(LastFolderPreference, destinationFolder);
                EditorPrefs.SetString(LastNamespacePreference, classNamespace);
                AssetDatabase.ImportAsset(
                    dataAssetPath,
                    ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(
                    providerAssetPath,
                    ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                MonoScript dataScript = AssetDatabase.LoadAssetAtPath<MonoScript>(
                    dataAssetPath);
                Selection.activeObject = dataScript;
                if (dataScript != null)
                {
                    EditorGUIUtility.PingObject(dataScript);
                }

                owner?.NotifyScriptsCreated(className, dataAssetPath);
                Close();
            }
            catch (Exception exception)
            {
                if (File.Exists(dataFullPath) && !File.Exists(providerFullPath))
                {
                    File.Delete(dataFullPath);
                }

                AssetDatabase.Refresh();
                SetStatus(
                    "Could not create the save-data scripts. " + exception.Message,
                    MessageType.Error);
                Debug.LogException(exception);
            }
        }

        private string ValidateInput()
        {
            if (!IsValidIdentifier(className))
            {
                return $"'{className}' is not a valid, non-keyword C# class name.";
            }

            if (!IsValidIdentifier(providerClassName))
            {
                return $"'{providerClassName}' is not a valid, non-keyword C# provider name.";
            }

            if (className == providerClassName)
            {
                return "The data class and provider class must have different names.";
            }

            if (!IsValidNamespace(classNamespace))
            {
                return $"'{classNamespace}' is not a valid C# namespace.";
            }

            if (string.IsNullOrEmpty(destinationFolder) ||
                (destinationFolder != "Assets" &&
                 !destinationFolder.StartsWith("Assets/", StringComparison.Ordinal)))
            {
                return "The destination must be inside the project's Assets folder.";
            }

            string fullDataTypeName = classNamespace + "." + className;
            string fullProviderTypeName = classNamespace + "." + providerClassName;
            if (existingFullTypeNames.Contains(fullDataTypeName) ||
                existingFullTypeNames.Contains(fullProviderTypeName))
            {
                return "A type with the requested namespace and class name already exists.";
            }

            string dataAssetPath = destinationFolder + "/" + className + ".cs";
            string providerAssetPath = destinationFolder + "/" + providerClassName + ".cs";
            if (File.Exists(ToFullProjectPath(dataAssetPath)) ||
                File.Exists(ToFullProjectPath(providerAssetPath)))
            {
                return "One of the generated script paths already exists.";
            }

            return string.Empty;
        }

        private string BuildDataClassSource()
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System;");
            builder.AppendLine();
            builder.AppendLine($"namespace {classNamespace}");
            builder.AppendLine("{");
            AppendSummary(builder, description, 1);
            builder.AppendLine("    [Serializable]");
            builder.AppendLine($"    public sealed class {className}");
            builder.AppendLine("    {");
            builder.AppendLine("        // Add public fields or [UnityEngine.SerializeField] fields supported by JsonUtility.");
            builder.AppendLine();
            builder.AppendLine($"        public {className}()");
            builder.AppendLine("        {");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private string BuildProviderSource()
        {
            var builder = new StringBuilder();
            builder.AppendLine("using Phoretell;");
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine();
            builder.AppendLine($"namespace {classNamespace}");
            builder.AppendLine("{");
            AppendSummary(
                builder,
                $"Maps matching serialized fields between a target component and {className}.",
                1);
            builder.AppendLine($"    public sealed class {providerClassName} : MonoBehaviour, ISaveLoad<{className}>");
            builder.AppendLine("    {");
            builder.AppendLine("        [SerializeField] private MonoBehaviour target;");
            builder.AppendLine($"        private SaveDataFieldMapper<{className}> fieldMapper;");
            builder.AppendLine();
            builder.AppendLine("        private void Awake()");
            builder.AppendLine("        {");
            builder.AppendLine("            CacheFieldBindings();");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        public void SaveData({className} data)");
            builder.AppendLine("        {");
            builder.AppendLine("            EnsureFieldMapper().CopyTargetToData(data);");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        public void LoadData({className} data)");
            builder.AppendLine("        {");
            builder.AppendLine("            EnsureFieldMapper().CopyDataToTarget(data);");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        private void CacheFieldBindings()");
            builder.AppendLine("        {");
            builder.AppendLine($"            fieldMapper = new SaveDataFieldMapper<{className}>(this, target);");
            builder.AppendLine("            target = fieldMapper.Target;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        private SaveDataFieldMapper<{className}> EnsureFieldMapper()");
            builder.AppendLine("        {");
            builder.AppendLine("            if (fieldMapper == null)");
            builder.AppendLine("            {");
            builder.AppendLine("                CacheFieldBindings();");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return fieldMapper;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void AppendSummary(
            StringBuilder builder,
            string value,
            int indentationLevel)
        {
            string indentation = new string(' ', indentationLevel * 4);
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "Project-owned save data."
                : value.Trim();

            normalized = normalized
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

            builder.AppendLine(indentation + "/// <summary>");
            foreach (string line in normalized.Split(
                         new[] { "\r\n", "\r", "\n" },
                         StringSplitOptions.None))
            {
                builder.AppendLine(indentation + "/// " + line.Trim());
            }
            builder.AppendLine(indentation + "/// </summary>");
        }

        private static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || ReservedKeywords.Contains(value))
            {
                return false;
            }

            if (!(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            return value.Skip(1).All(character =>
                char.IsLetterOrDigit(character) || character == '_');
        }

        private static bool IsValidNamespace(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Split('.').All(IsValidIdentifier);
        }

        private static HashSet<string> CollectExistingTypeNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (!string.IsNullOrEmpty(type.FullName))
                        {
                            result.Add(type.FullName);
                        }
                    }
                }
                catch (ReflectionTypeLoadException exception)
                {
                    foreach (Type type in exception.Types)
                    {
                        if (type != null && !string.IsNullOrEmpty(type.FullName))
                        {
                            result.Add(type.FullName);
                        }
                    }
                }
            }

            return result;
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            string[] segments = assetFolder.Split('/');
            string current = segments[0];

            for (int index = 1; index < segments.Length; index++)
            {
                string next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[index]);
                }

                current = next;
            }
        }

        private static string ToFullProjectPath(string assetPath)
        {
            return Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                assetPath);
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            Repaint();
        }
    }
}

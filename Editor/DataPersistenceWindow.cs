using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Phoretell.Editor
{
    internal sealed class DataPersistenceWindow : EditorWindow
    {
        private const string SettingsAssetPath =
            "Assets/Resources/PhoretellDataPersistenceSettings.asset";

        private readonly List<SaveDataTypeInfo> dataTypes =
            new List<SaveDataTypeInfo>();
        private Vector2 dataScrollPosition;
        private Vector2 windowScrollPosition;
        private string selectedTypeName;
        private DataPersistenceSettings settings;
        private SerializedObject serializedSettings;
        private string statusMessage;
        private MessageType statusType;

        private GUIStyle sectionHeaderStyle;
        private GUIStyle sectionSubtitleStyle;

        [MenuItem("Tools/Phoretell/Data Persistence")]
        public static void OpenWindow()
        {
            DataPersistenceWindow window = GetWindow<DataPersistenceWindow>();
            window.titleContent = new GUIContent("Phoretell Data Persistence");
            window.minSize = new Vector2(650f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Phoretell Data Persistence");
            RefreshDataTypes();
            FindSettingsAsset();
            BuildStyles();
        }
        private void OnGUI()
        {
            DrawToolbar();
            windowScrollPosition = EditorGUILayout.BeginScrollView(windowScrollPosition);
            DrawStatus();
            DrawDataClassesSection();
            EditorGUILayout.Space(12f);
            DrawSettingsSection();
            EditorGUILayout.EndScrollView();
        }

        private void BuildStyles()
        {
            sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            sectionSubtitleStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                richText = true
            };
        }
        private Color GetDataClassesTint()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.24f, 0.30f, 0.40f, 1f)
                : new Color(0.82f, 0.89f, 0.98f, 1f);
        }
        private Color GetSelectedClassTint()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.38f, 0.31f, 0.20f, 1f)
                : new Color(0.98f, 0.90f, 0.78f, 1f);
        }
        private Color GetSettingsTint()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.22f, 0.33f, 0.25f, 1f)
                : new Color(0.84f, 0.94f, 0.86f, 1f);
        }
        private void BeginTintedSection(Color tint)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            EditorGUILayout.BeginVertical(GUI.skin.box);
            GUI.backgroundColor = previous;
        }
        private void EndTintedSection()
        {
            EditorGUILayout.EndVertical();
        }
        private void DrawSectionHeader(string title, string subtitle)
        {
            EditorGUILayout.LabelField(title, sectionHeaderStyle);
            if (!string.IsNullOrEmpty(subtitle))
            {
                EditorGUILayout.LabelField(subtitle, sectionSubtitleStyle);
            }

            EditorGUILayout.Space(4f);
        }

        internal void NotifyScriptsCreated(string typeName, string assetPath)
        {
            SetStatus(
                $"Created '{typeName}' and its provider at '{assetPath}'. " +
                "They will appear after Unity finishes compiling.",
                MessageType.Info);
            ShowNotification(new GUIContent("Save-data scripts created"));
            Repaint();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Data Persistence", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            GUI.enabled = !EditorApplication.isCompiling;
            if (GUILayout.Button("Add Data Class", EditorStyles.toolbarButton))
            {
                DataClassCreationWindow.ShowWindow(this);
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
            {
                RefreshDataTypes();
                FindSettingsAsset();
                SetStatus("Data classes and settings refreshed.", MessageType.Info);
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.HelpBox(statusMessage, statusType);
            }

            if (EditorApplication.isCompiling)
            {
                EditorGUILayout.HelpBox(
                    "Unity is compiling scripts. The list refreshes when the window reloads.",
                    MessageType.Info);
            }
        }

        private void DrawDataClassesSection()
        {
            BeginTintedSection(GetDataClassesTint());
            DrawSectionHeader(
                "Data Classes",
                "Serializable payloads currently used by ISaveLoad<TData> provider components.");

            if (dataTypes.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No valid save-data classes were found. Create one here or implement " +
                    "ISaveLoad<TData> on a MonoBehaviour for an existing [Serializable] class.",
                    MessageType.Info);

                EndTintedSection();
                return;
            }

            float listHeight = Mathf.Clamp(dataTypes.Count * 82f, 120f, 330f);
            dataScrollPosition = EditorGUILayout.BeginScrollView(
                dataScrollPosition,
                GUI.skin.box,
                GUILayout.Height(listHeight));

            foreach (SaveDataTypeInfo info in dataTypes)
            {
                DrawDataTypeRow(info);
            }

            EditorGUILayout.EndScrollView();
            EndTintedSection();

            EditorGUILayout.Space(8f);
            DrawSelectedDataTypeActions();
        }

        private void DrawDataTypeRow(SaveDataTypeInfo info)
        {
            bool selected = selectedTypeName == info.DataType.AssemblyQualifiedName;
            GUIStyle style = selected
                ? new GUIStyle("SelectionRect")
                : new GUIStyle(EditorStyles.helpBox);
            Rect rowRect = EditorGUILayout.BeginVertical(style, GUILayout.MinHeight(76f));
            EditorGUILayout.LabelField(info.ClassName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Namespace", info.Namespace);
            EditorGUILayout.LabelField(
                "Script",
                string.IsNullOrEmpty(info.AssetPath)
                    ? "Not identified"
                    : info.AssetPath + (info.HasExactDataScript ? string.Empty : " (provider script)"));
            EditorGUILayout.LabelField("Save Contract", info.ContractName);
            EditorGUILayout.EndVertical();

            Event current = Event.current;
            if (current.type == EventType.MouseDown && rowRect.Contains(current.mousePosition))
            {
                selectedTypeName = info.DataType.AssemblyQualifiedName;
                if (current.clickCount == 2 && info.Script != null)
                {
                    AssetDatabase.OpenAsset(info.Script);
                }

                current.Use();
                Repaint();
            }
        }

        private void DrawSelectedDataTypeActions()
        {
            BeginTintedSection(GetSelectedClassTint());
            DrawSectionHeader(
                "Selected Class",
                "Details and actions for the currently highlighted save-data class.");

            SaveDataTypeInfo selected = GetSelectedType();
            if (selected == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a data class to inspect or open its script.",
                    MessageType.None);
                EndTintedSection();
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Class", selected.DataType.FullName);
            EditorGUILayout.LabelField("Base Class", selected.BaseTypeName);
            EditorGUILayout.LabelField(
                "Provider Components",
                selected.GetProviderSummary(),
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(
                "Script Location",
                string.IsNullOrEmpty(selected.AssetPath)
                    ? "Could not safely identify a script asset."
                    : selected.AssetPath,
                EditorStyles.wordWrappedLabel);

            if (!selected.HasExactDataScript && selected.Script != null)
            {
                EditorGUILayout.HelpBox(
                    "The data class does not have its own identifiable MonoScript. The listed " +
                    "asset is a provider script and cannot be removed from this window.",
                    MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = selected.Script != null;
            if (GUILayout.Button("Open Script"))
            {
                AssetDatabase.OpenAsset(selected.Script);
            }

            if (GUILayout.Button("Ping Script"))
            {
                EditorGUIUtility.PingObject(selected.Script);
            }

            if (GUILayout.Button("Reveal in Project"))
            {
                Selection.activeObject = selected.Script;
                EditorGUIUtility.PingObject(selected.Script);
            }
            GUI.enabled = true;

            GUI.enabled = selected.CanDelete;
            if (GUILayout.Button("Remove Data Class"))
            {
                RemoveSelectedDataClass(selected);
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (!selected.CanDelete)
            {
                EditorGUILayout.LabelField(
                    "Removal requires exact data-class and provider scripts under Assets. " +
                    "Framework/package scripts, shared scripts, and unidentified scripts are protected.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
            EndTintedSection();
        }
        private void DrawSettingsSection()
        {
            BeginTintedSection(GetSettingsTint());
            DrawSectionHeader(
                "Settings",
                "Project-wide file naming, storage, and save behavior.");

            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No project settings asset exists. Runtime uses backward-compatible defaults: " +
                    "pretty JSON, {dataKey}.4TELL files, and the Saves folder beneath " +
                    "Application.persistentDataPath.",
                    MessageType.Info);

                if (GUILayout.Button("Create Project Settings Asset"))
                {
                    CreateSettingsAsset();
                }

                EndTintedSection();
                return;
            }

            if (serializedSettings == null ||
                serializedSettings.targetObject != settings)
            {
                serializedSettings = new SerializedObject(settings);
            }

            serializedSettings.Update();
            EditorGUI.BeginChangeCheck();

            SerializedProperty encryption = serializedSettings.FindProperty("encryptSaveFiles");
            SerializedProperty prettyPrint = serializedSettings.FindProperty("prettyPrintJson");
            SerializedProperty fileName = serializedSettings.FindProperty("saveFileName");
            SerializedProperty extension = serializedSettings.FindProperty("saveFileExtension");
            SerializedProperty folder = serializedSettings.FindProperty("saveFolder");
            SerializedProperty saveOnQuit = serializedSettings.FindProperty("saveOnApplicationQuit");

            EditorGUILayout.PropertyField(encryption, new GUIContent("Encrypt Save Files"));
            if (encryption.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Encryption is not implemented. Runtime save and load operations are blocked " +
                    "while this is enabled, preventing accidental plaintext output. The integration " +
                    "point is FileDataHandler.CanAccessSaveFiles().",
                    MessageType.Warning);
            }

            EditorGUILayout.PropertyField(prettyPrint, new GUIContent("Pretty-print JSON"));
            EditorGUILayout.PropertyField(fileName, new GUIContent("Save File Name"));
            if (fileName.stringValue.IndexOf(
                    DataPersistenceSettings.DataKeyToken,
                    StringComparison.Ordinal) < 0)
            {
                EditorGUILayout.HelpBox(
                    $"Save File Name must contain {DataPersistenceSettings.DataKeyToken}. " +
                    "Each data class is stored independently.",
                    MessageType.Error);
            }

            EditorGUILayout.PropertyField(extension, new GUIContent("Save File Extension"));
            EditorGUILayout.PropertyField(folder, new GUIContent("Save Folder"));
            EditorGUILayout.PropertyField(saveOnQuit, new GUIContent("Save on Application Quit"));

            string previewFolder = string.IsNullOrWhiteSpace(folder.stringValue)
                ? "Saves"
                : folder.stringValue.Trim();

            EditorGUILayout.LabelField(
                "Runtime Storage",
                Path.Combine(Application.persistentDataPath, previewFolder),
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.HelpBox(
                "The save system writes one JSON file per data class and one profile metadata " +
                "file. Save File Name is therefore a pattern rather than a single shared file.",
                MessageType.Info);

            if (EditorGUI.EndChangeCheck())
            {
                serializedSettings.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Settings Asset"))
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }

            if (GUILayout.Button("Use Runtime Defaults"))
            {
                ResetSettingsToDefaults();
            }
            EditorGUILayout.EndHorizontal();

            EndTintedSection();
        }
        private void RefreshDataTypes()
        {
            dataTypes.Clear();
            dataTypes.AddRange(SaveDataTypeDiscovery.Discover());

            if (GetSelectedType() == null)
            {
                selectedTypeName = dataTypes.Count == 0
                    ? null
                    : dataTypes[0].DataType.AssemblyQualifiedName;
            }
        }

        private SaveDataTypeInfo GetSelectedType()
        {
            return dataTypes.Find(info =>
                info.DataType.AssemblyQualifiedName == selectedTypeName);
        }

        private void RemoveSelectedDataClass(SaveDataTypeInfo info)
        {
            if (!info.CanDelete)
            {
                SetStatus(
                    "This script cannot be identified safely or is outside the mutable Assets folder.",
                    MessageType.Error);
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Remove Save Data Class",
                $"Delete '{info.DataType.FullName}' and all of its provider scripts?\n\n" +
                string.Join("\n", info.GetDeletionAssetPaths()) + "\n\n" +
                "Provider scripts may contain user-written capture and restore code. " +
                "This action cannot be undone by this window.",
                "Delete All Listed Scripts",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            IReadOnlyList<string> deletionPaths = info.GetDeletionAssetPaths();
            var failedPaths = new List<string>();
            AssetDatabase.DeleteAssets(
                new List<string>(deletionPaths).ToArray(),
                failedPaths);

            selectedTypeName = null;
            AssetDatabase.Refresh();
            RefreshDataTypes();

            if (failedPaths.Count > 0)
            {
                SetStatus(
                    "Unity could not delete: " + string.Join(", ", failedPaths) +
                    ". Review the remaining scripts before continuing.",
                    MessageType.Error);
            }
            else
            {
                SetStatus(
                    "Deleted: " + string.Join(", ", deletionPaths),
                    MessageType.Info);
            }
        }

        private void FindSettingsAsset()
        {
            settings = null;
            serializedSettings = null;
            string[] guids = AssetDatabase.FindAssets(
                $"t:{nameof(DataPersistenceSettings)}");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsRuntimeSettingsPath(path))
                {
                    continue;
                }

                DataPersistenceSettings candidate =
                    AssetDatabase.LoadAssetAtPath<DataPersistenceSettings>(path);
                if (candidate == null)
                {
                    continue;
                }

                if (settings == null || path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    settings = candidate;
                }

                if (path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (settings != null)
            {
                serializedSettings = new SerializedObject(settings);
            }
        }

        private static bool IsRuntimeSettingsPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.EndsWith(
                       "/Resources/" +
                       DataPersistenceSettings.ResourceName +
                       ".asset",
                       StringComparison.OrdinalIgnoreCase);
        }

        private void CreateSettingsAsset()
        {
            EnsureAssetFolder("Assets/Resources");
            DataPersistenceSettings existing =
                AssetDatabase.LoadAssetAtPath<DataPersistenceSettings>(SettingsAssetPath);
            if (existing != null)
            {
                settings = existing;
            }
            else
            {
                settings = CreateInstance<DataPersistenceSettings>();
                AssetDatabase.CreateAsset(settings, SettingsAssetPath);
                AssetDatabase.SaveAssets();
            }

            serializedSettings = new SerializedObject(settings);
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
            SetStatus(
                $"Created runtime settings at '{SettingsAssetPath}'.",
                MessageType.Info);
        }

        private void ResetSettingsToDefaults()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Reset Data Persistence Settings",
                "Reset all data-persistence settings to their runtime defaults?",
                "Reset",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            Undo.RecordObject(settings, "Reset Data Persistence Settings");
            serializedSettings.FindProperty("encryptSaveFiles").boolValue = false;
            serializedSettings.FindProperty("prettyPrintJson").boolValue = true;
            serializedSettings.FindProperty("saveFileName").stringValue =
                DataPersistenceSettings.DataKeyToken;
            serializedSettings.FindProperty("saveFileExtension").stringValue = ".4TELL";
            serializedSettings.FindProperty("saveFolder").stringValue = "Saves";
            serializedSettings.FindProperty("saveOnApplicationQuit").boolValue = true;
            serializedSettings.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            SetStatus("Runtime settings restored to defaults.", MessageType.Info);
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

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            Repaint();
        }
    }
}

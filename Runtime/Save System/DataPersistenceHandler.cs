using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Phoretell
{
    /// <summary>
    /// Package-owned information that a generic save-slot UI can display.
    /// Game-specific progress belongs in a project-owned ISaveLoad data class.
    /// </summary>
    [Serializable]
    public sealed class SaveProfileInfo
    {
        public string profileId;
        public string displayName;
        public string savedAtUtc;

        public SaveProfileInfo()
        {
        }

        public SaveProfileInfo(string profileId, string displayName, string savedAtUtc)
        {
            this.profileId = profileId;
            this.displayName = displayName;
            this.savedAtUtc = savedAtUtc;
        }
    }

    /// <summary>
    /// Project-independent save coordinator.
    ///
    /// A game defines its own serializable data classes and implements
    /// ISaveLoad&lt;TData&gt; on scene components. This class discovers and groups those
    /// components without taking a compile-time dependency on any project data type.
    /// </summary>
    public sealed class DataPersistenceHandler : MonoBehaviour
    {
        private const string ProfileInfoKey = "_profile";

        private static DataPersistenceHandler instance;

        [SerializeField] private string selectedProfileId = "";
        [SerializeField] private string selectedProfileDisplayName = "";
        [SerializeField] private bool saveOnApplicationQuit = true;

        private FileDataHandler fileDataHandler;

        public static DataPersistenceHandler Instance
        {
            get
            {
                if (instance == null)
                {
                    Debug.LogError(
                        $"{nameof(DataPersistenceHandler)} instance was not found in the scene.");
                }

                return instance;
            }
        }

        public string SelectedProfileId => selectedProfileId;
        public string GameSavesPath { get; private set; }

        [Obsolete("Use GameSavesPath instead.")]
        public string gameSavesPath => GameSavesPath;

        public event Action<string> ProfileLoaded;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            GameSavesPath = System.IO.Path.Combine(
                Application.persistentDataPath,
                "Saves");
            fileDataHandler = new FileDataHandler(GameSavesPath);
        }

        public bool ChangeSelectedProfileId(string newProfileId)
        {
            if (!FileDataHandler.IsValidPathSegment(newProfileId))
            {
                Debug.LogError($"'{newProfileId}' is not a valid save profile id.");
                return false;
            }

            selectedProfileId = newProfileId;
            selectedProfileDisplayName = newProfileId;
            return true;
        }

        public void SetSelectedProfileDisplayName(string displayName)
        {
            selectedProfileDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? selectedProfileId
                : displayName.Trim();
        }

        /// <summary>
        /// Applies a fresh instance of every discovered project data type.
        /// </summary>
        public void NewGame()
        {
            foreach (SaveDataSection section in FindSaveDataSections())
            {
                object data = section.CreateDefaultData();
                if (data != null)
                {
                    section.RestoreData(data);
                }
            }
        }

        public bool SaveGame()
        {
            if (!EnsureProfileSelected())
            {
                return false;
            }

            bool savedEverything = true;

            foreach (SaveDataSection section in FindSaveDataSections())
            {
                object data = section.CreateDefaultData();
                if (data == null)
                {
                    savedEverything = false;
                    continue;
                }

                try
                {
                    section.CaptureData(data);
                    savedEverything &= fileDataHandler.Save(
                        selectedProfileId,
                        section.SaveKey,
                        data);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"Save provider '{section.SaveKey}' failed while capturing data.\n{exception}");
                    savedEverything = false;
                }
            }

            var profileInfo = new SaveProfileInfo(
                selectedProfileId,
                string.IsNullOrWhiteSpace(selectedProfileDisplayName)
                    ? selectedProfileId
                    : selectedProfileDisplayName,
                DateTime.UtcNow.ToString("O"));

            savedEverything &= fileDataHandler.Save(
                selectedProfileId,
                ProfileInfoKey,
                profileInfo);

            return savedEverything;
        }

        public bool LoadGame()
        {
            if (!EnsureProfileSelected())
            {
                return false;
            }

            bool profileExists = fileDataHandler.ProfileExists(selectedProfileId);

            foreach (SaveDataSection section in FindSaveDataSections())
            {
                object data;
                if (!fileDataHandler.TryLoad(
                        selectedProfileId,
                        section.SaveKey,
                        section.DataType,
                        out data))
                {
                    data = section.CreateDefaultData();
                }

                if (data == null)
                {
                    continue;
                }

                try
                {
                    section.RestoreData(data);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"Save provider '{section.SaveKey}' failed while restoring data.\n{exception}");
                }
            }

            SaveProfileInfo profileInfo;
            if (TryGetProfileInfo(selectedProfileId, out profileInfo))
            {
                selectedProfileDisplayName = profileInfo.displayName;
            }

            ProfileLoaded?.Invoke(selectedProfileId);
            return profileExists;
        }

        public IReadOnlyList<SaveProfileInfo> GetAllProfiles()
        {
            var profiles = new List<SaveProfileInfo>();

            foreach (string profileId in fileDataHandler.GetProfileIds())
            {
                SaveProfileInfo profileInfo;
                if (!TryGetProfileInfo(profileId, out profileInfo))
                {
                    profileInfo = new SaveProfileInfo(profileId, profileId, "");
                }

                profiles.Add(profileInfo);
            }

            return profiles;
        }

        private bool TryGetProfileInfo(string profileId, out SaveProfileInfo profileInfo)
        {
            object loadedData;
            bool loaded = fileDataHandler.TryLoad(
                profileId,
                ProfileInfoKey,
                typeof(SaveProfileInfo),
                out loadedData);

            profileInfo = loadedData as SaveProfileInfo;
            return loaded && profileInfo != null;
        }

        private bool EnsureProfileSelected()
        {
            if (FileDataHandler.IsValidPathSegment(selectedProfileId))
            {
                return true;
            }

            Debug.LogError(
                "Select a valid save profile before calling SaveGame or LoadGame.");
            return false;
        }

        private static List<SaveDataSection> FindSaveDataSections()
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            var sectionsByKey = new Dictionary<string, SaveDataSection>(
                StringComparer.Ordinal);

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null)
                {
                    continue;
                }

                foreach (Type interfaceType in behaviour.GetType().GetInterfaces())
                {
                    if (!interfaceType.IsGenericType ||
                        interfaceType.GetGenericTypeDefinition() != typeof(ISaveLoad<>))
                    {
                        continue;
                    }

                    Type dataType = interfaceType.GetGenericArguments()[0];
                    string saveKey = GetSaveKey(behaviour, dataType);

                    SaveDataSection section;
                    if (!sectionsByKey.TryGetValue(saveKey, out section))
                    {
                        section = new SaveDataSection(saveKey, dataType);
                        sectionsByKey.Add(saveKey, section);
                    }
                    else if (section.DataType != dataType)
                    {
                        Debug.LogError(
                            $"Save key '{saveKey}' is used for both " +
                            $"'{section.DataType.FullName}' and '{dataType.FullName}'. " +
                            $"Give one provider a different {nameof(ISaveKeyProvider.SaveKey)}.");
                        continue;
                    }

                    section.AddTarget(behaviour, interfaceType);
                }
            }

            var sections = new List<SaveDataSection>(sectionsByKey.Values);
            sections.Sort((left, right) =>
                string.CompareOrdinal(left.SaveKey, right.SaveKey));
            return sections;
        }

        private static string GetSaveKey(MonoBehaviour behaviour, Type dataType)
        {
            var keyProvider = behaviour as ISaveKeyProvider;
            if (keyProvider != null && !string.IsNullOrWhiteSpace(keyProvider.SaveKey))
            {
                return keyProvider.SaveKey.Trim();
            }

            return dataType.FullName ?? dataType.Name;
        }

        private void OnApplicationQuit()
        {
            if (saveOnApplicationQuit &&
                FileDataHandler.IsValidPathSegment(selectedProfileId))
            {
                SaveGame();
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        private sealed class SaveDataSection
        {
            private readonly List<SaveDataTarget> targets = new List<SaveDataTarget>();

            public string SaveKey { get; }
            public Type DataType { get; }

            public SaveDataSection(string saveKey, Type dataType)
            {
                SaveKey = saveKey;
                DataType = dataType;
            }

            public void AddTarget(object target, Type interfaceType)
            {
                MethodInfo saveMethod = interfaceType.GetMethod(nameof(ISaveLoad<object>.SaveData));
                MethodInfo loadMethod = interfaceType.GetMethod(nameof(ISaveLoad<object>.LoadData));

                if (saveMethod == null || loadMethod == null)
                {
                    Debug.LogError(
                        $"'{target.GetType().FullName}' has an invalid ISaveLoad implementation.");
                    return;
                }

                targets.Add(new SaveDataTarget(target, saveMethod, loadMethod));
            }

            public object CreateDefaultData()
            {
                try
                {
                    return Activator.CreateInstance(DataType);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"Save data type '{DataType.FullName}' must be a non-abstract class " +
                        $"with a parameterless constructor.\n{exception}");
                    return null;
                }
            }

            public void CaptureData(object data)
            {
                foreach (SaveDataTarget target in targets)
                {
                    target.SaveMethod.Invoke(target.Instance, new[] { data });
                }
            }

            public void RestoreData(object data)
            {
                foreach (SaveDataTarget target in targets)
                {
                    target.LoadMethod.Invoke(target.Instance, new[] { data });
                }
            }
        }

        private sealed class SaveDataTarget
        {
            public object Instance { get; }
            public MethodInfo SaveMethod { get; }
            public MethodInfo LoadMethod { get; }

            public SaveDataTarget(
                object instance,
                MethodInfo saveMethod,
                MethodInfo loadMethod)
            {
                Instance = instance;
                SaveMethod = saveMethod;
                LoadMethod = loadMethod;
            }
        }
    }
}

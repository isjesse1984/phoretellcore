using System;
using System.IO;
using UnityEngine;

namespace Phoretell
{
    /// <summary>
    /// Project-wide runtime settings for the data-persistence system.
    /// Create the project override from Tools > Phoretell > Data Persistence.
    /// </summary>
    [CreateAssetMenu(
        fileName = ResourceName,
        menuName = "Phoretell/Data Persistence Settings")]
    public sealed class DataPersistenceSettings : ScriptableObject
    {
        public const string ResourceName = "PhoretellDataPersistenceSettings";
        public const string DataKeyToken = "{dataKey}";

        internal const string DefaultSaveFolder = "Saves";
        internal const string DefaultFileNamePattern = DataKeyToken;
        internal const string DefaultFileExtension = ".4TELL";

        [Tooltip(
            "Encryption is reserved for a future authenticated-encryption provider. " +
            "Saves are blocked while this is enabled so data is never silently written as plaintext.")]
        [SerializeField] private bool encryptSaveFiles;

        [Tooltip("Write indented JSON. Disable this for smaller save files.")]
        [SerializeField] private bool prettyPrintJson = true;

        [Tooltip(
            "File-name pattern used for each save-data section. Keep {dataKey} in the pattern " +
            "so independently saved data classes cannot overwrite one another.")]
        [SerializeField] private string saveFileName = DefaultFileNamePattern;

        [Tooltip("Extension appended to each save file, including the leading period.")]
        [SerializeField] private string saveFileExtension = DefaultFileExtension;

        [Tooltip(
            "Folder beneath Application.persistentDataPath. A relative path such as " +
            "Saves or MyGame/Saves is supported.")]
        [SerializeField] private string saveFolder = DefaultSaveFolder;

        [Tooltip(
            "Global switch for saving on application quit. The DataPersistenceHandler's " +
            "per-instance option must also be enabled.")]
        [SerializeField] private bool saveOnApplicationQuit = true;

        public bool EncryptSaveFiles => encryptSaveFiles;
        public bool PrettyPrintJson => prettyPrintJson;
        public string SaveFileName => string.IsNullOrWhiteSpace(saveFileName)
            ? DefaultFileNamePattern
            : saveFileName.Trim();
        public string SaveFileExtension => NormalizeFileExtension(saveFileExtension);
        public string SaveFolder => string.IsNullOrWhiteSpace(saveFolder)
            ? DefaultSaveFolder
            : saveFolder.Trim();
        public bool SaveOnApplicationQuit => saveOnApplicationQuit;

        /// <summary>
        /// Loads the project override from a Resources folder. When no override
        /// exists, an in-memory instance supplies backward-compatible defaults.
        /// </summary>
        public static DataPersistenceSettings Load()
        {
            DataPersistenceSettings settings =
                Resources.Load<DataPersistenceSettings>(ResourceName);

            if (settings != null)
            {
                return settings;
            }

            settings = CreateInstance<DataPersistenceSettings>();
            settings.hideFlags = HideFlags.HideAndDontSave;
            return settings;
        }

        public string GetSaveRootPath()
        {
            string persistentRoot = Path.GetFullPath(Application.persistentDataPath);

            try
            {
                string candidate = Path.GetFullPath(Path.Combine(persistentRoot, SaveFolder));
                string rootWithSeparator = persistentRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                Debug.LogError(
                    $"Save folder '{SaveFolder}' is invalid. {exception.Message}");
            }

            Debug.LogError(
                $"Save folder '{SaveFolder}' must resolve beneath " +
                "Application.persistentDataPath. " +
                $"Falling back to '{DefaultSaveFolder}'.");

            return Path.Combine(persistentRoot, DefaultSaveFolder);
        }

        private static string NormalizeFileExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return DefaultFileExtension;
            }

            string trimmed = extension.Trim();
            return trimmed[0] == '.' ? trimmed : "." + trimmed;
        }
    }
}

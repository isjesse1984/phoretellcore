using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Phoretell
{
    /// <summary>
    /// Handles JSON files on disk. It deliberately knows nothing about a game's
    /// concrete save-data classes.
    /// </summary>
    public sealed class FileDataHandler
    {
        private const string FileExtension = ".4TELL";
        private readonly string rootDirectory;
        private readonly bool encryptSaveFiles;
        private readonly bool prettyPrintJson;
        private readonly string fileNamePattern;
        private readonly string fileExtension;
        private bool encryptionErrorReported;

        public bool IsOperational => CanAccessSaveFiles();

        public FileDataHandler(string rootDirectory)
            : this(rootDirectory, null)
        {
        }

        public FileDataHandler(
            string rootDirectory,
            DataPersistenceSettings settings)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("A save root directory is required.", nameof(rootDirectory));
            }

            this.rootDirectory = Path.GetFullPath(rootDirectory);
            encryptSaveFiles = settings != null && settings.EncryptSaveFiles;
            prettyPrintJson = settings == null || settings.PrettyPrintJson;
            fileNamePattern = settings == null
                ? DataPersistenceSettings.DefaultFileNamePattern
                : settings.SaveFileName;
            fileExtension = settings == null
                ? FileExtension
                : settings.SaveFileExtension;
        }

        public bool Save(string profileId, string dataKey, object data)
        {
            if (!CanAccessSaveFiles())
            {
                return false;
            }

            if (data == null)
            {
                Debug.LogError($"Cannot save null data for key '{dataKey}'.");
                return false;
            }

            string fullPath;
            try
            {
                fullPath = GetDataPath(profileId, dataKey);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message);
                return false;
            }

            string temporaryPath = fullPath + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                string json = JsonUtility.ToJson(data, prettyPrintJson);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                File.Move(temporaryPath, fullPath);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"An error occurred while saving '{dataKey}' to '{fullPath}'.\n{exception}");
                TryDeleteTemporaryFile(temporaryPath);
                return false;
            }
        }

        public bool TryLoad(string profileId, string dataKey, Type dataType, out object data)
        {
            data = null;

            if (!CanAccessSaveFiles())
            {
                return false;
            }

            if (dataType == null)
            {
                Debug.LogError("A data type is required when loading save data.");
                return false;
            }

            string fullPath;
            try
            {
                fullPath = GetDataPath(profileId, dataKey);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(fullPath, Encoding.UTF8);
                data = JsonUtility.FromJson(json, dataType);
                return data != null;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"An error occurred while loading '{dataKey}' from '{fullPath}'.\n{exception}");
                return false;
            }
        }

        public IReadOnlyList<string> GetProfileIds()
        {
            if (!Directory.Exists(rootDirectory))
            {
                return Array.Empty<string>();
            }

            var profileIds = new List<string>();

            try
            {
                foreach (string directory in Directory.GetDirectories(rootDirectory))
                {
                    profileIds.Add(Path.GetFileName(directory));
                }
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"An error occurred while finding save profiles in '{rootDirectory}'.\n{exception}");
            }

            profileIds.Sort(StringComparer.OrdinalIgnoreCase);
            return profileIds;
        }

        public bool ProfileExists(string profileId)
        {
            try
            {
                return Directory.Exists(GetProfilePath(profileId));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static bool IsValidPathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == "..")
            {
                return false;
            }

            return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                   value.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private string GetDataPath(string profileId, string dataKey)
        {
            if (!IsValidPathSegment(dataKey))
            {
                throw new ArgumentException(
                    $"Save key '{dataKey}' is not a valid file name.", nameof(dataKey));
            }

            if (string.IsNullOrWhiteSpace(fileNamePattern) ||
                fileNamePattern.IndexOf(
                    DataPersistenceSettings.DataKeyToken,
                    StringComparison.Ordinal) < 0)
            {
                throw new ArgumentException(
                    $"Save file name pattern must contain " +
                    $"'{DataPersistenceSettings.DataKeyToken}'.");
            }

            string fileName = fileNamePattern.Replace(
                DataPersistenceSettings.DataKeyToken,
                dataKey) + fileExtension;

            if (!IsValidPathSegment(fileName))
            {
                throw new ArgumentException(
                    $"Save file name '{fileName}' is not valid. Check the data-persistence settings.");
            }

            return Path.Combine(GetProfilePath(profileId), fileName);
        }

        private string GetProfilePath(string profileId)
        {
            if (!IsValidPathSegment(profileId))
            {
                throw new ArgumentException(
                    $"Profile id '{profileId}' is not a valid directory name.", nameof(profileId));
            }

            return Path.Combine(rootDirectory, profileId);
        }

        private static void TryDeleteTemporaryFile(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // The original save error is the useful one. Do not hide it with
                // a secondary cleanup exception.
            }
        }

        private bool CanAccessSaveFiles()
        {
            if (!encryptSaveFiles)
            {
                return true;
            }

            if (!encryptionErrorReported)
            {
                Debug.LogError(
                    "Save-file encryption is enabled, but no authenticated-encryption " +
                    "provider is implemented. Save and load operations are blocked to " +
                    "avoid writing data as unprotected plaintext.");
                encryptionErrorReported = true;
            }

            return false;
        }
    }
}

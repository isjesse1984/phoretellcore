using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

namespace Phoretell
{
    /// <summary>
    /// Generic profile picker. Scene changes and other game flow are deliberately
    /// delegated to the onProfileLoaded UnityEvent.
    /// </summary>
    public sealed class SaveSlotMenu : MonoBehaviour
    {
        [FormerlySerializedAs("saveSlot")]
        [SerializeField] private GameObject saveSlotPrefab;
        [SerializeField] private UnityEvent<string> onProfileLoaded =
            new UnityEvent<string>();

        private readonly List<SaveSlot> spawnedSaveSlots = new List<SaveSlot>();

        public UnityEvent<string> OnProfileLoaded => onProfileLoaded;

        private void Start()
        {
            ActivateMenu();
        }

        public void ActivateMenu()
        {
            ClearSpawnedSlots();

            DataPersistenceHandler persistence = DataPersistenceHandler.Instance;
            if (persistence == null || saveSlotPrefab == null)
            {
                return;
            }

            foreach (SaveProfileInfo profile in persistence.GetAllProfiles())
            {
                GameObject newSaveSlot = Instantiate(saveSlotPrefab, transform);
                newSaveSlot.name = profile.profileId;

                SaveSlot slot = newSaveSlot.GetComponent<SaveSlot>();
                if (slot == null)
                {
                    Debug.LogError(
                        $"Save slot prefab '{saveSlotPrefab.name}' needs a {nameof(SaveSlot)} component.");
                    Destroy(newSaveSlot);
                    continue;
                }

                slot.SetProfileId(profile.profileId);
                slot.SetData(profile);
                spawnedSaveSlots.Add(slot);
            }
        }

        public void OnSaveSlotClicked(SaveSlot saveSlot)
        {
            if (saveSlot == null)
            {
                return;
            }

            DataPersistenceHandler persistence = DataPersistenceHandler.Instance;
            if (persistence == null ||
                !persistence.ChangeSelectedProfileId(saveSlot.ProfileId))
            {
                return;
            }

            persistence.LoadGame();
            onProfileLoaded.Invoke(saveSlot.ProfileId);
        }

        private void ClearSpawnedSlots()
        {
            foreach (SaveSlot slot in spawnedSaveSlots)
            {
                if (slot != null)
                {
                    Destroy(slot.gameObject);
                }
            }

            spawnedSaveSlots.Clear();
        }
    }
}

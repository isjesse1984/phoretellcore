using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Phoretell
{
    public sealed class SaveSlot : MonoBehaviour
    {
        [SerializeField] private string profileId = "";
        [SerializeField] private GameObject noDataContent;
        [SerializeField] private GameObject hasDataContent;
        [SerializeField] private TextMeshProUGUI playerName;
        [SerializeField] private TextMeshProUGUI levelName;
        [SerializeField] private TextMeshProUGUI lastSaved;

        public string ProfileId => profileId;

        private void Awake()
        {
            Button button = GetComponent<Button>();
            if (button != null)
            {
                button.onClick.AddListener(NotifyMenu);
            }
        }

        public void SetProfileId(string value)
        {
            profileId = value;
        }

        public void SetData(SaveProfileInfo data)
        {
            bool hasData = data != null;

            if (noDataContent != null)
            {
                noDataContent.SetActive(!hasData);
            }

            if (hasDataContent != null)
            {
                hasDataContent.SetActive(hasData);
            }

            if (!hasData)
            {
                return;
            }

            if (playerName != null)
            {
                playerName.text = string.IsNullOrWhiteSpace(data.displayName)
                    ? data.profileId
                    : data.displayName;
            }

            if (levelName != null)
            {
                levelName.text = "";
            }

            if (lastSaved != null)
            {
                DateTime savedAt;
                lastSaved.text = DateTime.TryParse(
                    data.savedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out savedAt)
                    ? $"Last Saved: {savedAt.ToLocalTime():g}"
                    : "Last Saved: Unknown";
            }
        }

        private void NotifyMenu()
        {
            SaveSlotMenu saveSlotMenu = GetComponentInParent<SaveSlotMenu>();
            if (saveSlotMenu != null)
            {
                saveSlotMenu.OnSaveSlotClicked(this);
            }
        }
    }
}

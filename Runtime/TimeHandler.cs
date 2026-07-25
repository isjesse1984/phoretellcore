using System;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace Phoretell
{
    /// <summary>
    /// Advances and displays an accelerated in-game clock.
    /// totalSeconds is the single source of truth; all display values are derived.
    /// </summary>
    public sealed class TimeHandler : Singleton<TimeHandler>, ISaveLoad<TimeData>
    {
        private const double SecondsPerDay = 86_400d;
        private const double SecondsPerHour = 3_600d;
        private const double SecondsPerMinute = 60d;
        private const float MinimumDayLengthMinutes = 0.01f;

        [Header("Clock Settings")]
        [Tooltip("How many real-world minutes a full in-game day takes.")]
        [Min(MinimumDayLengthMinutes)]
        [FormerlySerializedAs("minutesPerIngameDay")]
        [SerializeField] private float minutesPerInGameDay = 20f;

        [Tooltip("Additional clock-speed multiplier. Set to 0 to pause the clock.")]
        [Min(0f)]
        [FormerlySerializedAs("timeMultiplyer")]
        [SerializeField] private float timeMultiplier = 1f;

        [Tooltip("When enabled, the game clock continues while Time.timeScale is 0.")]
        [SerializeField] private bool useUnscaledTime;

        [Header("UI (Optional)")]
        [SerializeField] private TextMeshProUGUI dayText;
        [SerializeField] private TextMeshProUGUI timeText;

        [Header("Current Time")]
        [Min(0f)]
        [SerializeField] private double totalSeconds = 43_200d;
        [SerializeField] private int currentDay = 1;
        [SerializeField] private int hour24;
        [SerializeField] private int hour12 = 12;
        [SerializeField] private int minute;
        [SerializeField] private int second;
        [SerializeField] private string amPm = "PM";
        [SerializeField] private string formattedTime = "Day 1  12:00:00 PM";

        private long lastDisplayedWholeSecond = -1;
        private float previousRunningMultiplier = 1f;

        public double TotalSeconds => totalSeconds;
        public int CurrentDay => currentDay;
        public int Hour24 => hour24;
        public int Hour12 => hour12;
        public int Minute => minute;
        public int Second => second;
        public string AmPm => amPm;
        public string FormattedTime => formattedTime;
        public float TimeMultiplier => timeMultiplier;
        public bool IsPaused => timeMultiplier <= 0f;

        private void OnEnable()
        {
            RefreshClock(true);
        }

        private void Update()
        {
            if (timeMultiplier > 0f)
            {
                float deltaTime = useUnscaledTime
                    ? Time.unscaledDeltaTime
                    : Time.deltaTime;

                double realSecondsPerGameDay =
                    Math.Max(MinimumDayLengthMinutes, minutesPerInGameDay) *
                    SecondsPerMinute;

                totalSeconds +=
                    deltaTime *
                    (SecondsPerDay / realSecondsPerGameDay) *
                    timeMultiplier;
            }

            RefreshClock(false);
        }

        /// <summary>
        /// Immediately refreshes the derived clock values and optional UI.
        /// Kept public for Inspector events and compatibility with existing callers.
        /// </summary>
        public void UpdateUi()
        {
            RefreshClock(true);
        }

        public void SetTimeMultiplier(float value)
        {
            timeMultiplier = Mathf.Max(0f, value);

            if (timeMultiplier > 0f)
            {
                previousRunningMultiplier = timeMultiplier;
            }
        }

        public void Pause()
        {
            if (timeMultiplier > 0f)
            {
                previousRunningMultiplier = timeMultiplier;
            }

            timeMultiplier = 0f;
        }

        public void Resume()
        {
            timeMultiplier = previousRunningMultiplier > 0f
                ? previousRunningMultiplier
                : 1f;
        }

        public void SetTotalSeconds(double value)
        {
            totalSeconds = Math.Max(0d, value);
            RefreshClock(true);
        }

        public void AdvanceTime(double secondsToAdd)
        {
            totalSeconds = Math.Max(0d, totalSeconds + secondsToAdd);
            RefreshClock(true);
        }

        /// <summary>
        /// Compatibility accessor. Prefer TotalSeconds when sub-second precision or
        /// very long play sessions matter.
        /// </summary>
        public int GetTotalSeconds()
        {
            if (totalSeconds >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Floor(totalSeconds);
        }

        public void SaveData(TimeData data)
        {
            if (data == null)
            {
                Debug.LogError("Cannot save time into a null TimeData object.", this);
                return;
            }

            data.totalSeconds = totalSeconds;
            data.timeMultiplyer = timeMultiplier;
        }

        public void LoadData(TimeData data)
        {
            if (data == null)
            {
                Debug.LogWarning("No TimeData was supplied. The current clock was kept.", this);
                return;
            }

            totalSeconds = Math.Max(0d, data.totalSeconds);
            SetTimeMultiplier(data.timeMultiplyer);
            RefreshClock(true);
        }

        private void RefreshClock(bool force)
        {
            long wholeSeconds = ToSafeWholeSeconds(totalSeconds);
            if (!force && wholeSeconds == lastDisplayedWholeSecond)
            {
                return;
            }

            lastDisplayedWholeSecond = wholeSeconds;

            long elapsedDays = wholeSeconds / (long)SecondsPerDay;
            currentDay = elapsedDays >= int.MaxValue
                ? int.MaxValue
                : (int)elapsedDays + 1;

            int secondsToday = (int)(wholeSeconds % (long)SecondsPerDay);
            hour24 = (int)(secondsToday / SecondsPerHour);
            minute = (int)(secondsToday / SecondsPerMinute) % 60;
            second = secondsToday % 60;

            hour12 = hour24 % 12;
            if (hour12 == 0)
            {
                hour12 = 12;
            }

            amPm = hour24 < 12 ? "AM" : "PM";
            formattedTime =
                $"Day {currentDay}  {hour12:D2}:{minute:D2}:{second:D2} {amPm}";

            if (dayText != null)
            {
                dayText.text = currentDay.ToString();
            }

            if (timeText != null)
            {
                timeText.text = $"{hour12:D2}:{minute:D2}:{second:D2} {amPm}";
            }
        }

        private static long ToSafeWholeSeconds(double value)
        {
            if (double.IsNaN(value) || value <= 0d)
            {
                return 0L;
            }

            if (double.IsPositiveInfinity(value) || value >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Floor(value);
        }

        private void OnValidate()
        {
            minutesPerInGameDay = Mathf.Max(
                MinimumDayLengthMinutes,
                minutesPerInGameDay);
            timeMultiplier = Mathf.Max(0f, timeMultiplier);
            totalSeconds = Math.Max(0d, totalSeconds);

            if (!Application.isPlaying)
            {
                RefreshClock(true);
            }
        }
    }
}

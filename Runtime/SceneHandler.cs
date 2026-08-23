using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;
using Unity.VisualScripting;

namespace Phoretell
{
    public class SceneHandler : Singleton<SceneHandler>
    {
    private const float SceneReadyProgress = 0.9f;

    [Header("Loading Screen")]
    [SerializeField] private GameObject loadingScreenObject;
    [SerializeField] private Slider loadingBarSlider;
    [SerializeField, Min(0f)] private float minimumVisibleDuration = 0.35f;
    [SerializeField, Min(0f)] private float completionHoldDuration = 0.12f;

    [Header("Base")]
    [SerializeField] private List<string> maps = new List<string>();

    [Header("DLC")]
    [SerializeField] private string mapDLC_Path = string.Empty;
    [SerializeField] private List<string> mapsDLC = new List<string>();

    private Coroutine transitionRoutine;
    private bool pendingDataLoad;
    private bool dataLoadComplete;

    public string[] MapsCollection { get; private set; } = Array.Empty<string>();
    public bool IsTransitioning => transitionRoutine != null;

    protected override void Awake()
    {
        base.Awake();

        if (!TryGetInstance(out GameMarketSceneHandler activeHandler) ||
            activeHandler != this)
        {
            return;
        }

        maps ??= new List<string>();
        mapsDLC ??= new List<string>();
        RefreshAvailableMaps();
        SetLoadingProgress(0f);
        SetLoadingScreenVisible(false);
    }

    private void OnEnable()
    {
        if (TryGetInstance(out GameMarketSceneHandler activeHandler) &&
            activeHandler == this)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public string GetActiveSceneName()
    {
        return SceneManager.GetActiveScene().name;
    }

    public void ChangeScene(string requestedSceneName)
    {
        if (string.IsNullOrWhiteSpace(requestedSceneName))
        {
            Debug.LogError("A scene name is required before starting a transition.");
            return;
        }

        if (transitionRoutine != null)
        {
            Debug.LogWarning(
                $"A scene transition is already running. Ignoring '{requestedSceneName}'.");
            return;
        }

        transitionRoutine = StartCoroutine(
            LoadSceneRoutine(requestedSceneName.Trim()));
    }

    private IEnumerator LoadSceneRoutine(string requestedSceneName)
    {
        float visibleSince = Time.realtimeSinceStartup;
        SetLoadingProgress(0f);
        SetLoadingScreenVisible(true);
        Canvas.ForceUpdateCanvases();

        // Give Unity a complete render opportunity before any scene or bundle
        // loading can occupy the main thread.
        yield return new WaitForEndOfFrame();

        if (!TryResolveScene(
                requestedSceneName,
                out string sceneToLoad,
                out AssetBundle mapBundle))
        {
            AbortTransition(null);
            yield break;
        }

        if (!TryStartSceneLoad(sceneToLoad, out AsyncOperation operation))
        {
            AbortTransition(mapBundle);
            yield break;
        }

        operation.allowSceneActivation = false;

        while (operation.progress < SceneReadyProgress)
        {
            // Unity reports the load phase from 0.0 to 0.9. Keep that range
            // honest instead of displaying 100% before activation/data load.
            SetLoadingProgress(Mathf.Clamp(operation.progress, 0f, SceneReadyProgress));
            yield return null;
        }

        SetLoadingProgress(SceneReadyProgress);
        Canvas.ForceUpdateCanvases();
        yield return null;

        pendingDataLoad = true;
        dataLoadComplete = false;
        operation.allowSceneActivation = true;

        while (!operation.isDone)
        {
            yield return null;
        }

        // sceneLoaded normally restores data during activation. Retain a safe
        // fallback in case another loading mechanism suppresses that callback.
        if (!dataLoadComplete)
        {
            RestoreSelectedProfile();
        }

        mapBundle?.Unload(false);

        SetLoadingProgress(1f);
        Canvas.ForceUpdateCanvases();

        float remainingMinimum = minimumVisibleDuration -
                                 (Time.realtimeSinceStartup - visibleSince);
        if (remainingMinimum > 0f)
        {
            yield return new WaitForSecondsRealtime(remainingMinimum);
        }

        if (completionHoldDuration > 0f)
        {
            yield return new WaitForSecondsRealtime(completionHoldDuration);
        }

        // Ensure the completed bar is presented at least once before hiding it.
        yield return new WaitForEndOfFrame();

        SetLoadingScreenVisible(false);
        pendingDataLoad = false;
        dataLoadComplete = false;
        transitionRoutine = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!pendingDataLoad || transitionRoutine == null)
        {
            return;
        }

        RestoreSelectedProfile();
    }

    private void RestoreSelectedProfile()
    {
        pendingDataLoad = false;

        DataPersistenceHandler persistence = DataPersistenceHandler.Instance;
        if (persistence == null)
        {
            Debug.LogWarning(
                "The scene loaded, but no DataPersistenceHandler was available.");
            dataLoadComplete = true;
            return;
        }

        if (!persistence.LoadGame())
        {
            Debug.LogWarning(
                "The scene loaded, but the selected save profile could not be restored.");
        }

        dataLoadComplete = true;
    }

    private static bool TryStartSceneLoad(
        string sceneToLoad,
        out AsyncOperation operation)
    {
        try
        {
            operation = SceneManager.LoadSceneAsync(
                sceneToLoad,
                LoadSceneMode.Single);
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Could not begin loading scene '{sceneToLoad}'.\n{exception}");
            operation = null;
            return false;
        }

        if (operation != null)
        {
            return true;
        }

        Debug.LogError($"Unity did not create a load operation for '{sceneToLoad}'.");
        return false;
    }

    private bool TryResolveScene(
        string requestedSceneName,
        out string sceneToLoad,
        out AssetBundle mapBundle)
    {
        sceneToLoad = requestedSceneName;
        mapBundle = null;

        bool isDlcMap = mapsDLC.Any(candidate => string.Equals(
            candidate,
            requestedSceneName,
            StringComparison.OrdinalIgnoreCase));
        if (!isDlcMap)
        {
            return true;
        }

        string bundlePath = Path.Combine(mapDLC_Path, requestedSceneName);
        mapBundle = AssetBundle.LoadFromFile(bundlePath);
        if (mapBundle == null)
        {
            Debug.LogError($"Could not load map bundle '{bundlePath}'.");
            return false;
        }

        sceneToLoad = mapBundle
            .GetAllScenePaths()
            .FirstOrDefault(scenePath => string.Equals(
                Path.GetFileNameWithoutExtension(scenePath),
                requestedSceneName,
                StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(sceneToLoad))
        {
            return true;
        }

        Debug.LogError(
            $"Map bundle '{bundlePath}' does not contain scene '{requestedSceneName}'.");
        mapBundle.Unload(false);
        mapBundle = null;
        return false;
    }

    private void RefreshAvailableMaps()
    {
        mapsDLC.Clear();

        try
        {
            string dlcRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Application.companyName,
                Application.productName,
                "Mods",
                "Maps");
            mapDLC_Path = dlcRoot;
            Directory.CreateDirectory(mapDLC_Path);

            foreach (string mapAssetBundle in Directory.GetFiles(mapDLC_Path))
            {
                mapsDLC.Add(Path.GetFileNameWithoutExtension(mapAssetBundle));
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"DLC maps could not be enumerated. Base scenes remain available.\n" +
                exception.Message);
        }

        MapsCollection = maps
            .Concat(mapsDLC)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AbortTransition(AssetBundle mapBundle)
    {
        mapBundle?.Unload(false);
        pendingDataLoad = false;
        dataLoadComplete = false;
        SetLoadingProgress(0f);
        SetLoadingScreenVisible(false);
        transitionRoutine = null;
    }

    private void SetLoadingScreenVisible(bool visible)
    {
        if (loadingScreenObject != null &&
            loadingScreenObject.activeSelf != visible)
        {
            loadingScreenObject.SetActive(visible);
        }
    }

    private void SetLoadingProgress(float progress)
    {
        if (loadingBarSlider != null)
        {
            loadingBarSlider.SetValueWithoutNotify(Mathf.Clamp01(progress));
        }
    }
}
}

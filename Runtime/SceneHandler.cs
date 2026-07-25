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
        [Header("Loading Screen")]
        [SerializeField] private GameObject loadingScreenObject;
        [SerializeField] private Slider loadingBarSlider;

        [Header("Base")]
        [SerializeField] private List<string> maps;

        [Header("DLC")]
        [SerializeField] private string mapDLC_Path;
        [SerializeField] private List<String> mapsDLC;

        public string[] mapsCollection { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);

            GetDLCMaps();

        }
        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }
        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"New scene loaded = {scene.name}");
            DataPersistenceHandler.Instance.LoadGame();

            foreach (string gameScene in maps)
            {
                if (SceneManager.GetActiveScene().name == gameScene)
                {
                }
            }

        }
        private void OnSceneUnloaded(Scene scene)
        {

            //DataPersistenceHandler.Instance.SaveGame();
            foreach (string gameScene in maps)
            {
                if (SceneManager.GetActiveScene().name == gameScene)
                {

                }
            }
        }


        private void GetDLCMaps()
        {
            mapsDLC.Clear();

            string dlcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Application.companyName);
            dlcPath = Path.Combine(dlcPath, Application.productName);
            mapDLC_Path = Path.Combine(dlcPath, "Mods/Maps");

            if (!Directory.Exists(mapDLC_Path)) { Directory.CreateDirectory(mapDLC_Path); }

            string[] MapAssetBundles = Directory.GetFiles(mapDLC_Path);

            foreach (string mapAssetBundle in MapAssetBundles)
            {
                mapsDLC.Add(Path.GetFileNameWithoutExtension(mapAssetBundle));
            }

            UpdateGameMaps();
        }
        private void UpdateGameMaps()
        {
            mapsCollection = new string[(maps.Count + mapsDLC.Count) - 1];
            mapsCollection = maps.Concat(mapsDLC).ToArray();
        }


        public void ChangeScene(string requestedSceneName)
        {
            StartCoroutine(LoadSceneAsync(requestedSceneName));
        }

        private IEnumerator LoadSceneAsync(string requestedSceneName)
        {
            AsyncOperation operation = null;


            if (mapsDLC.Contains(requestedSceneName))
            {
                AssetBundle currentMapBundle = AssetBundle.LoadFromFile(Path.Combine(mapDLC_Path, requestedSceneName));


                string[] scenesPathInCurrentBundle = currentMapBundle.GetAllScenePaths(); //Add fail safe!

                foreach (string scenePath in scenesPathInCurrentBundle)
                {
                    string sceneName = Path.GetFileNameWithoutExtension(scenePath);
                    print(sceneName);

                    string correctedRequestedScene = char.ToUpper(requestedSceneName[0]) + requestedSceneName.Substring(1);
                    if (sceneName == correctedRequestedScene)
                    {
                        operation = SceneManager.LoadSceneAsync(scenePath);

                        loadingScreenObject.SetActive(true);

                        while (!operation.isDone)
                        {
                            float progressValue = Mathf.Clamp01(operation.progress / 0.9f);
                            loadingBarSlider.value = progressValue;
                            yield return null;
                        }

                        loadingScreenObject.SetActive(false);
                    }
                }
            }
            else
            {
                operation = SceneManager.LoadSceneAsync(requestedSceneName);

                loadingScreenObject.SetActive(true);

                while (!operation.isDone)
                {
                    float progressValue = Mathf.Clamp01(operation.progress / 0.9f);
                    loadingBarSlider.value = progressValue;
                    yield return null;
                }

                loadingScreenObject.SetActive(false);
            }

        }
    }
}
using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Unity only invokes RuntimeInitializeOnLoad methods on non-generic classes.
/// Closed Singleton types register their reset callbacks here.
/// </summary>
internal static class SingletonStaticResetRegistry
{
    private static readonly HashSet<Action> ResetCallbacks = new HashSet<Action>();

    internal static void Register(Action resetCallback)
    {
        ResetCallbacks.Add(resetCallback);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRegisteredSingletons()
    {
        foreach (Action resetCallback in ResetCallbacks)
        {
            resetCallback.Invoke();
        }
    }
}

/// <summary>
/// Base class for a scene-owned singleton.
///
/// This class does not create a missing instance automatically. Add one concrete
/// singleton component to a scene, or use PersistentSingleton when it should
/// survive scene changes.
/// </summary>
/// <typeparam name="T">The concrete component inheriting this class.</typeparam>
[DefaultExecutionOrder(-1000)]
public abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T instance;
    private static bool hasLoggedMissingInstance;

    static Singleton()
    {
        SingletonStaticResetRegistry.Register(ResetStaticState);
    }

    /// <summary>
    /// Gets the loaded instance. If Awake has not assigned it yet, this also checks
    /// inactive scene objects. A missing instance is logged only once.
    /// </summary>
    public static T Instance => GetOrFindInstance(true);

    /// <summary>
    /// True only when an instance has already been registered.
    /// This property does not search the scene.
    /// </summary>
    public static bool HasInstance => instance != null;

    /// <summary>
    /// Safely gets the loaded instance without logging when one is unavailable.
    /// </summary>
    public static bool TryGetInstance(out T foundInstance)
    {
        foundInstance = GetOrFindInstance(false);
        return foundInstance != null;
    }

    /// <summary>
    /// Override in a specialized singleton base to retain the owning GameObject
    /// across scene changes.
    /// </summary>
    protected virtual bool PersistAcrossScenes => false;

    protected virtual void Awake()
    {
        T thisInstance = this as T;
        if (thisInstance == null)
        {
            Debug.LogError(
                $"{GetType().Name} must inherit {nameof(Singleton<T>)} using its own " +
                $"type, for example: MyManager : Singleton<MyManager>.",
                this);
            enabled = false;
            return;
        }

        if (instance != null && instance != thisInstance)
        {
            Debug.LogWarning(
                $"Duplicate {typeof(T).Name} found on '{gameObject.name}'. " +
                "The duplicate GameObject will be destroyed.",
                this);
            Destroy(gameObject);
            return;
        }

        instance = thisInstance;
        hasLoggedMissingInstance = false;

        if (PersistAcrossScenes)
        {
            DontDestroyOnLoad(gameObject);
        }
    }

    protected virtual void OnDestroy()
    {
        T thisInstance = this as T;
        if (instance == thisInstance)
        {
            instance = null;
        }
    }

    private static T GetOrFindInstance(bool logWhenMissing)
    {
        if (instance == null)
        {
            instance = UnityEngine.Object.FindFirstObjectByType<T>(
                FindObjectsInactive.Include);
        }

        if (instance != null)
        {
            hasLoggedMissingInstance = false;
            return instance;
        }

        if (logWhenMissing && !hasLoggedMissingInstance)
        {
            Debug.LogError(
                $"{typeof(T).Name} instance was not found in the loaded scenes. " +
                $"Use {nameof(TryGetInstance)} when the instance is optional.");
            hasLoggedMissingInstance = true;
        }

        return null;
    }

    // Called by the non-generic registry because Unity does not invoke
    // RuntimeInitializeOnLoad methods declared inside generic classes.
    private static void ResetStaticState()
    {
        instance = null;
        hasLoggedMissingInstance = false;
    }
}

/// <summary>
/// Singleton variant whose GameObject survives scene changes.
/// </summary>
public abstract class PersistentSingleton<T> : Singleton<T> where T : MonoBehaviour
{
    protected sealed override bool PersistAcrossScenes => true;
}

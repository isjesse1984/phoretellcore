# Phoretell Save System

The reusable save-system code does not own a game's save-data classes. Each Unity
project defines those classes under its own `Assets` folder, so changing one game
cannot change another game or the package.

## Define project data

```csharp
using System;
using UnityEngine;

[Serializable]
public class MyGameData
{
    public string playerName = "New Player";
    public int currentLevel;
    public Vector3 playerPosition;
}
```

Use public fields (or fields marked with `SerializeField`) supported by Unity's
`JsonUtility`. Avoid dictionaries, interface-typed fields, and direct scene-object
references. Save stable IDs for assets or scene objects instead.

## Capture and restore it

Any number of components can contribute to the same data object:

```csharp
using Phoretell;
using UnityEngine;

public class PlayerSaveData : MonoBehaviour, ISaveLoad<MyGameData>
{
    public void SaveData(MyGameData data)
    {
        data.playerPosition = transform.position;
    }

    public void LoadData(MyGameData data)
    {
        transform.position = data.playerPosition;
    }
}
```

`DataPersistenceHandler` finds every `ISaveLoad<MyGameData>` component, gives them
the same `MyGameData` instance, and saves it as its own file. A different project
can define `RacingGameData`, `PuzzleGameData`, or several separate data classes
without editing the save-system package.

The data class must be serializable and have a parameterless constructor. Its full
type name is the default stable save key. Implement `ISaveKeyProvider` when you need
to preserve an old key or deliberately choose another one.

## Game flow

1. Put one `DataPersistenceHandler` in the startup scene.
2. Call `ChangeSelectedProfileId("profile-1")`.
3. Call `NewGame()`, `SaveGame()`, or `LoadGame()`.
4. Set a user-facing slot label with `SetSelectedProfileDisplayName`.

The generic save-slot menu does not reference a game's `GameHandler` or
`SceneHandler`. Connect its `On Profile Loaded (String)` Inspector event to a
project-owned method that performs the appropriate scene change.

For scene-specific providers, load the destination scene before calling
`LoadGame()`, or call `LoadGame()` again after that scene has loaded, so those
providers exist when discovery runs.

## UPM package boundary

When this becomes a Unity Package Manager package, put the manager, file handler,
interfaces, and generic menu under:

```text
Packages/com.phoretell.save-system/
```

Keep `MyGameData` and all `ISaveLoad<MyGameData>` implementations under the game's
`Assets/` folder. Do not put game-specific data classes in the package repository.

## Editor management window

Open **Tools > Phoretell > Data Persistence** to inspect the data classes currently
referenced by `ISaveLoad<TData>` providers, open or locate their scripts, create a
new data class and companion provider, or edit project-wide runtime settings.

Newly generated providers cache exact-name, exact-type field bindings in `Awake`.
They can target a manually assigned `MonoBehaviour`, or automatically choose the
other component on the same GameObject with the most matching public or
`[SerializeField]` fields. Static, readonly, and non-serialized fields are ignored.

The settings asset is created under `Assets/Resources` only when requested from the
window. Without one, the runtime retains its previous defaults: pretty-printed JSON,
`{dataKey}.4TELL` files, and a `Saves` directory under
`Application.persistentDataPath`.

Encryption is intentionally not advertised as implemented. If the reserved
encryption setting is enabled, save and load operations are blocked rather than
silently writing plaintext. The authenticated-encryption integration point is in
`FileDataHandler`.

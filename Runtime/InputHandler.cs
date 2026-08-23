using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Central access point for the local PlayerInput component.
///
/// Action references are cached once instead of being searched every time controls
/// are locked or unlocked. Action-map switching is delegated to PlayerInput so only
/// the selected map remains active.
/// </summary>
[RequireComponent(typeof(PlayerInput))]
public sealed class InputHandler : PersistentSingleton<InputHandler>
{
    private const string DefaultGameplayMapName = "Player";
    private const string DefaultMoveActionName = "Move";
    private const string DefaultLookActionName = "Look";

    [Header("Input")]
    [SerializeField] private PlayerInput playerInput;
    [SerializeField] private string gameplayActionMapName = DefaultGameplayMapName;
    [SerializeField] private string moveActionName = DefaultMoveActionName;
    [SerializeField] private string lookActionName = DefaultLookActionName;

    [Header("Current State")]
    [SerializeField] private bool mouseVisible;
    [SerializeField] private bool lookLocked;
    [SerializeField] private bool moveLocked;

    private InputActionMap gameplayActionMap;
    private InputAction moveAction;
    private InputAction lookAction;
    private bool referencesResolved;

    public PlayerInput PlayerInput => playerInput;
    public InputActionAsset Actions =>
        playerInput != null ? playerInput.actions : null;
    public InputActionMap CurrentActionMap =>
        playerInput != null ? playerInput.currentActionMap : null;
    public string CurrentActionMapName =>
        CurrentActionMap != null ? CurrentActionMap.name : string.Empty;
    public Vector2 MoveInput =>
        moveAction != null && moveAction.enabled
            ? moveAction.ReadValue<Vector2>()
            : Vector2.zero;
    public Vector2 LookInput =>
        lookAction != null && lookAction.enabled
            ? lookAction.ReadValue<Vector2>()
            : Vector2.zero;
    public bool IsLookLocked => lookLocked;
    public bool IsMoveLocked => moveLocked;
    public bool IsMouseVisible => mouseVisible;

    protected override void Awake()
    {
        base.Awake();

        if (!TryGetInstance(out InputHandler activeHandler) ||
            activeHandler != this)
        {
            return;
        }

        ResolveReferences(true);
        ApplyCursorState();
        ApplyGameplayLocks();
    }

    /// <summary>
    /// Compatibility accessor for existing code.
    /// Prefer the Actions property in new code.
    /// </summary>
    public InputActionAsset GetInput()
    {
        return Actions;
    }

    /// <summary>
    /// Switches to one action map and disables the previously active map.
    /// </summary>
    public void ChangeActionMap(string actionMapName)
    {
        TryChangeActionMap(actionMapName);
    }

    public bool TryChangeActionMap(string actionMapName)
    {
        if (!ResolveReferences(true))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(actionMapName))
        {
            Debug.LogError("An action-map name is required.", this);
            return false;
        }

        InputActionMap requestedMap = playerInput.actions.FindActionMap(
            actionMapName,
            false);

        if (requestedMap == null)
        {
            Debug.LogError(
                $"Action map '{actionMapName}' was not found in " +
                $"'{playerInput.actions.name}'.",
                this);
            return false;
        }

        if (playerInput.currentActionMap != requestedMap)
        {
            playerInput.SwitchCurrentActionMap(requestedMap.name);
        }
        else if (!requestedMap.enabled)
        {
            requestedMap.Enable();
        }

        if (requestedMap == gameplayActionMap)
        {
            ApplyGameplayLocks();
        }

        return true;
    }

    /// <summary>
    /// Preserves the original API while delegating to focused control methods.
    /// A locked action is disabled and therefore returns no input.
    /// </summary>
    public void ToggleControls(
        bool mouseVisible,
        bool lookLocked,
        bool moveLocked)
    {
        SetMouseVisible(mouseVisible);
        SetLookLocked(lookLocked);
        SetMoveLocked(moveLocked);
    }

    public void SetMouseVisible(bool visible)
    {
        mouseVisible = visible;
        ApplyCursorState();
    }

    public void SetLookLocked(bool locked)
    {
        lookLocked = locked;

        if (!ResolveReferences(false))
        {
            return;
        }

        ApplyActionLock(lookAction, lookLocked);
    }

    public void SetMoveLocked(bool locked)
    {
        moveLocked = locked;

        if (!ResolveReferences(false))
        {
            return;
        }

        ApplyActionLock(moveAction, moveLocked);
    }

    public void SetAllInputEnabled(bool enabled)
    {
        if (!ResolveReferences(true))
        {
            return;
        }

        if (enabled)
        {
            playerInput.ActivateInput();
            ApplyGameplayLocks();
        }
        else
        {
            playerInput.DeactivateInput();
        }
    }

    public InputAction FindAction(
        string actionMapName,
        string actionName,
        bool logWhenMissing = true)
    {
        if (!ResolveReferences(logWhenMissing))
        {
            return null;
        }

        InputActionMap actionMap = playerInput.actions.FindActionMap(
            actionMapName,
            false);
        InputAction action = actionMap?.FindAction(actionName, false);

        if (action == null && logWhenMissing)
        {
            Debug.LogError(
                $"Input action '{actionMapName}/{actionName}' was not found in " +
                $"'{playerInput.actions.name}'.",
                this);
        }

        return action;
    }

    private bool ResolveReferences(bool logWhenMissing)
    {
        if (playerInput == null)
        {
            playerInput = GetComponent<PlayerInput>();
        }

        if (playerInput == null)
        {
            if (logWhenMissing)
            {
                Debug.LogError(
                    $"{nameof(InputHandler)} requires a {nameof(PlayerInput)} component.",
                    this);
            }

            referencesResolved = false;
            return false;
        }

        if (playerInput.actions == null)
        {
            if (logWhenMissing)
            {
                Debug.LogError(
                    $"Assign an Input Action Asset to {nameof(PlayerInput)} on " +
                    $"'{gameObject.name}'.",
                    this);
            }

            referencesResolved = false;
            return false;
        }

        if (referencesResolved)
        {
            return true;
        }

        gameplayActionMap = playerInput.actions.FindActionMap(
            gameplayActionMapName,
            false);

        if (gameplayActionMap == null)
        {
            if (logWhenMissing)
            {
                Debug.LogError(
                    $"Gameplay action map '{gameplayActionMapName}' was not found in " +
                    $"'{playerInput.actions.name}'.",
                    this);
            }

            return false;
        }

        moveAction = gameplayActionMap.FindAction(moveActionName, false);
        lookAction = gameplayActionMap.FindAction(lookActionName, false);

        if (logWhenMissing)
        {
            if (moveAction == null)
            {
                Debug.LogError(
                    $"Move action '{gameplayActionMapName}/{moveActionName}' was not found.",
                    this);
            }

            if (lookAction == null)
            {
                Debug.LogError(
                    $"Look action '{gameplayActionMapName}/{lookActionName}' was not found.",
                    this);
            }
        }

        referencesResolved = true;
        return true;
    }

    private void ApplyGameplayLocks()
    {
        if (!ResolveReferences(false))
        {
            return;
        }

        ApplyActionLock(lookAction, lookLocked);
        ApplyActionLock(moveAction, moveLocked);
    }

    private void ApplyActionLock(InputAction action, bool locked)
    {
        if (action == null)
        {
            return;
        }

        if (locked)
        {
            action.Disable();
        }
        else if (gameplayActionMap != null && gameplayActionMap.enabled)
        {
            action.Enable();
        }
    }

    private void ApplyCursorState()
    {
        Cursor.visible = mouseVisible;
        Cursor.lockState = mouseVisible
            ? CursorLockMode.None
            : CursorLockMode.Locked;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
        {
            ApplyCursorState();
        }
    }

    private void Reset()
    {
        playerInput = GetComponent<PlayerInput>();
        gameplayActionMapName = DefaultGameplayMapName;
        moveActionName = DefaultMoveActionName;
        lookActionName = DefaultLookActionName;
    }

    private void OnValidate()
    {
        if (playerInput == null)
        {
            playerInput = GetComponent<PlayerInput>();
        }

        referencesResolved = false;
    }
}

using System;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerMovementHandler))]
public sealed class PlayerCinemachineCameraHandler : MonoBehaviour
{
    public enum CameraPerspective
    {
        ThirdPerson,
        FirstPerson
    }

    private const string DefaultActionMap = "Player";
    private const string DefaultLookAction = "Look";
    private const string DefaultChangeViewAction = "ChangeView";

    [Header("References")]
    [SerializeField] private PlayerMovementHandler movementHandler;
    [FormerlySerializedAs("cinemachineCamera")]
    [SerializeField] private CinemachineCamera thirdPersonCamera;
    [SerializeField] private CinemachineCamera firstPersonCamera;
    [Tooltip("The real Camera driven by Cinemachine. Usually the Main Camera.")]
    [SerializeField] private Camera outputCamera;
    [Tooltip("Optional. A child target is generated at runtime when this is empty.")]
    [SerializeField] private Transform cameraTarget;

    [Header("Input - Player Action Map")]
    [SerializeField] private string actionMapName = DefaultActionMap;
    [SerializeField] private string lookActionName = DefaultLookAction;
    [SerializeField] private string changeViewActionName = DefaultChangeViewAction;
    [SerializeField] private bool lockCursorOnEnable = true;
    [SerializeField] private bool invertY;
    [SerializeField, Min(0f)] private float mouseSensitivity = 0.08f;
    [SerializeField, Min(0f)] private float gamepadDegreesPerSecond = 180f;
    [SerializeField] private bool useUnscaledTime;

    [Header("Perspective Switching")]
    [SerializeField] private CameraPerspective startingPerspective =
        CameraPerspective.ThirdPerson;
    [SerializeField, Min(0f)] private float perspectiveBlendDuration = 0.35f;
    [SerializeField] private CinemachineBlendDefinition.Styles blendStyle =
        CinemachineBlendDefinition.Styles.EaseInOut;
    [SerializeField] private bool configureBrainBlend = true;
    [SerializeField] private int activeCameraPriority = 100;
    [SerializeField] private int inactiveCameraPriority;

    [Header("Look and Target")]
    [SerializeField] private Vector3 thirdPersonTargetOffset =
        new Vector3(0f, 1.55f, 0f);
    [SerializeField] private Vector3 firstPersonTargetOffset =
        new Vector3(0f, 1.65f, 0.05f);
    [SerializeField, Range(-89f, 0f)] private float minimumPitch = -35f;
    [SerializeField, Range(0f, 89f)] private float maximumPitch = 70f;
    [SerializeField] private bool lowerTargetWhileCrouching = true;
    [SerializeField, Min(0f)] private float targetPositionSharpness = 15f;
    [SerializeField] private bool alignPlayerYawInFirstPerson = true;
    [SerializeField] private bool rotatePlayerToMovementInThirdPerson = true;

    [Header("Third Person Rig")]
    [SerializeField, Range(1f, 179f)] private float thirdPersonFieldOfView = 65f;
    [SerializeField, Min(0.01f)] private float thirdPersonDistance = 4f;
    [SerializeField] private Vector3 shoulderOffset = new Vector3(0.45f, 0.15f, 0f);
    [SerializeField] private float verticalArmLength = 0.35f;
    [SerializeField, Range(0f, 1f)] private float cameraSide = 0.5f;
    [SerializeField] private Vector3 thirdPersonDamping =
        new Vector3(0.1f, 0.25f, 0.15f);
    [SerializeField] private bool avoidObstacles = true;
    [SerializeField] private LayerMask cameraCollisionLayers = ~0;
    [SerializeField, Min(0.001f)] private float cameraCollisionRadius = 0.2f;
    [SerializeField, Min(0f)] private float collisionDampingIn;
    [SerializeField, Min(0f)] private float collisionDampingOut = 0.5f;

    [Header("First Person Rig")]
    [SerializeField, Range(1f, 179f)] private float firstPersonFieldOfView = 75f;
    [SerializeField, Min(0.001f)] private float firstPersonNearClip = 0.05f;
    [SerializeField] private bool hideBodyInFirstPerson = true;
    [SerializeField] private bool preserveBodyShadows = true;
    [Tooltip("Leave empty to automatically use every Renderer under the player.")]
    [SerializeField] private Renderer[] firstPersonHiddenRenderers;

    [Header("Automatic Setup")]
    [SerializeField] private bool createCamerasIfMissing = true;
    [SerializeField] private bool addBrainIfMissing = true;
    [SerializeField] private bool applySettingsToAssignedCameras = true;

    private InputHandler inputHandler;
    private InputAction lookAction;
    private InputAction changeViewAction;
    private CharacterController characterController;
    private CinemachineBrain cinemachineBrain;
    private RendererState[] rendererStates = Array.Empty<RendererState>();
    private Vector3 currentTargetLocalPosition;
    private float standingControllerHeight;
    private float yaw;
    private float pitch;
    private bool ownsThirdPersonCamera;
    private bool ownsFirstPersonCamera;
    private bool ownsRuntimeTarget;
    private CameraPerspective currentPerspective;

    private struct RendererState
    {
        public Renderer Renderer;
        public bool Enabled;
        public ShadowCastingMode ShadowCastingMode;
    }

    public event Action<CameraPerspective> PerspectiveChanged;

    public CinemachineCamera ThirdPersonCamera => thirdPersonCamera;
    public CinemachineCamera FirstPersonCamera => firstPersonCamera;
    public CinemachineBrain Brain => cinemachineBrain;
    public Camera OutputCamera => outputCamera;
    public Transform CameraTarget => cameraTarget;
    public CameraPerspective CurrentPerspective => currentPerspective;
    public bool IsFirstPerson => currentPerspective == CameraPerspective.FirstPerson;
    public float Yaw => yaw;
    public float Pitch => pitch;

    private void Reset()
    {
        movementHandler = GetComponent<PlayerMovementHandler>();
        characterController = GetComponent<CharacterController>();
        outputCamera = Camera.main;
        actionMapName = DefaultActionMap;
        lookActionName = DefaultLookAction;
        changeViewActionName = DefaultChangeViewAction;
    }

    private void Awake()
    {
        ResolveReferences();
        CacheRendererStates();

        if (!EnsureOutputCamera() || !EnsureCameraTarget() ||
            !EnsureCinemachineCameras())
        {
            enabled = false;
            return;
        }

        ConfigureCameraTargets();
        movementHandler.SetMovementReference(outputCamera.transform);
        InitializeRotation();
        SetPerspective(startingPerspective, false);
    }

    private void OnEnable()
    {
        inputHandler = InputHandler.Instance;
        if (inputHandler == null)
        {
            Debug.LogError(
                $"{nameof(PlayerCinemachineCameraHandler)} requires an active " +
                $"{nameof(InputHandler)}.",
                this);
            enabled = false;
            return;
        }

        lookAction = inputHandler.FindAction(actionMapName, lookActionName);
        changeViewAction = inputHandler.FindAction(
            actionMapName,
            changeViewActionName);

        if (lookAction == null || changeViewAction == null)
        {
            enabled = false;
            return;
        }

        changeViewAction.performed += OnChangeViewPerformed;
        ApplyFirstPersonRendererState(IsFirstPerson);

        if (lockCursorOnEnable)
            inputHandler.SetMouseVisible(false);
    }

    private void OnDisable()
    {
        if (changeViewAction != null)
            changeViewAction.performed -= OnChangeViewPerformed;

        RestoreRendererStates();
    }

    private void LateUpdate()
    {
        UpdateLookRotation();

        if (IsFirstPerson && alignPlayerYawInFirstPerson)
        {
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        UpdateCameraTargetPosition();
        cameraTarget.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void OnDestroy()
    {
        RestoreRendererStates();

        if (ownsThirdPersonCamera && thirdPersonCamera != null)
            Destroy(thirdPersonCamera.gameObject);

        if (ownsFirstPersonCamera && firstPersonCamera != null)
            Destroy(firstPersonCamera.gameObject);

        if (ownsRuntimeTarget && cameraTarget != null)
            Destroy(cameraTarget.gameObject);
    }

    public void TogglePerspective()
    {
        SetPerspective(IsFirstPerson
            ? CameraPerspective.ThirdPerson
            : CameraPerspective.FirstPerson);
    }

    public void SetPerspective(CameraPerspective perspective)
    {
        SetPerspective(perspective, true);
    }

    public void SetFirstPerson(bool firstPerson)
    {
        SetPerspective(firstPerson
            ? CameraPerspective.FirstPerson
            : CameraPerspective.ThirdPerson);
    }

    public void RecenterBehindPlayer()
    {
        yaw = NormalizeAngle(transform.eulerAngles.y);
        pitch = Mathf.Clamp(0f, minimumPitch, maximumPitch);
        ApplyTargetTransformImmediately();
    }

    public void SetLookEnabled(bool lookEnabled)
    {
        if (inputHandler != null)
            inputHandler.SetLookLocked(!lookEnabled);
    }

    public void ApplyTargetTransformImmediately()
    {
        if (cameraTarget == null)
            return;

        currentTargetLocalPosition = GetDesiredTargetLocalPosition();
        cameraTarget.position = transform.TransformPoint(currentTargetLocalPosition);
        cameraTarget.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void SetPerspective(
        CameraPerspective perspective,
        bool notifyListeners)
    {
        currentPerspective = perspective;
        bool firstPerson = perspective == CameraPerspective.FirstPerson;

        if (thirdPersonCamera != null)
        {
            thirdPersonCamera.Priority = firstPerson
                ? inactiveCameraPriority
                : activeCameraPriority;
        }

        if (firstPersonCamera != null)
        {
            firstPersonCamera.Priority = firstPerson
                ? activeCameraPriority
                : inactiveCameraPriority;
        }

        CinemachineCamera activeCamera = firstPerson
            ? firstPersonCamera
            : thirdPersonCamera;
        activeCamera?.Prioritize();

        movementHandler.SetRotateTowardsMovement(
            !firstPerson && rotatePlayerToMovementInThirdPerson);
        ApplyFirstPersonRendererState(firstPerson);

        if (notifyListeners)
            PerspectiveChanged?.Invoke(currentPerspective);
    }

    private void ResolveReferences()
    {
        if (movementHandler == null)
            movementHandler = GetComponent<PlayerMovementHandler>();

        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (characterController != null)
            standingControllerHeight = characterController.height;
    }

    private bool EnsureOutputCamera()
    {
        if (outputCamera == null)
            outputCamera = Camera.main;

        if (outputCamera == null)
        {
            Debug.LogError(
                "No output Camera was found. Assign a Camera or tag one as MainCamera.",
                this);
            return false;
        }

        if (!outputCamera.TryGetComponent(out cinemachineBrain))
        {
            if (!addBrainIfMissing)
            {
                Debug.LogError(
                    $"Add a {nameof(CinemachineBrain)} to '{outputCamera.name}'.",
                    outputCamera);
                return false;
            }

            cinemachineBrain = outputCamera.gameObject.AddComponent<CinemachineBrain>();
        }

        if (configureBrainBlend)
        {
            cinemachineBrain.DefaultBlend = new CinemachineBlendDefinition(
                blendStyle,
                perspectiveBlendDuration);
        }

        return true;
    }

    private bool EnsureCameraTarget()
    {
        if (cameraTarget != null)
            return true;

        GameObject targetObject = new GameObject("Cinemachine Camera Target");
        cameraTarget = targetObject.transform;
        cameraTarget.SetParent(transform, false);
        cameraTarget.localPosition = thirdPersonTargetOffset;
        ownsRuntimeTarget = true;
        return true;
    }

    private bool EnsureCinemachineCameras()
    {
        if (thirdPersonCamera != null &&
            thirdPersonCamera == firstPersonCamera)
        {
            Debug.LogError(
                "First-person and third-person cameras must be different Cinemachine Cameras.",
                this);
            return false;
        }

        if (!EnsureCamera(
                ref thirdPersonCamera,
                "Player Third Person Camera",
                false,
                ref ownsThirdPersonCamera))
        {
            return false;
        }

        if (!EnsureCamera(
                ref firstPersonCamera,
                "Player First Person Camera",
                true,
                ref ownsFirstPersonCamera))
        {
            return false;
        }

        if (thirdPersonCamera == firstPersonCamera)
        {
            Debug.LogError(
                "First-person and third-person cameras must be different Cinemachine Cameras.",
                this);
            return false;
        }

        return true;
    }

    private bool EnsureCamera(
        ref CinemachineCamera camera,
        string cameraName,
        bool firstPerson,
        ref bool ownsCamera)
    {
        bool createdCamera = false;

        if (camera == null)
        {
            if (!createCamerasIfMissing)
            {
                Debug.LogError(
                    $"Assign the {cameraName} Cinemachine Camera.",
                    this);
                return false;
            }

            GameObject cameraObject = new GameObject(cameraName);
            camera = cameraObject.AddComponent<CinemachineCamera>();
            createdCamera = true;
            ownsCamera = true;
        }

        CinemachineThirdPersonFollow follow =
            camera.GetComponent<CinemachineThirdPersonFollow>();

        if (follow == null)
        {
            CinemachineComponentBase existingBody =
                camera.GetCinemachineComponent(CinemachineCore.Stage.Body);

            if (existingBody != null)
            {
                Debug.LogError(
                    $"'{camera.name}' uses {existingBody.GetType().Name}. Replace it with " +
                    $"{nameof(CinemachineThirdPersonFollow)}.",
                    camera);
                return false;
            }

            follow = camera.gameObject.AddComponent<CinemachineThirdPersonFollow>();
            createdCamera = true;
        }

        if (createdCamera || applySettingsToAssignedCameras)
        {
            if (firstPerson)
                ConfigureFirstPersonCamera(camera, follow);
            else
                ConfigureThirdPersonCamera(camera, follow);
        }

        return true;
    }

    private void ConfigureThirdPersonCamera(
        CinemachineCamera camera,
        CinemachineThirdPersonFollow follow)
    {
        LensSettings lens = camera.Lens;
        lens.FieldOfView = thirdPersonFieldOfView;
        camera.Lens = lens;

        follow.CameraDistance = thirdPersonDistance;
        follow.ShoulderOffset = shoulderOffset;
        follow.VerticalArmLength = verticalArmLength;
        follow.CameraSide = cameraSide;
        follow.Damping = thirdPersonDamping;

        CinemachineThirdPersonFollow.ObstacleSettings obstacles =
            follow.AvoidObstacles;
        obstacles.Enabled = avoidObstacles;
        obstacles.CollisionFilter = cameraCollisionLayers;
        obstacles.IgnoreTag = CompareTag("Untagged") ? string.Empty : tag;
        obstacles.CameraRadius = cameraCollisionRadius;
        obstacles.DampingIntoCollision = collisionDampingIn;
        obstacles.DampingFromCollision = collisionDampingOut;
        follow.AvoidObstacles = obstacles;
    }

    private void ConfigureFirstPersonCamera(
        CinemachineCamera camera,
        CinemachineThirdPersonFollow follow)
    {
        LensSettings lens = camera.Lens;
        lens.FieldOfView = firstPersonFieldOfView;
        lens.NearClipPlane = firstPersonNearClip;
        camera.Lens = lens;

        follow.CameraDistance = 0f;
        follow.ShoulderOffset = Vector3.zero;
        follow.VerticalArmLength = 0f;
        follow.CameraSide = 0.5f;
        follow.Damping = Vector3.zero;

        CinemachineThirdPersonFollow.ObstacleSettings obstacles =
            follow.AvoidObstacles;
        obstacles.Enabled = false;
        follow.AvoidObstacles = obstacles;
    }

    private void ConfigureCameraTargets()
    {
        ConfigureCameraTarget(thirdPersonCamera);
        ConfigureCameraTarget(firstPersonCamera);
    }

    private void ConfigureCameraTarget(CinemachineCamera camera)
    {
        CameraTarget target = camera.Target;
        target.TrackingTarget = cameraTarget;
        target.LookAtTarget = null;
        target.CustomLookAtTarget = false;
        camera.Target = target;
    }

    private void InitializeRotation()
    {
        Vector3 angles = cameraTarget.rotation.eulerAngles;
        yaw = NormalizeAngle(angles.y);
        pitch = Mathf.Clamp(
            NormalizeAngle(angles.x),
            minimumPitch,
            maximumPitch);
        currentTargetLocalPosition = thirdPersonTargetOffset;
        ApplyTargetTransformImmediately();
    }

    private void UpdateLookRotation()
    {
        if (lookAction == null || !lookAction.enabled)
            return;

        Vector2 lookInput = lookAction.ReadValue<Vector2>();
        if (lookInput.sqrMagnitude <= 0f)
            return;

        bool deltaInput = lookAction.activeControl != null &&
            lookAction.activeControl.device is Pointer;
        float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        float multiplier = deltaInput
            ? mouseSensitivity
            : gamepadDegreesPerSecond * deltaTime;

        yaw = NormalizeAngle(yaw + lookInput.x * multiplier);
        float pitchDirection = invertY ? 1f : -1f;
        pitch = Mathf.Clamp(
            pitch + lookInput.y * multiplier * pitchDirection,
            minimumPitch,
            maximumPitch);
    }

    private void UpdateCameraTargetPosition()
    {
        Vector3 desiredPosition = GetDesiredTargetLocalPosition();
        float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        float interpolation = targetPositionSharpness <= 0f
            ? 1f
            : 1f - Mathf.Exp(-targetPositionSharpness * deltaTime);

        currentTargetLocalPosition = Vector3.Lerp(
            currentTargetLocalPosition,
            desiredPosition,
            interpolation);
        cameraTarget.position = transform.TransformPoint(currentTargetLocalPosition);
    }

    private Vector3 GetDesiredTargetLocalPosition()
    {
        Vector3 position = IsFirstPerson
            ? firstPersonTargetOffset
            : thirdPersonTargetOffset;

        if (lowerTargetWhileCrouching && characterController != null)
        {
            position.y -= Mathf.Max(
                0f,
                standingControllerHeight - characterController.height);
        }

        return position;
    }

    private void CacheRendererStates()
    {
        if (firstPersonHiddenRenderers == null ||
            firstPersonHiddenRenderers.Length == 0)
        {
            firstPersonHiddenRenderers = GetComponentsInChildren<Renderer>(true);
        }

        rendererStates = new RendererState[firstPersonHiddenRenderers.Length];
        for (int i = 0; i < firstPersonHiddenRenderers.Length; i++)
        {
            Renderer targetRenderer = firstPersonHiddenRenderers[i];
            rendererStates[i] = new RendererState
            {
                Renderer = targetRenderer,
                Enabled = targetRenderer != null && targetRenderer.enabled,
                ShadowCastingMode = targetRenderer != null
                    ? targetRenderer.shadowCastingMode
                    : ShadowCastingMode.Off
            };
        }
    }

    private void ApplyFirstPersonRendererState(bool firstPerson)
    {
        if (!hideBodyInFirstPerson || !firstPerson)
        {
            RestoreRendererStates();
            return;
        }

        for (int i = 0; i < rendererStates.Length; i++)
        {
            RendererState state = rendererStates[i];
            if (state.Renderer == null || !state.Enabled)
                continue;

            if (preserveBodyShadows)
            {
                state.Renderer.enabled = true;
                state.Renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
            else
            {
                state.Renderer.enabled = false;
            }
        }
    }

    private void RestoreRendererStates()
    {
        for (int i = 0; i < rendererStates.Length; i++)
        {
            RendererState state = rendererStates[i];
            if (state.Renderer == null)
                continue;

            state.Renderer.enabled = state.Enabled;
            state.Renderer.shadowCastingMode = state.ShadowCastingMode;
        }
    }

    private void OnChangeViewPerformed(InputAction.CallbackContext context)
    {
        TogglePerspective();
    }

    private void OnValidate()
    {
        if (movementHandler == null)
            movementHandler = GetComponent<PlayerMovementHandler>();

        mouseSensitivity = Mathf.Max(0f, mouseSensitivity);
        gamepadDegreesPerSecond = Mathf.Max(0f, gamepadDegreesPerSecond);
        perspectiveBlendDuration = Mathf.Max(0f, perspectiveBlendDuration);
        targetPositionSharpness = Mathf.Max(0f, targetPositionSharpness);
        thirdPersonFieldOfView = Mathf.Clamp(thirdPersonFieldOfView, 1f, 179f);
        firstPersonFieldOfView = Mathf.Clamp(firstPersonFieldOfView, 1f, 179f);
        thirdPersonDistance = Mathf.Max(0.01f, thirdPersonDistance);
        firstPersonNearClip = Mathf.Max(0.001f, firstPersonNearClip);
        cameraCollisionRadius = Mathf.Max(0.001f, cameraCollisionRadius);
        collisionDampingIn = Mathf.Max(0f, collisionDampingIn);
        collisionDampingOut = Mathf.Max(0f, collisionDampingOut);
        thirdPersonDamping.x = Mathf.Max(0f, thirdPersonDamping.x);
        thirdPersonDamping.y = Mathf.Max(0f, thirdPersonDamping.y);
        thirdPersonDamping.z = Mathf.Max(0f, thirdPersonDamping.z);

        if (maximumPitch < minimumPitch)
            maximumPitch = Mathf.Max(0f, minimumPitch);

        if (activeCameraPriority <= inactiveCameraPriority)
            activeCameraPriority = inactiveCameraPriority + 1;
    }

    private static float NormalizeAngle(float angle)
    {
        return Mathf.Repeat(angle + 180f, 360f) - 180f;
    }
}

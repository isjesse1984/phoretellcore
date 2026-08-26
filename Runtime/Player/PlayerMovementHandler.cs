using UnityEngine;
using UnityEngine.InputSystem;
using Phoretell;

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class PlayerMovementHandler : MonoBehaviour
{
    private const string DefaultActionMap = "Player";
    private const string DefaultMoveAction = "Move";
    private const string DefaultJumpAction = "Jump";
    private const string DefaultSprintAction = "Sprint";
    private const string DefaultCrouchAction = "Crouch";

    [Header("References")]
    [SerializeField] private CharacterController characterController;
    [Tooltip("Usually the gameplay camera. Movement uses its flattened forward and right axes.")]
    [SerializeField] private Transform movementReference;

    [Header("Input - Player Action Map")]
    [SerializeField] private string actionMapName = DefaultActionMap;
    [SerializeField] private string moveActionName = DefaultMoveAction;
    [SerializeField] private string jumpActionName = DefaultJumpAction;
    [SerializeField] private string sprintActionName = DefaultSprintAction;
    [SerializeField] private string crouchActionName = DefaultCrouchAction;
    [SerializeField] private bool switchActionMapOnEnable = true;
    [SerializeField] private bool toggleCrouch = true;

    [Header("Movement")]
    [SerializeField, Min(0f)] private float walkSpeed = 4.5f;
    [SerializeField, Min(0f)] private float sprintSpeed = 7.5f;
    [SerializeField, Min(0f)] private float crouchSpeed = 2.25f;
    [SerializeField, Min(0f)] private float groundAcceleration = 35f;
    [SerializeField, Min(0f)] private float groundDeceleration = 45f;
    [SerializeField, Min(0f)] private float airAcceleration = 10f;
    [SerializeField, Min(0f)] private float airDeceleration = 1.5f;
    [SerializeField] private bool rotateTowardsMovement;
    [SerializeField, Min(0f)] private float rotationSharpness = 14f;
    [SerializeField] private bool sprintRequiresForwardInput = true;
    [SerializeField, Range(-1f, 1f)] private float sprintForwardThreshold = 0.1f;

    [Header("Footsteps")]
    [SerializeField] private AudioClip footstepClip;
    [SerializeField, Range(0f, 1f)] private float footstepVolume = 0.75f;
    [SerializeField, Min(0.1f)] private float walkStepDistance = 1.8f;
    [SerializeField, Min(0.1f)] private float sprintStepDistance = 2.2f;
    [SerializeField, Min(0.1f)] private float crouchStepDistance = 1.2f;
    [SerializeField, Min(0f)] private float minimumFootstepSpeed = 0.15f;

    [Header("Jumping and Gravity")]
    [SerializeField, Min(0f)] private float jumpHeight = 1.35f;
    [SerializeField, Min(0.01f)] private float gravity = 25f;
    [SerializeField, Min(0f)] private float maximumFallSpeed = 50f;
    [SerializeField, Min(0f)] private float groundedGravity = 4f;
    [SerializeField, Min(0f)] private float coyoteTime = 0.12f;
    [SerializeField, Min(0f)] private float jumpBufferTime = 0.15f;
    [SerializeField] private bool allowJumpWhileCrouched = true;

    [Header("Ground Detection")]
    [SerializeField] private LayerMask groundLayers = ~0;
    [SerializeField, Min(0.01f)] private float groundCheckDistance = 0.2f;
    [SerializeField, Min(0f)] private float groundCheckStartOffset = 0.05f;
    [SerializeField] private QueryTriggerInteraction groundTriggerInteraction =
        QueryTriggerInteraction.Ignore;

    [Header("Crouching")]
    [SerializeField, Min(0.1f)] private float crouchingHeight = 1.15f;
    [SerializeField, Min(0f)] private float crouchTransitionSpeed = 5f;
    [SerializeField] private LayerMask ceilingLayers = ~0;

    private InputAction moveAction;
    private InputAction jumpAction;
    private InputAction sprintAction;
    private InputAction crouchAction;

    private Vector2 moveInput;
    private Vector3 planarVelocity;
    private Vector3 groundNormal = Vector3.up;
    private float verticalVelocity;
    private float coyoteTimer;
    private float jumpBufferTimer;
    private float footstepDistanceTravelled;
    private float standingHeight;
    private Vector3 standingCenter;
    private bool crouchRequested;
    private bool isGrounded;
    private bool isSprinting;
    private bool inputReady;
    private readonly RaycastHit[] ceilingHits = new RaycastHit[8];

    public Vector2 MoveInput => moveInput;
    public Vector3 Velocity => planarVelocity + Vector3.up * verticalVelocity;
    public Vector3 GroundNormal => groundNormal;
    public float CurrentSpeed => new Vector2(planarVelocity.x, planarVelocity.z).magnitude;
    public float VerticalSpeed => verticalVelocity;
    public bool IsGrounded => isGrounded;
    public bool IsSprinting => isSprinting;
    public bool IsCrouching => characterController != null &&
        characterController.height < standingHeight - 0.01f;
    public Transform MovementReference => movementReference;
    public bool RotateTowardsMovement => rotateTowardsMovement;

    private void Reset()
    {
        characterController = GetComponent<CharacterController>();

        Camera mainCamera = Camera.main;
        movementReference = mainCamera != null ? mainCamera.transform : null;

        actionMapName = DefaultActionMap;
        moveActionName = DefaultMoveAction;
        jumpActionName = DefaultJumpAction;
        sprintActionName = DefaultSprintAction;
        crouchActionName = DefaultCrouchAction;
    }

    private void Awake()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (movementReference == null && Camera.main != null)
            movementReference = Camera.main.transform;

        standingHeight = characterController.height;
        standingCenter = characterController.center;
        crouchingHeight = Mathf.Clamp(
            crouchingHeight,
            characterController.radius * 2f,
            standingHeight);
    }

    private void OnEnable()
    {
        inputReady = ResolveInputActions();

        if (!inputReady)
        {
            enabled = false;
            return;
        }

        jumpAction.performed += OnJumpPerformed;
        crouchAction.performed += OnCrouchPerformed;

        if (!toggleCrouch)
            crouchAction.canceled += OnCrouchCanceled;
    }

    private void OnDisable()
    {
        if (jumpAction != null)
            jumpAction.performed -= OnJumpPerformed;

        if (crouchAction != null)
        {
            crouchAction.performed -= OnCrouchPerformed;
            crouchAction.canceled -= OnCrouchCanceled;
        }

        moveInput = Vector2.zero;
        planarVelocity = Vector3.zero;
        isSprinting = false;
        inputReady = false;
    }

    private void Update()
    {
        if (!inputReady)
            return;

        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f)
            return;

        UpdateGroundState();
        UpdateTimers(deltaTime);
        UpdateCrouch(deltaTime);
        ReadMovementInput();
        UpdatePlanarVelocity(deltaTime);
        TryConsumeBufferedJump();
        UpdateVerticalVelocity(deltaTime);
        RotateTowardsVelocity(deltaTime);

        Vector3 positionBeforeMove = transform.position;
        CollisionFlags collisionFlags = characterController.Move(
            (planarVelocity + Vector3.up * verticalVelocity) * deltaTime);

        if ((collisionFlags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
            verticalVelocity = 0f;

        if ((collisionFlags & CollisionFlags.Below) != 0)
        {
            isGrounded = true;
            if (verticalVelocity < 0f)
                verticalVelocity = -groundedGravity;
        }

        UpdateFootsteps(positionBeforeMove);
    }

    public void SetCrouching(bool crouching)
    {
        crouchRequested = crouching;
    }

    public void SetMovementReference(Transform reference)
    {
        movementReference = reference;
    }

    public void SetRotateTowardsMovement(bool shouldRotate)
    {
        rotateTowardsMovement = shouldRotate;
    }

    public void StopImmediately()
    {
        planarVelocity = Vector3.zero;
        verticalVelocity = 0f;
        moveInput = Vector2.zero;
    }

    private bool ResolveInputActions()
    {
        InputHandler input = InputHandler.Instance;
        if (input == null)
        {
            Debug.LogError(
                $"{nameof(PlayerMovementHandler)} requires an active {nameof(InputHandler)}.",
                this);
            return false;
        }

        if (switchActionMapOnEnable && !input.TryChangeActionMap(actionMapName))
            return false;

        moveAction = input.FindAction(actionMapName, moveActionName);
        jumpAction = input.FindAction(actionMapName, jumpActionName);
        sprintAction = input.FindAction(actionMapName, sprintActionName);
        crouchAction = input.FindAction(actionMapName, crouchActionName);

        return moveAction != null && jumpAction != null &&
            sprintAction != null && crouchAction != null;
    }

    private void ReadMovementInput()
    {
        moveInput = moveAction.enabled
            ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f)
            : Vector2.zero;

        bool sprintPressed = sprintAction.enabled && sprintAction.IsPressed();
        bool hasForwardInput = !sprintRequiresForwardInput ||
            moveInput.y > sprintForwardThreshold;

        isSprinting = sprintPressed && hasForwardInput && !IsCrouching;
    }

    private void UpdatePlanarVelocity(float deltaTime)
    {
        Vector3 moveDirection = GetCameraRelativeDirection(moveInput);
        float targetSpeed = IsCrouching
            ? crouchSpeed
            : isSprinting
                ? sprintSpeed
                : walkSpeed;

        Vector3 targetVelocity = moveDirection * targetSpeed;
        bool isAccelerating = targetVelocity.sqrMagnitude > planarVelocity.sqrMagnitude ||
            Vector3.Dot(targetVelocity, planarVelocity) < 0f;

        float changeRate;
        if (isGrounded)
        {
            changeRate = isAccelerating
                ? groundAcceleration
                : groundDeceleration;
        }
        else
        {
            changeRate = isAccelerating
                ? airAcceleration
                : airDeceleration;
        }

        planarVelocity = Vector3.MoveTowards(
            planarVelocity,
            targetVelocity,
            changeRate * deltaTime);
        planarVelocity.y = 0f;
    }

    private Vector3 GetCameraRelativeDirection(Vector2 input)
    {
        if (movementReference == null && Camera.main != null)
            movementReference = Camera.main.transform;

        Transform reference = movementReference != null
            ? movementReference
            : transform;

        Vector3 forward = Vector3.ProjectOnPlane(reference.forward, Vector3.up);
        Vector3 right = Vector3.ProjectOnPlane(reference.right, Vector3.up);

        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(Vector3.up, forward);

        Vector3 direction = forward.normalized * input.y + right.normalized * input.x;
        return Vector3.ClampMagnitude(direction, 1f);
    }

    private void UpdateGroundState()
    {
        Bounds bounds = characterController.bounds;
        float radius = Mathf.Max(
            0.01f,
            Mathf.Min(bounds.extents.x, bounds.extents.z) -
            characterController.skinWidth);
        Vector3 castOrigin = new Vector3(
            bounds.center.x,
            bounds.min.y + radius + groundCheckStartOffset,
            bounds.center.z);

        bool foundGround = Physics.SphereCast(
            castOrigin,
            radius,
            Vector3.down,
            out RaycastHit hit,
            groundCheckDistance + groundCheckStartOffset,
            groundLayers,
            groundTriggerInteraction);

        bool walkableGround = foundGround &&
            Vector3.Angle(hit.normal, Vector3.up) <= characterController.slopeLimit + 0.1f;
        bool ascending = verticalVelocity > 0.01f;

        isGrounded = !ascending &&
            (characterController.isGrounded || walkableGround);
        groundNormal = walkableGround ? hit.normal : Vector3.up;

        if (isGrounded)
            coyoteTimer = coyoteTime;
    }

    private void UpdateTimers(float deltaTime)
    {
        if (!isGrounded)
            coyoteTimer = Mathf.Max(0f, coyoteTimer - deltaTime);

        jumpBufferTimer = Mathf.Max(0f, jumpBufferTimer - deltaTime);
    }

    private void TryConsumeBufferedJump()
    {
        if (jumpBufferTimer <= 0f || coyoteTimer <= 0f)
            return;

        if (IsCrouching && !allowJumpWhileCrouched)
            return;

        verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
        jumpBufferTimer = 0f;
        coyoteTimer = 0f;
        isGrounded = false;
    }

    private void UpdateVerticalVelocity(float deltaTime)
    {
        if (isGrounded && verticalVelocity <= 0f)
        {
            verticalVelocity = -groundedGravity;
            return;
        }

        verticalVelocity = Mathf.Max(
            verticalVelocity - gravity * deltaTime,
            -maximumFallSpeed);
    }

    private void RotateTowardsVelocity(float deltaTime)
    {
        if (!rotateTowardsMovement || planarVelocity.sqrMagnitude < 0.0001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(
            planarVelocity.normalized,
            Vector3.up);
        float interpolation = 1f - Mathf.Exp(-rotationSharpness * deltaTime);
        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            targetRotation,
            interpolation);
    }

    private void UpdateFootsteps(Vector3 positionBeforeMove)
    {
        if (!isGrounded)
        {
            footstepDistanceTravelled = 0f;
            return;
        }

        if (footstepClip == null || CurrentSpeed < minimumFootstepSpeed)
            return;

        Vector3 displacement = transform.position - positionBeforeMove;
        float planarDistance = Vector3.ProjectOnPlane(
            displacement,
            Vector3.up).magnitude;
        if (planarDistance <= 0.0001f)
            return;

        footstepDistanceTravelled += planarDistance;
        float stepDistance = IsCrouching
            ? crouchStepDistance
            : isSprinting
                ? sprintStepDistance
                : walkStepDistance;

        if (footstepDistanceTravelled < stepDistance)
            return;

        footstepDistanceTravelled %= stepDistance;

        AudioHandler audioHandler = AudioHandler.Instance;
        if (audioHandler != null)
            audioHandler.TryPlayEffectAudio(footstepClip, footstepVolume);
    }

    private void UpdateCrouch(float deltaTime)
    {
        float targetHeight = crouchRequested ? crouchingHeight : standingHeight;

        if (!crouchRequested && characterController.height < standingHeight &&
            IsCeilingBlockingStand())
        {
            targetHeight = characterController.height;
        }

        float nextHeight = crouchTransitionSpeed <= 0f
            ? targetHeight
            : Mathf.MoveTowards(
                characterController.height,
                targetHeight,
                crouchTransitionSpeed * deltaTime);

        SetControllerHeightKeepingFeet(nextHeight);
    }

    private void SetControllerHeightKeepingFeet(float height)
    {
        float bottom = standingCenter.y - standingHeight * 0.5f;
        Vector3 center = characterController.center;
        center.y = bottom + height * 0.5f;

        characterController.height = height;
        characterController.center = center;
    }

    private bool IsCeilingBlockingStand()
    {
        float additionalHeight = standingHeight - characterController.height;
        if (additionalHeight <= 0.001f)
            return false;

        float horizontalScale = Mathf.Max(
            Mathf.Abs(transform.lossyScale.x),
            Mathf.Abs(transform.lossyScale.z));
        float verticalScale = Mathf.Abs(transform.lossyScale.y);
        float castRadius = Mathf.Max(
            0.01f,
            (characterController.radius - characterController.skinWidth) *
            horizontalScale);
        float topSphereOffset = characterController.height * 0.5f -
            characterController.radius;
        Vector3 topSphereCenter = transform.TransformPoint(
            characterController.center + Vector3.up * topSphereOffset);

        int hitCount = Physics.SphereCastNonAlloc(
            topSphereCenter,
            castRadius,
            transform.up,
            ceilingHits,
            additionalHeight * verticalScale,
            ceilingLayers,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = ceilingHits[i].collider;
            if (hitCollider != null && hitCollider != characterController &&
                !hitCollider.transform.IsChildOf(transform))
            {
                return true;
            }
        }

        return false;
    }

    private void OnJumpPerformed(InputAction.CallbackContext context)
    {
        jumpBufferTimer = jumpBufferTime;
    }

    private void OnCrouchPerformed(InputAction.CallbackContext context)
    {
        crouchRequested = toggleCrouch ? !crouchRequested : true;
    }

    private void OnCrouchCanceled(InputAction.CallbackContext context)
    {
        if (!toggleCrouch)
            crouchRequested = false;
    }

    private void OnValidate()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        walkSpeed = Mathf.Max(0f, walkSpeed);
        sprintSpeed = Mathf.Max(0f, sprintSpeed);
        crouchSpeed = Mathf.Max(0f, crouchSpeed);
        groundAcceleration = Mathf.Max(0f, groundAcceleration);
        groundDeceleration = Mathf.Max(0f, groundDeceleration);
        airAcceleration = Mathf.Max(0f, airAcceleration);
        airDeceleration = Mathf.Max(0f, airDeceleration);
        gravity = Mathf.Max(0.01f, gravity);
        maximumFallSpeed = Mathf.Max(0f, maximumFallSpeed);
        groundedGravity = Mathf.Max(0f, groundedGravity);
        jumpHeight = Mathf.Max(0f, jumpHeight);
        coyoteTime = Mathf.Max(0f, coyoteTime);
        jumpBufferTime = Mathf.Max(0f, jumpBufferTime);
        crouchTransitionSpeed = Mathf.Max(0f, crouchTransitionSpeed);
        groundCheckDistance = Mathf.Max(0.01f, groundCheckDistance);
        groundCheckStartOffset = Mathf.Max(0f, groundCheckStartOffset);
        walkStepDistance = Mathf.Max(0.1f, walkStepDistance);
        sprintStepDistance = Mathf.Max(0.1f, sprintStepDistance);
        crouchStepDistance = Mathf.Max(0.1f, crouchStepDistance);
        minimumFootstepSpeed = Mathf.Max(0f, minimumFootstepSpeed);

        if (characterController != null)
        {
            float maximumHeight = Application.isPlaying && standingHeight > 0f
                ? standingHeight
                : characterController.height;
            crouchingHeight = Mathf.Clamp(
                crouchingHeight,
                characterController.radius * 2f,
                maximumHeight);
        }
    }
}

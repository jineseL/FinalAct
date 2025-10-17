using UnityEngine;

public class PlayerMotor : MonoBehaviour
{
    //handle all player movement controls: Moving, Jumping, Dashing
    private CharacterController controller;
    Vector3 playerVelocity;
    bool isGrounded;

    [Header("Move")]
    public float speed = 5f;                 // base speed
    public float gravity = -9.8f;
    public float jumpHeight = 1.5f;
    public float maxExternalSpeed = 50f;     // for external force (knockback/pull)

    [Header("Dash Settings")]
    public float dashSpeed = 20f;
    public float dashDuration = 0.15f;
    public float dashCooldown = 1f;
    public bool canDash = true;

    private bool isDashing = false;
    private float dashTimer = 0f;
    private float lastDashTime = -Mathf.Infinity;
    private Vector3 dashDirection;
    private Vector2 lastInput;

    [Header("Jump Settings")]
    public int maxJumps = 2;   // number of jumps allowed (2 = double jump)
    private int jumpCount = 0; // how many jumps have been used
    public bool canJump = true;

    [Header("External Force Lock")]
    [SerializeField] private bool blockNewExternalForces = false; // inspector toggle
    private float externalForceLockUntil = 0f;
    private Vector3 externalForce;
    [SerializeField] private float persistentAccel = 30f;   // how fast we can change persistentVel (units/s^2)
    [SerializeField] private float persistentMaxSpeed = 15f;// cap for persistentVel

    private Vector3 persistentVel;       // current persistent velocity (e.g., blackhole)
    private Vector3 persistentTargetVel; // desired persistent velocity set by effects each frame
    public bool IsExternalForceLocked => blockNewExternalForces && Time.time < externalForceLockUntil;
    public bool canExternalForceApplied = true;

    [Header("Footsteps")]
    [SerializeField] private string[] footstepKeys;  // keys from SoundManager.sfxTable (e.g., "step1","step2")
    [SerializeField] private float stepDistance = 2.1f;       // meters between steps
    [SerializeField] private float minStepInterval = 0.22f;   // seconds between steps
    [SerializeField] private float stepVolume = 0.9f;
    [SerializeField] private Vector2 stepPitchRange = new Vector2(0.95f, 1.05f);

    // 3D playback settings (only affects how it sounds locally)
    [SerializeField] private float stepSpatialBlend = 0.0f;    // 0 = 2D, 1 = 3D
    [SerializeField] private float stepMinDistance = 1.0f;
    [SerializeField] private float stepMaxDistance = 20.0f;

    // runtime
    private float _footstepAccum = 0f;
    private float _footstepCooldown = 0f;

    // ===== Slow =====
    // current slow multiplier (1 = normal speed, 0.6 = 40% slow)
    private float moveFactor = 1f;
    private float slowUntil = 0f;

    private void Start()
    {
        controller = GetComponent<CharacterController>();
    }

    private void Update()
    {
        isGrounded = controller.isGrounded;
        if (isGrounded && playerVelocity.y < 0)
        {
            playerVelocity.y = -2f;
            jumpCount = 0; // reset jump count
        }

        if (blockNewExternalForces && Time.time >= externalForceLockUntil) blockNewExternalForces = false;

        if (isDashing)
        {
            DashMovement();
        }

        // expire slow
        if (Time.time >= slowUntil && moveFactor != 1f)
            moveFactor = 1f;
    }

    public void ApplyExternalForce(Vector3 forceVelocity)
    {
        // Ignore new external forces while locked
        if (!canExternalForceApplied) return;
        if (IsExternalForceLocked) return;

        externalForce = Vector3.ClampMagnitude(externalForce + forceVelocity, maxExternalSpeed);
    }

    // use this if you ever need to override the lock for a special case
    public void ApplyExternalForceOverride(Vector3 forceVelocity)
    {
        externalForce = Vector3.ClampMagnitude(externalForce + forceVelocity, maxExternalSpeed);
    }
    public void LockExternalForces(float duration)
    {
        blockNewExternalForces = true;
        externalForceLockUntil = Mathf.Max(externalForceLockUntil, Time.time + Mathf.Max(0f, duration));
    }

    public void UnlockExternalForces()
    {
        blockNewExternalForces = false;
        externalForceLockUntil = 0f;
    }
    public void SetPersistentExternalVelocity(Vector3 desiredVel)
    {
        if (IsExternalForceLocked) return; // respect knockback lock
        persistentTargetVel = Vector3.ClampMagnitude(desiredVel, persistentMaxSpeed);
    }

    // Call to stop pulling
    public void ClearPersistentExternalVelocity()
    {
        // do not snap, just set the target to zero; it will ease back using persistentAccel
        persistentTargetVel = Vector3.zero;
    }

    public void ProcessMove(Vector2 input)
    {
        if (isDashing) return;

        lastInput = input;

        Vector3 posBefore = transform.position;
        // Normal input movement (local space)
        Vector3 moveDirection = new Vector3(input.x, 0, input.y);
        moveDirection = transform.TransformDirection(moveDirection);

        // Apply slow via moveFactor
        float appliedSpeed = speed * moveFactor;
        controller.Move(moveDirection * appliedSpeed * Time.deltaTime);

        // Gravity
        playerVelocity.y += gravity * Time.deltaTime;
        if (isGrounded && playerVelocity.y < 0)
            playerVelocity.y = -2f;

        // Ease persistentVel toward the current target
        persistentVel = Vector3.MoveTowards(
            persistentVel,
            persistentTargetVel,
            persistentAccel * Time.deltaTime
        );

        // External recoil/pull velocity + vertical velocity
        Vector3 finalMove = playerVelocity + externalForce + persistentVel;
        controller.Move(finalMove * Time.deltaTime);

        // Decay external velocity smoothly; lower value = longer push
        externalForce = Vector3.Lerp(externalForce, Vector3.zero, 4f * Time.deltaTime);

        Vector3 posAfter = transform.position;
        Vector3 diff = posAfter - posBefore;
        diff.y = 0f;
        float horizontalDelta = diff.magnitude;

        FootstepTick(horizontalDelta);
    }

    public void Jump()
    {
        if (jumpCount < maxJumps && canJump)
        {
            playerVelocity.y = Mathf.Sqrt(jumpHeight * -3.0f * gravity);
            jumpCount++;
        }
    }

    public void Dash()
    {
        if (Time.time < lastDashTime + dashCooldown || isDashing)
            return;

        isDashing = true;
        // Get movement-based dash direction, or fall back to forward
        Vector3 inputDir = new Vector3(lastInput.x, 0f, lastInput.y);
        dashDirection = inputDir.sqrMagnitude > 0.01f
            ? transform.TransformDirection(inputDir.normalized)
            : transform.forward;

        dashTimer = dashDuration;
        lastDashTime = Time.time;

        TriggerDashVFX();
    }

    private void DashMovement()
    {
        if (dashTimer > 0f)
        {
            // Dash speed ignores slow by design; change to (dashSpeed * moveFactor) if you want slow to affect dash
            if (canDash)
            {
                controller.Move(dashDirection * dashSpeed * Time.deltaTime);
                dashTimer -= Time.deltaTime;
            }
        }
        else
        {
            isDashing = false;
        }
    }

    private void TriggerDashVFX()
    {
        // todo dash effect
    }

    // ===== New: Slow API =====
    /// <summary>
    /// Apply a movement slow. factor in [0..1], e.g. 0.6 = 40% slow.
    /// duration refreshes the slow timer; strongest slow wins.
    /// </summary>
    public void ApplySlow(float factor, float duration)
    {
        factor = Mathf.Clamp01(factor);
        // keep the stronger slow (smaller factor)
        moveFactor = Mathf.Min(moveFactor, factor);
        // extend the slow if this one lasts longer
        slowUntil = Mathf.Max(slowUntil, Time.time + Mathf.Max(0f, duration));
    }

    /// <summary>
    /// Clears any active slows immediately.
    /// </summary>
    public void ClearSlow()
    {
        moveFactor = 1f;
        slowUntil = 0f;
    }
    private void FootstepTick(float horizontalDelta)
    {
        // Only count distance while grounded
        if (!isGrounded)
        {
            _footstepAccum = 0f;
            _footstepCooldown = Mathf.Max(0f, _footstepCooldown - Time.deltaTime);
            return;
        }

        _footstepCooldown = Mathf.Max(0f, _footstepCooldown - Time.deltaTime);

        if (horizontalDelta > 0.001f)
            _footstepAccum += horizontalDelta;

        if (_footstepAccum >= stepDistance && _footstepCooldown <= 0f)
        {
            PlayRandomFootstep();
            _footstepAccum = 0f;
            _footstepCooldown = minStepInterval;
        }
    }

    private void PlayRandomFootstep()
    {
        if (footstepKeys == null || footstepKeys.Length == 0) return;

        string key = footstepKeys[Random.Range(0, footstepKeys.Length)];
        float pitch = Random.Range(stepPitchRange.x, stepPitchRange.y);

        // Play at the player's current position (local only; no networking here)
        SoundManager.PlaySfxAt(
            key,
            transform.position,
            stepVolume,
            pitch,
            stepSpatialBlend,
            stepMinDistance,
            stepMaxDistance
        );
    }
}


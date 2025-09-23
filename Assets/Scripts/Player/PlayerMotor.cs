using UnityEngine;

public class PlayerMotor : MonoBehaviour
{
    //handle all player movement controls: Moving, Jumping, Dashing
    private CharacterController controller;
    Vector3 playerVelocity;
    bool isGrounded;
    public float speed = 5f;
    public float gravity = -9.8f;
    public float jumpHeight = 1.5f;

    [Header("Dash Settings")]
    public float dashSpeed = 20f;
    public float dashDuration = 0.15f;
    public float dashCooldown = 1f;

    private bool isDashing = false;
    private float dashTimer = 0f;
    private float lastDashTime = -Mathf.Infinity;
    private Vector3 dashDirection;
    private Vector2 lastInput;

    [Header("Jump Settings")]
    public int maxJumps = 2;   // number of jumps allowed (2 = double jump)
    private int jumpCount = 0; // how many jumps have been used

    private Vector3 externalForce;
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
            jumpCount = 0; // to reset jump count
        }
        if (isDashing)
        {
            DashMovement();
        }
    }
    public void ApplyExternalForce(Vector3 force)
    {
        externalForce = force;
    }
    public void ProcessMove(Vector2 input)
    {
        if (isDashing) return;

        lastInput = input;

        Vector3 moveDirection = new Vector3(input.x, 0, input.y);
        moveDirection = transform.TransformDirection(moveDirection);

        // Player movement
        controller.Move(moveDirection * speed * Time.deltaTime);

        // Gravity
        playerVelocity.y += gravity * Time.deltaTime;
        if (isGrounded && playerVelocity.y < 0)
            playerVelocity.y = -2f;

        // Apply vertical + knockback
        Vector3 finalMove = playerVelocity + externalForce;
        controller.Move(finalMove * Time.deltaTime);

        // Decay knockback over time
        externalForce = Vector3.Lerp(externalForce, Vector3.zero, 5f * Time.deltaTime);
    }
    public void Jump()
    {
        /*if (isGrounded)
        {
            playerVelocity.y = Mathf.Sqrt(jumpHeight * -3.0f * gravity);
        }*/
        if (jumpCount < maxJumps)
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

        //todo
        TriggerDashVFX();
    }

    private void DashMovement()
    {
        if (dashTimer > 0f)
        {
            controller.Move(dashDirection * dashSpeed * Time.deltaTime);
            dashTimer -= Time.deltaTime;
        }
        else
        {
            isDashing = false;
        }
    }
    private void TriggerDashVFX()
    {
        //todo dash effect
    }

}

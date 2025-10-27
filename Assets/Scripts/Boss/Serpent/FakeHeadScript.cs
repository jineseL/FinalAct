using UnityEngine;

public class FakeHeadScript : MonoBehaviour
{
    [Header("Refs")]
    public Transform realHead;
    [Tooltip("The body segment directly behind the head (neck) used for clamp reference.")]
    public Transform previousSegment;

    [Header("Follow")]
    public bool copyPosition = true;
    public bool copyRotation = true;
    public bool followHead = true;

    [Header("Clamp")]
    [Tooltip("Limit how far the fake head's forward can deviate from the neck direction.")]
    [Range(0f, 180f)] public float maxBendAngleDeg = 60f;

    [Tooltip("Limit roll around the forward axis (relative to the neck's up).")]
    [Range(0f, 180f)] public float maxRollDeg = 45f;

    [Tooltip("How fast the fake head rotates toward the clamped result (deg/sec). 0 = snap.")]
    public float turnSpeedDeg = 720f;

    // ---------------- Hover/Face override (NEW) ----------------
    [Header("Hover / Face Override")]
    [Tooltip("If true, yaw (Y) is NOT copied from realHead. We yaw to face 'faceTarget' instead.")]
    public bool overrideYawToFaceTarget = false;

    [Tooltip("Target to face when overrideYawToFaceTarget is enabled.")]
    public Transform faceTarget;

    [Tooltip("Add a gentle pitch (X) bob while hovering (degrees).")]
    public float hoverBobAmplitudeDeg = 6f;

    [Tooltip("Bob speed in Hz (cycles per second). Set to 0 to disable bobbing.")]
    public float hoverBobFrequencyHz = 1.2f;

    [Tooltip("Optional phase offset for the bob (radians). Use this to sync with root hover.")]
    public float hoverBobPhase = 0f;

    public void EnableYawFaceOverride(Transform target, float bobAmplitudeDeg, float bobHz, float phase = 0f)
    {
        overrideYawToFaceTarget = true;
        faceTarget = target;
        hoverBobAmplitudeDeg = bobAmplitudeDeg;
        hoverBobFrequencyHz = bobHz;
        hoverBobPhase = phase;
    }

    public void DisableYawFaceOverride()
    {
        overrideYawToFaceTarget = false;
        faceTarget = null;
    }
    // -----------------------------------------------------------

    private void LateUpdate()
    {
        if (!realHead) return;

        // 1) Follow position only
        if (followHead && copyPosition)
            transform.position = realHead.position;

        // 2) Choose a base desired rotation
        Quaternion desiredRot =
            (followHead && copyRotation) ? realHead.rotation : transform.rotation;

        // 3) If we’re NOT overriding yaw, do your original clamped follow and return
        if (!overrideYawToFaceTarget || !faceTarget)
        {
            ApplyClamped(desiredRot);
            return;
        }

        // ---------------- Override: keep pitch & roll from real head, but compute yaw to face the target ----------------

        // YAW: face target (flattened to XZ so we keep world-up as yaw axis)
        Vector3 toTgt = faceTarget.position - transform.position;
        Vector3 toTgtXZ = Vector3.ProjectOnPlane(toTgt, Vector3.up);
        if (toTgtXZ.sqrMagnitude < 1e-6f) toTgtXZ = transform.forward; // fallback
        toTgtXZ.Normalize();

        Vector3 upRef = previousSegment ? previousSegment.up : Vector3.up;
        Quaternion yawOnly = Quaternion.LookRotation(toTgtXZ, upRef);   // yaw that looks at the player

        // PITCH from real head
        // pitch (+up/-down) measured from the real head's forward
        Vector3 fwd = realHead.forward;
        float pitchRad = Mathf.Atan2(fwd.y, new Vector2(fwd.x, fwd.z).magnitude);
        float pitchDeg = pitchRad * Mathf.Rad2Deg;

        // Add optional hover bob on pitch (small up/down tilt)
        if (hoverBobFrequencyHz > 0f && Mathf.Abs(hoverBobAmplitudeDeg) > 0.01f)
        {
            float bob = Mathf.Sin((Mathf.PI * 2f) * hoverBobFrequencyHz * Time.time + hoverBobPhase) * hoverBobAmplitudeDeg;
            pitchDeg += bob;
        }

        // Compose yaw + (local) pitch
        Quaternion yawPitch = yawOnly * Quaternion.AngleAxis(pitchDeg, Vector3.right);

        // ROLL from real head (twist around forward)
        // Measure real head's roll relative to neck up and apply it around the new forward.
        Vector3 realUp = realHead.up;
        float realRollDeg = SignedAngleAroundAxis(
            Vector3.ProjectOnPlane(upRef, realHead.forward).normalized,
            Vector3.ProjectOnPlane(realUp, realHead.forward).normalized,
            realHead.forward);

        Quaternion yawPitchRoll = yawPitch * Quaternion.AngleAxis(realRollDeg, Vector3.forward);

        // Now apply your normal clamp (relative to previous segment)
        ApplyClamped(yawPitchRoll);
    }

    private void ApplyClamped(Quaternion desiredRot)
    {
        // If we have no neck reference, just apply desired (with smoothing)
        if (!previousSegment)
        {
            ApplyRotation(desiredRot);
            return;
        }

        // 1) Clamp bend: keep forward near the neck->head direction
        Vector3 parentPos = previousSegment.position;
        Vector3 refDir = (transform.position - parentPos);
        if (refDir.sqrMagnitude < 1e-6f) refDir = previousSegment.forward; // fallback
        refDir.Normalize();

        Vector3 desiredFwd = (desiredRot * Vector3.forward).normalized;

        float maxRad = Mathf.Deg2Rad * Mathf.Clamp(maxBendAngleDeg, 0f, 179f);
        Vector3 clampedFwd = Vector3.RotateTowards(refDir, desiredFwd, maxRad, 0f);

        // 2) Clamp roll: keep up close to neck's up (projected onto the plane of the clamped forward)
        Vector3 upRef = previousSegment.up;
        Vector3 upRefProj = Vector3.ProjectOnPlane(upRef, clampedFwd);
        if (upRefProj.sqrMagnitude < 1e-6f)
            upRefProj = Vector3.ProjectOnPlane(Vector3.up, clampedFwd);
        upRefProj.Normalize();

        Vector3 desiredUpProj = Vector3.ProjectOnPlane(desiredRot * Vector3.up, clampedFwd).normalized;

        float signedRoll = SignedAngleAroundAxis(upRefProj, desiredUpProj, clampedFwd);
        float limitedRoll = Mathf.Clamp(signedRoll, -maxRollDeg, maxRollDeg);
        Quaternion rollOffset = Quaternion.AngleAxis(limitedRoll, clampedFwd);
        Vector3 finalUp = (rollOffset * upRefProj).normalized;

        Quaternion finalRot = Quaternion.LookRotation(clampedFwd, finalUp);

        // 3) Apply with smoothing
        ApplyRotation(finalRot);
    }

    private void ApplyRotation(Quaternion targetRot)
    {
        if (turnSpeedDeg <= 0f)
            transform.rotation = targetRot;
        else
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeedDeg * Time.deltaTime);
    }

    private static float SignedAngleAroundAxis(Vector3 from, Vector3 to, Vector3 axis)
    {
        Vector3 cross = Vector3.Cross(from, to);
        float angle = Mathf.Atan2(Vector3.Dot(cross, axis), Vector3.Dot(from, to)) * Mathf.Rad2Deg;
        return angle;
    }
}

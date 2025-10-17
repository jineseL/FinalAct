using UnityEngine;

public class FakeHeadScript : MonoBehaviour
{
    public Transform realHead;
    [Tooltip("The body segment directly behind the head (neck) used for clamp reference.")]
    public Transform previousSegment;
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
    private void LateUpdate()
    {
        if (!realHead) return;

        // 1) Follow position only
        if (followHead && copyPosition)
            transform.position = realHead.position;

        // 2) Decide the desired rotation for this frame
        Quaternion desiredRot = copyRotation ? realHead.rotation : transform.rotation;

        // 3) If we have no neck reference, just apply desired (with optional smoothing)
        if (!previousSegment)
        {
            ApplyRotation(desiredRot);
            return;
        }

        // 4) Clamp bend relative to the direction from neck->head
        Vector3 parentPos = previousSegment.position;
        Vector3 refDir = (transform.position - parentPos);
        if (refDir.sqrMagnitude < 1e-6f) refDir = previousSegment.forward; // fallback
        refDir.Normalize();

        Vector3 desiredFwd = (desiredRot * Vector3.forward).normalized;

        float maxRad = Mathf.Deg2Rad * Mathf.Clamp(maxBendAngleDeg, 0f, 179f);
        Vector3 clampedFwd = Vector3.RotateTowards(refDir, desiredFwd, maxRad, 0f);

        // 5) Clamp roll: keep up close to neck's up (projected onto plane of clampedFwd)
        Vector3 upRef = previousSegment.up;
        Vector3 upRefProj = Vector3.ProjectOnPlane(upRef, clampedFwd);
        if (upRefProj.sqrMagnitude < 1e-6f)
            upRefProj = Vector3.ProjectOnPlane(Vector3.up, clampedFwd); // last resort
        upRefProj.Normalize();

        Vector3 desiredUpProj = Vector3.ProjectOnPlane(desiredRot * Vector3.up, clampedFwd).normalized;

        // Signed roll angle from upRefProj to desiredUpProj around the clamped forward axis
        float signedRoll = SignedAngleAroundAxis(upRefProj, desiredUpProj, clampedFwd);
        float limitedRoll = Mathf.Clamp(signedRoll, -maxRollDeg, maxRollDeg);

        Quaternion rollOffset = Quaternion.AngleAxis(limitedRoll, clampedFwd);
        Vector3 finalUp = (rollOffset * upRefProj).normalized;

        Quaternion finalRot = Quaternion.LookRotation(clampedFwd, finalUp);

        // 6) Apply with smoothing
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
        // Signed angle between 'from' and 'to' around 'axis'
        Vector3 cross = Vector3.Cross(from, to);
        float angle = Mathf.Atan2(Vector3.Dot(cross, axis), Vector3.Dot(from, to)) * Mathf.Rad2Deg;
        return angle;
    }
}


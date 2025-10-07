using UnityEngine;
using System.Collections.Generic;
using System;
/// Simple, fast chain solver for snake/segmented creatures.
/// Head is driven externally (AI). Each segment is constrained to:
/// 1) Stay on a sphere of radius 'linkLength' around its parent,
/// 2) Not exceed 'maxBendAngleDeg' relative to the parent's direction.
[ExecuteAlways]
public class ChainSnakeSolver : MonoBehaviour
{
    [Header("Chain")]
    public Transform head;                    // Driven by your AI/movement
    public Transform[] segments;              // array of segments excluding head
    public bool autoBuildFromChildren = true;
    public bool autoLengthsFromInitial = true;

    public bool perLinkLengths = false; //true = each link has its own radius value according to constraintLength, false = use same value for all
    //for this prototype keep false;

    [SerializeField] private List<float> constraintLength = new List<float>();//pre set length for each point radius

    [Min(0.001f)] public float defaultLinkLength = 1f; //length from 1 link to another/ the constraint redius

    [Header("Bend Constraint")]
    [Range(0f, 179f)] public float maxBendAngleDeg = 45f;//max bend per joint

    [Header("Smoothing")]
    [Tooltip("Extra pass count to stabilize and stiffen. 0 or 1 is often enough.")]
    [Range(0, 4)] public int solverIterations = 1; //extra passes to stiffen/stabilize the chain
    [Tooltip("Position smoothing toward the solved target for each segment.")]
    [Range(0f, 1f)] public float followLerp = 0.4f; //how strongly each segment moves toward its constraint target (smoothing).

    [Header("Orientation")]
    public bool alignRotation = true;
    public Vector3 upHint = Vector3.up;

    [Header("Gizmos")]
    public bool drawSpheres = true;
    public Color sphereColor = new Color(0f, 1f, 1f, 0.15f);
    public bool drawLinks = true;
    public Color linkColor = new Color(0f, 0.8f, 1f, 0.8f);

    // Runtime
    private float[] linkLengths; // desired distance from parent to segment i (sphere radius)
    private Vector3 _lastHeadPos;//cached last-frame head position (to infer motion direction).
    private bool _initd = false;

    private void OnEnable()
    {
        InitIfNeeded();
    }

    /*private void Reset()
    {
        autoBuildFromChildren = true;
        autoLengthsFromInitial = true;
        perLinkLengths = true;
        defaultLinkLength = 1f;
        maxBendAngleDeg = 45f;
        solverIterations = 1;
        followLerp = 0.4f;
        alignRotation = true;
        upHint = Vector3.up;
    }*/

    private void OnValidate()
    {
        if (Application.isPlaying) return;
        InitIfNeeded();
        if (autoLengthsFromInitial)
            MeasureLinkLengths();
    }

    private void InitIfNeeded()
    {
        if (autoBuildFromChildren)
        {
            BuildSegmentsFromChildren();
        }

        if (segments == null) return;

        if (linkLengths == null || linkLengths.Length != (segments?.Length ?? 0)) linkLengths = new float[segments.Length];

        if (!_initd || autoLengthsFromInitial)
            MeasureLinkLengths();

        if (head == null && transform.childCount > 0)
            head = transform.GetChild(0);

        _initd = true;
        _lastHeadPos = head ? head.position : transform.position;
    }

    private void BuildSegmentsFromChildren()
    {
        // Build from children EXCLUDING head if head is a child of this object.
        // If head is not a child, we just take all children as segments.
        List<Transform> list = new List<Transform>();
        for (int i = 0; i < transform.childCount; i++)
        {
            var c = transform.GetChild(i);
            if (c == head) continue;
            list.Add(c);
        }
        segments = list.ToArray();
    }

    /// <summary>
    /// create length of in between each segment
    /// </summary>
    private void MeasureLinkLengths()
    {
        if (segments == null || head == null) return;

        if (perLinkLengths)
        {
            // link 0: from head to segments[0], etc…
            if (linkLengths.Length == constraintLength.Count)
            {
                for (int i = 0; i < segments.Length; i++)
                {
                    /*Transform a = (i == 0) ? head : segments[i - 1];
                    Transform b = segments[i];
                    float d = (a && b) ? Vector3.Distance(a.position, b.position) : defaultLinkLength;
                    linkLengths[i] = Mathf.Max(0.001f, d);*/
                    linkLengths[i] = constraintLength[i];
                }
            }
        }
        else
        {
            for (int i = 0; i < segments.Length; i++)
                linkLengths[i] = Mathf.Max(0.001f, defaultLinkLength);
        }
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying)
        {
            // In edit mode, still run to preview chains
            SolveChain(Time.deltaTime);
        }
    }

    private void Update()
    {
        if (Application.isPlaying)
        {
            SolveChain(Time.deltaTime);
        }
    }

    private void SolveChain(float dt)
    {
        //InitIfNeeded();
        if (head == null || segments == null || segments.Length == 0) return;

        // Use head "direction" hint from motion if available
        Vector3 headDir = -head.forward;
        //Vector3 headVel = (head.position - _lastHeadPos) / Mathf.Max(0.0001f, dt);
        //if (headVel.sqrMagnitude > 0.0001f) headDir = headVel.normalized;
        if (headDir.sqrMagnitude < 0.001f) headDir = transform.forward;
        /*Choose an initial “parent direction” for the first joint:

        Start with -head.forward so the chain tends to trail behind the head’s facing.

        If the head is actually moving, use its velocity direction instead (more natural).

        If that’s degenerate, fall back to the root’s forward.
         */

        for (int it = 0; it < Mathf.Max(1, solverIterations); it++)
        {
            Vector3 prevPos = head.position;
            Vector3 prevDir = headDir;

            for (int i = 0; i < segments.Length; i++)
            {
                Transform seg = segments[i];
                if (!seg) continue;

                float L = linkLengths[i];

                // Current raw direction from parent to this segment
                Vector3 rawDir = (seg.position - prevPos);
                if (rawDir.sqrMagnitude < 1e-6f) rawDir = prevDir; // fallback
                rawDir.Normalize();

                // Clamp bend angle
                float maxRad = maxBendAngleDeg * Mathf.Deg2Rad;
                Vector3 clampedDir;
                if (prevDir.sqrMagnitude < 1e-6f)
                {
                    clampedDir = rawDir;
                }
                else
                {
                    // If rawDir deviates more than max, clamp towards prevDir
                    clampedDir = Vector3.RotateTowards(prevDir, rawDir, maxRad, 0f);
                }

                // Enforce constant length (sphere around parent => on circumference)
                Vector3 targetPos = prevPos + clampedDir * L;

                // Smooth toward target to reduce jitter
                seg.position = Vector3.Lerp(seg.position, targetPos, followLerp);

                if (alignRotation)
                {
                    // Orient along the chain direction
                    Vector3 up = upHint.sqrMagnitude > 0.001f ? upHint : Vector3.up;
                    seg.rotation = Quaternion.LookRotation(-clampedDir, up);
                }

                // Next parent for the next link
                prevPos = seg.position;
                prevDir = clampedDir;
            }
        }

        _lastHeadPos = head.position;
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawSpheres || segments == null || head == null) return;

        Gizmos.color = sphereColor;
        Vector3 prevPos = head.position;
        for (int i = 0; i < segments.Length; i++)
        {
            float L = (linkLengths != null && i < linkLengths.Length) ? linkLengths[i] : defaultLinkLength;
            Gizmos.DrawWireSphere(prevPos, L);
            if (drawLinks && segments[i])
            {
                Gizmos.color = linkColor;
                Gizmos.DrawLine(prevPos, segments[i].position);
                Gizmos.color = sphereColor;
            }
            prevPos = segments[i] ? segments[i].position : prevPos;
        }
    }
}

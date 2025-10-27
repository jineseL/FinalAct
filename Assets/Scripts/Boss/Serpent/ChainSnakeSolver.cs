using UnityEngine;
using System.Collections.Generic;
using System;

/// Simple, fast chain solver for snake/segmented creatures.
[ExecuteAlways]
public class ChainSnakeSolver : MonoBehaviour
{
    [Header("Chain")]
    public Transform head;                    // Driven by your AI/movement
    public Transform[] segments;              // array of segments excluding head
    public bool autoBuildFromChildren = true;
    public bool autoLengthsFromInitial = true;

    public bool perLinkLengths = false;
    [SerializeField] private List<float> constraintLength = new List<float>();
    [Min(0.001f)] public float defaultLinkLength = 1f;

    [Header("Bend Constraint")]
    [Range(0f, 179f)] public float maxBendAngleDeg = 45f;

    [Header("Smoothing")]
    [Tooltip("Extra pass count to stabilize and stiffen. 0 or 1 is often enough.")]
    [Range(0, 4)] public int solverIterations = 1;
    [Tooltip("Position smoothing toward the solved target for each segment.")]
    [Range(0f, 1f)] public float followLerp = 0.4f;

    [Header("Orientation")]
    public bool alignRotation = true;
    public Vector3 upHint = Vector3.up;

    [Header("Gizmos")]
    public bool drawSpheres = true;
    public Color sphereColor = new Color(0f, 1f, 1f, 0.15f);
    public bool drawLinks = true;
    public Color linkColor = new Color(0f, 0.8f, 1f, 0.8f);

    // ========================== Idle Wiggle (optional) ==========================
    [Header("Idle Wiggle (optional)")]
    [Tooltip("Enable a sinusoidal side-to-side body wave (head stays driven by controller).")]
    public bool idleWiggleEnabled = false;

    [Tooltip("Max angular deflection per link (degrees). Tail is scaled by Tail Multiplier.")]
    public float idleWiggleAmplitudeDeg = 12f;

    [Tooltip("Oscillation frequency (Hz).")]
    public float idleWiggleFrequency = 0.9f;

    [Tooltip("How much phase offset between links (degrees per segment).")]
    public float idleWigglePhasePerLinkDeg = 20f;

    [Tooltip("Amplify wiggle toward the tail. 1 = no taper, >1 = stronger tail.")]
    public float idleWiggleTailMultiplier = 1.4f;

    [Tooltip("Axis for wiggle rotation. If zero vector, uses Vector3.up.")]
    public Vector3 idleWiggleUpAxis = Vector3.up;

    [Space(6)]
    [Tooltip("When wiggle turns on, briefly pull all links straight behind the head first.")]
    public bool wiggleStraightenOnEnable = true;

    [Tooltip("Seconds to keep straightening active after enabling wiggle.")]
    [Min(0f)] public float wiggleStraightenTime = 0.25f;

    [Tooltip("Seconds to ramp wiggle amplitude 0 -> target when enabled (and back to 0 when disabled).")]
    [Min(0f)] public float wiggleRampTime = 0.35f;

    [Tooltip("Exponent >1 means the tail straightens more than the front.")]
    [Min(0f)] public float wiggleStraightenTailPower = 1.5f;

    [Tooltip("Randomize the starting wiggle phase when enabling to avoid sameness.")]
    public bool randomizePhaseOnEnable = true;
    [Header("Tail Stiffness After Straighten")]
    [Tooltip("Persistent straightening at the tail after the initial straighten window (only while idle wiggle is on). 0 = off.")]
    [Range(0f, 1f)] public float postStraightenTailBias = 0.18f;

    [Tooltip("Concentrates the post-straighten bias toward the tail. >1 = mostly tail, 1 = linear.")]
    [Min(0f)] public float postStraightenTailPower = 1.8f;

    [Tooltip("Seconds to crossfade from the strong idle-start straighten into the softer post tail bias.")]
    [Min(0f)] public float postStraightenLerpTime = 0.5f;

    // Inspector
    [Header("Straighten Triggers")]
    [Tooltip("Skip the straighten-on-enable the very first time wiggle turns on (e.g., on spawn).")]
    public bool skipStraightenOnFirstEnable = true;

    // Runtime
    private bool _hasEverEnabledWiggle = false;  // tracks first runtime enable
    private bool _straightenArmed = false;       // only true when a real straighten window is scheduled
    // Runtime
    private float[] linkLengths;
    private Vector3 _lastHeadPos;
    private bool _initd = false;
    private float _wiggleTime = 0f;
    private float _wiggleStraightenStart = 0f;   // when the lerp started
    private float _phaseOffsetRad = 0f;          // random phase so it looks different each time

    // New runtime controls
    private float _wiggleAmpScale = 0f;          // 0..1 amplitude envelope
    private float _wiggleStraightenUntil = 0f;   // world time until which we force straightening
    private bool _prevWiggleEnabled = false;

    // ===== Public API for controller =====
    /// <summary>Enable/disable body wiggle and set amplitude/frequency.</summary>
    public void SetIdleWiggle(bool on, float amplitudeDeg, float frequencyHz)
    {
        bool turningOn = on && !_prevWiggleEnabled;
        _prevWiggleEnabled = on;

        idleWiggleEnabled = on;
        idleWiggleAmplitudeDeg = Mathf.Max(0f, amplitudeDeg);
        idleWiggleFrequency = Mathf.Max(0f, frequencyHz);

        if (turningOn)
        {
            _wiggleTime = 0f;
            _wiggleAmpScale = 0f; // ramp up in SolveChain

            bool shouldSkipThisEnable = skipStraightenOnFirstEnable && !_hasEverEnabledWiggle;
            bool shouldStraightenNow = wiggleStraightenOnEnable && wiggleStraightenTime > 0f && !shouldSkipThisEnable;

            if (shouldStraightenNow)
            {
                // Arm a real straighten window
                _wiggleStraightenUntil = Time.time + wiggleStraightenTime;
                _straightenArmed = true;
            }
            else
            {
                // No window armed; also prevents post-bias from activating
                _straightenArmed = false;
                _wiggleStraightenUntil = -1f;
            }
        }
        else if (!on)
        {
            // Leaving idle — disarm any pending window/post bias
            _straightenArmed = false;
        }

        _hasEverEnabledWiggle = _hasEverEnabledWiggle || turningOn;
    }


    /// <summary>Alias for compatibility.</summary>
    public void EnableIdleWiggle(bool on, float amplitudeDeg, float frequencyHz)
        => SetIdleWiggle(on, amplitudeDeg, frequencyHz);

    private void OnEnable() { InitIfNeeded(); }

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
            BuildSegmentsFromChildren();

        if (segments == null) return;

        if (linkLengths == null || linkLengths.Length != (segments?.Length ?? 0))
            linkLengths = new float[segments.Length];

        if (!_initd || autoLengthsFromInitial)
            MeasureLinkLengths();

        if (head == null && transform.childCount > 0)
            head = transform.GetChild(0);

        _initd = true;
        _lastHeadPos = head ? head.position : transform.position;

        // Reset wiggle runtime state
        _wiggleTime = 0f;
        _wiggleAmpScale = idleWiggleEnabled ? 1f : 0f;
        _prevWiggleEnabled = idleWiggleEnabled;
        _wiggleStraightenUntil = 0f;
        _hasEverEnabledWiggle = false;
        _straightenArmed = false;
        _wiggleStraightenUntil = -1f; // sentinel: no window armed
    }

    private void BuildSegmentsFromChildren()
    {
        List<Transform> list = new List<Transform>();
        for (int i = 0; i < transform.childCount; i++)
        {
            var c = transform.GetChild(i);
            if (c == head) continue;
            list.Add(c);
        }
        segments = list.ToArray();
    }

    private void MeasureLinkLengths()
    {
        if (segments == null || head == null) return;

        if (perLinkLengths)
        {
            if (linkLengths.Length == constraintLength.Count)
            {
                for (int i = 0; i < segments.Length; i++)
                    linkLengths[i] = Mathf.Max(0.001f, constraintLength[i]);
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
            SolveChain(Time.deltaTime);
    }

    private void Update()
    {
        if (Application.isPlaying)
            SolveChain(Time.deltaTime);
    }

    private void SolveChain(float dt)
    {
        if (head == null || segments == null || segments.Length == 0) return;

        // === Wiggle clock and amplitude ramp ===
        _wiggleTime += Mathf.Max(0f, dt);

        if (idleWiggleEnabled)
        {
            float step = (wiggleRampTime <= 0f) ? 1f : dt / Mathf.Max(0.0001f, wiggleRampTime);
            _wiggleAmpScale = Mathf.Clamp01(_wiggleAmpScale + step);
        }
        else
        {
            float step = (wiggleRampTime <= 0f) ? 1f : dt / Mathf.Max(0.0001f, wiggleRampTime);
            _wiggleAmpScale = Mathf.Clamp01(_wiggleAmpScale - step);
        }

        // Base direction the chain wants to extend along: opposite the head forward
        Vector3 headDir = -head.forward;
        if (headDir.sqrMagnitude < 0.001f) headDir = transform.forward;

        int n = segments.Length;

        // Stable phase offset so each enable looks a bit different.
        float phaseOffsetRad = 0f;
        if (wiggleStraightenOnEnable && wiggleStraightenTime > 0f)
        {
            // Hash the "until" timestamp into [0, 2PI)
            float s = Mathf.Sin(_wiggleStraightenUntil * 12.9898f) * 43758.5453f;
            float frac = s - Mathf.Floor(s);
            phaseOffsetRad = frac * (Mathf.PI * 2f);
        }

        float basePhase = phaseOffsetRad + _wiggleTime * Mathf.PI * 2f * Mathf.Max(0f, idleWiggleFrequency);
        float perLinkPhaseRad = idleWigglePhasePerLinkDeg * Mathf.Deg2Rad;
        float ampDeg = Mathf.Max(0f, idleWiggleAmplitudeDeg) * _wiggleAmpScale; // ramped
        Vector3 wiggleAxis = (idleWiggleUpAxis.sqrMagnitude > 1e-6f ? idleWiggleUpAxis.normalized : Vector3.up);

        // --- Windowed straighten (only while idle just began) ---
        bool inStraightenWindow = _straightenArmed&& wiggleStraightenOnEnable&& wiggleStraightenTime > 0f&& Time.time < _wiggleStraightenUntil;

        float straightenBlendGlobal = 0f;
        if (inStraightenWindow)
        {
            float start = _wiggleStraightenUntil - wiggleStraightenTime;
            float u = Mathf.InverseLerp(start, _wiggleStraightenUntil, Time.time); // 0..1 inside the window
            straightenBlendGlobal = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u));    // ease-in
        }

        // --- Post window: gently bias the tail, but crossfade to it over time (no snap) ---
        bool idleActive = idleWiggleEnabled || _wiggleAmpScale > 0f;
        bool afterWindow = _straightenArmed && idleActive && !inStraightenWindow;

        // Ramp 0->1 after the window finishes, over postStraightenLerpTime
        float postPhaseT = 0f;
        if (afterWindow)
        {
            if (postStraightenLerpTime <= 0f) postPhaseT = 1f;
            else
            {
                float t = (Time.time - _wiggleStraightenUntil) / Mathf.Max(0.0001f, postStraightenLerpTime);
                postPhaseT = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            }
        }

        // Target post bias values (user knobs)
        float postBiasTarget = Mathf.Clamp01(postStraightenTailBias);
        float postPowerTarget = Mathf.Max(0f, postStraightenTailPower);

        // Crossfade power from 1 (linear along the body) to the user's tail-focused power
        float effectivePostPower = Mathf.Lerp(1f, postPowerTarget, postPhaseT);

        // Strength of the post bias also grows from 0 to the user's target
        float postBiasStrength = postBiasTarget * postPhaseT;

        const float windowTailPower = 1.5f; // 1.2–2.0 feels nice for the initial straighten

        for (int it = 0; it < Mathf.Max(1, solverIterations); it++)
        {
            Vector3 prevPos = head.position;
            Vector3 prevDir = headDir;

            for (int i = 0; i < n; i++)
            {
                Transform seg = segments[i];
                if (!seg) continue;

                float L = linkLengths[i];

                // Direction from parent to this segment (raw desire)
                Vector3 rawDir = (seg.position - prevPos);
                if (rawDir.sqrMagnitude < 1e-6f) rawDir = prevDir;
                rawDir.Normalize();

                // Constrain how sharply we can bend away from the parent direction
                float maxRad = maxBendAngleDeg * Mathf.Deg2Rad;
                Vector3 constrained = (prevDir.sqrMagnitude < 1e-6f)
                    ? rawDir
                    : Vector3.RotateTowards(prevDir, rawDir, maxRad, 0f);

                // Tail weighting 0..1 (head->tail)
                float tailT = (n > 1) ? ((i + 1) / (float)n) : 1f;

                // What the window strength would be at the *end* of the window for this link
                float windowEndBlend = Mathf.Pow(tailT, windowTailPower);

                // Active window blend (ramps in from 0..1 only while in window)
                float windowBlendNow = straightenBlendGlobal * windowEndBlend;

                // Post blend grows in after the window, both in strength and tail focus
                float postBlendNow = postBiasStrength * Mathf.Pow(tailT, effectivePostPower);

                // Final blend per link:
                // - If in the window: use the window blend.
                // - After the window: crossfade smoothly from the window's final strength to the post blend.
                float blend;
                if (inStraightenWindow)
                    blend = windowBlendNow;
                else if (afterWindow)
                    blend = Mathf.Lerp(windowEndBlend, postBlendNow, postPhaseT);
                else
                    blend = 0f; // roaming/attacking or wiggle off

                Vector3 baseDir = (blend > 0f)
                    ? Vector3.Slerp(constrained, prevDir, Mathf.Clamp01(blend))
                    : constrained;

                // Overlay the idle wiggle (kept alive during the straighten/post blends)
                if (ampDeg > 0f)
                {
                    float ampThisDeg = Mathf.Lerp(ampDeg, ampDeg * Mathf.Max(1f, idleWiggleTailMultiplier), tailT);
                    float phase = basePhase + perLinkPhaseRad * i;
                    float angDeg = ampThisDeg * Mathf.Sin(phase);
                    baseDir = Quaternion.AngleAxis(angDeg, wiggleAxis) * baseDir;
                }

                // Constant length
                Vector3 targetPos = prevPos + baseDir * L;

                // Smooth toward target
                seg.position = Vector3.Lerp(seg.position, targetPos, followLerp);

                if (alignRotation)
                {
                    Vector3 up = upHint.sqrMagnitude > 0.001f ? upHint : Vector3.up;
                    seg.rotation = Quaternion.LookRotation(-baseDir, up);
                }

                prevPos = seg.position;
                prevDir = baseDir;
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

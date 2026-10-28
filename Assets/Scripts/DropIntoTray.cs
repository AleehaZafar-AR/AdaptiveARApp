using UnityEngine;

/// <summary>
/// Physics behaviour for a stageable assembly part.
///
/// Originally this released every part to gravity the instant it was enabled, which
/// meant all 74 parts free-fell from their authored poses the moment EngineAnchor
/// was activated. Combined with Discrete collision detection, thin non-convex tray
/// shells and no floor underneath, parts tunnelled through the trays and fell
/// forever out of reach.
///
/// Three changes address that, all of them configurable rather than hard-coded:
///   1. Parts stay put by default instead of being dropped (releaseToGravityOnEnable).
///   2. Continuous collision detection, so a fast-moving small part cannot tunnel.
///   3. Out-of-bounds recovery, so a part that escapes is returned to its authored
///      pose instead of ending the bench session.
///
/// Physics feel
/// ------------
/// The first version of (2) used ContinuousSpeculative and forced Interpolate on
/// every part. On the headset that read as slow motion: speculative contacts make a
/// small part decelerate before it touches anything, the project's 1 cm contact
/// offset is a large cushion for a 2 cm pin, and interpolation makes a kinematic
/// part moved by the hand lag a physics step behind it. The Tuned profile below uses
/// sweep-based CCD (tunnelling is still prevented), no interpolation, and a small
/// contact offset. It applies to every existing part without touching the scene;
/// choose PerObject to go back to this object's own fields.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DropIntoTray : MonoBehaviour
{
    public enum PhysicsProfile
    {
        /// <summary>Sweep CCD, no interpolation, small contact offset. Normal-feeling parts.</summary>
        Tuned = 0,

        /// <summary>Use the collisionDetection / interpolation fields on this object as authored.</summary>
        PerObject = 1
    }

    [Header("Spawn Behaviour")]
    [Tooltip("Original behaviour: release to gravity as soon as this part is enabled. " +
             "Left OFF, the part stays exactly where it was authored and never free-falls.")]
    [SerializeField] private bool releaseToGravityOnEnable = false;

    [Header("Physics")]
    [Tooltip("Tuned: sweep CCD, no interpolation, small contact offset (recommended). " +
             "PerObject: the two fields below are used as authored on this object.")]
    [SerializeField] private PhysicsProfile physicsProfile = PhysicsProfile.Tuned;

    [Tooltip("Contact offset for this part's colliders under the Tuned profile, metres. The " +
             "project default is 1 cm, which floats small parts visibly above surfaces.")]
    [SerializeField] private float tunedContactOffset = 0.003f;

    [Tooltip("How fast overlapping geometry is pushed apart under the Tuned profile, m/s.")]
    [SerializeField] private float tunedMaxDepenetrationVelocity = 1f;

    [Header("Collision (PerObject profile)")]
    [Tooltip("Speculative continuous detection is valid for both kinematic and dynamic " +
             "bodies, which matters because hand-held parts are moved kinematically and fast.")]
    [SerializeField] private CollisionDetectionMode collisionDetection = CollisionDetectionMode.ContinuousSpeculative;

    [Tooltip("Interpolation smooths the visual pose of a moving part between physics steps.")]
    [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.Interpolate;

    [Header("Out-of-Bounds Recovery")]
    [Tooltip("Return this part to its authored pose if it falls away, instead of letting it fall forever.")]
    [SerializeField] private bool recoverIfLost = true;

    [Tooltip("Metres this part may drop below where it started before it counts as lost. " +
             "This is the real failure: falling through the table. Carrying a part sideways " +
             "to the engine never trips it.")]
    [SerializeField] private float maxFallBelowStart = 0.5f;

    [Tooltip("Generous catch-all distance in metres, for a part thrown right out of the work area. " +
             "Keep this well beyond the reach of normal assembly moves.")]
    [SerializeField] private float maxDistanceFromStart = 3f;

    [Tooltip("After this many recoveries the part is frozen instead of released again, so a gap " +
             "in the colliders cannot cause an endless fall-and-respawn loop.")]
    [SerializeField] private int maxRecoveriesBeforeHolding = 3;

    [Header("Resting")]
    [Tooltip("Seconds below the motion thresholds before a loose part is put to rest: sweep CCD off, body asleep.")]
    [SerializeField] private float restAfterSeconds = 0.5f;

    [Tooltip("A resting (sleeping) part is woken while any grab interactor's body is within this distance, metres, " +
             "so the Interaction SDK's trigger registry sees it. Tracked hands wake parts with their physics capsules; " +
             "controller-driven hands have none.")]
    [SerializeField] private float wakeRadius = 0.15f;

    [Header("Debug")]
    [SerializeField] private bool logRecovery = true;

    private Rigidbody rb;

    private bool _wasMoving;
    private float _stillSince = -1f;
    private bool _resting;
    private bool _wokeLogged;
    private int _resolveCount;
    private Renderer _renderer;

    // ---- solid surfaces (trays, oil pan, work surface, desk plane): static or engine-kinematic colliders ----
    private static readonly System.Collections.Generic.List<Collider> _solid = new System.Collections.Generic.List<Collider>();
    private static float _solidScannedAt = -999f;

    /// <summary>Enabled, non-trigger colliders that are not loose parts: trays, oil pan, work surface, floor plane, engine body.</summary>
    public static System.Collections.Generic.List<Collider> SolidColliders()
    {
        if (Time.time - _solidScannedAt > 5f)
        {
            _solidScannedAt = Time.time;
            _solid.Clear();
            foreach (Collider c in FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Rigidbody arb = c.attachedRigidbody;
                if (arb != null && (!arb.isKinematic || arb.GetComponentInParent<DropIntoTray>() != null)) continue;   // loose / placed parts
                if (c.GetComponentInParent<Oculus.Interaction.HandGrab.HandGrabInteractor>() != null) continue;      // hands
                _solid.Add(c);
            }
        }
        return _solid;
    }

    /// <summary>Deepest penetration of this part's colliders into any solid surface (metres), with the surface and the push-out direction.</summary>
    public bool DeepestPenetration(out Collider surface, out Vector3 direction, out float depth, out bool supported)
    {
        surface = null; direction = Vector3.up; depth = 0f; supported = false;
        foreach (Collider mine in GetComponents<Collider>())
        {
            if (mine == null || !mine.enabled || mine.isTrigger) continue;
            foreach (Collider other in SolidColliders())
            {
                if (other == null || !mine.bounds.Intersects(other.bounds)) continue;
                try
                {
                    if (Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation, other, other.transform.position, other.transform.rotation, out Vector3 dir, out float dist))
                    {
                        supported = true;
                        if (dist > depth) { depth = dist; direction = dir; surface = other; }
                    }
                }
                catch (System.Exception) { }
            }
        }
        return depth > 0f;
    }

    private Vector3 authoredLocalPosition;
    private Quaternion authoredLocalRotation;
    private Transform authoredParent;
    private bool authoredPoseCaptured;
    private int recoveryCount;

    /// <summary>Collision mode this part actually runs with.</summary>
    public CollisionDetectionMode EffectiveCollisionDetection
    {
        get { return physicsProfile == PhysicsProfile.Tuned ? CollisionDetectionMode.ContinuousDynamic : collisionDetection; }
    }

    /// <summary>Interpolation this part actually runs with.</summary>
    public RigidbodyInterpolation EffectiveInterpolation
    {
        get { return physicsProfile == PhysicsProfile.Tuned ? RigidbodyInterpolation.None : interpolation; }
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        CaptureAuthoredPose();
        ApplyPhysicsProfile();

        rb.isKinematic = true; // no physics until explicitly released
    }

    void OnEnable()
    {
        // A placed (locked) part that is re-activated must never be dropped back to gravity.
        var padlock = GetComponent<AdaptiveAR.Steps.PlacementLock>();
        if (padlock != null && padlock.IsLocked) { Hold(); return; }
        if (releaseToGravityOnEnable)
            ReleaseToPhysics();
        else
            Hold();
    }

    void OnDisable()
    {
        // When hidden again, freeze it
        Hold();
    }

    void FixedUpdate()
    {
        // While a hand is holding the part the Interaction SDK drives it kinematically,
        // so this check is naturally skipped during a grab.
        if (!recoverIfLost || rb == null || rb.isKinematic || !authoredPoseCaptured)
            return;

        // Hand/controller parity: when a loose part comes to rest after moving (dropped,
        // fallen into a tray), re-assert both grab paths - the SDK's trigger broadcasters can
        // be left disabled after a toggle, which makes the part grabbable by one input but
        // not the other.
        bool moving = rb.linearVelocity.sqrMagnitude > 0.0004f || rb.angularVelocity.sqrMagnitude > 0.01f;
        if (moving)
        {
            _wasMoving = true; _stillSince = -1f;
            if (_resting)
            {
                // Back in motion (grabbed / knocked): full sweep collision again.
                _resting = false; _wokeLogged = false;
                rb.collisionDetectionMode = EffectiveCollisionDetection;
            }
            if (rb.linearVelocity.sqrMagnitude > 0.01f) _resolveCount = 0;       // a real move (carried): fresh budget
        }
        else if (_wasMoving)
        {
            if (_stillSince < 0f) _stillSince = Time.time;
            else if (Time.time - _stillSince > restAfterSeconds)
            {
                // At rest: no sweep CCD (it keeps re-solving resting contacts), let the body sleep.
                _wasMoving = false;
                _resting = true;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.Sleep();
                if (logRecovery) Debug.Log($"[TrayRest] '{name}' at rest: sweep collision off, body asleep (wakes normally on grab or when a hand is within {wakeRadius * 100f:F0} cm).");
                AuditRestingGeometry();
                AdaptiveAR.Steps.GrabParity.OnSettled(gameObject, "settled after moving");
            }
        }

        // The SDK finds parts through physics triggers on the part; a sleeping body is kept
        // awake only while a hand / controller-hand body is close enough to be reaching for it.
        if (_resting && rb.IsSleeping())
        {
            var bodies = AdaptiveAR.Steps.ControllerGrabDiagnostics.InteractorBodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                Transform b = bodies[i];
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                if (_renderer == null) _renderer = GetComponentInChildren<Renderer>(true);
                Vector3 nearest = _renderer != null ? _renderer.bounds.ClosestPoint(b.position) : transform.position;   // the mesh, not the pivot
                if ((b.position - nearest).sqrMagnitude <= wakeRadius * wakeRadius)
                {
                    rb.WakeUp();
                    if (!_wokeLogged) { _wokeLogged = true; Debug.Log($"[ControllerRegrab] woke resting '{name}' - interactor '{b.name}' within {Vector3.Distance(b.position, nearest) * 100f:F0} cm of its mesh (sleeping body)."); }
                    break;
                }
            }
        }

        // THE DISAPPEARING-ROD GUARD. A part joined to an assembly has a new parent; its
        // authored tray pose expressed in that parent's frame is a point metres away, so
        // the old "lost" test fired and teleported it there. A joined or locked part is
        // never lost.
        if (transform.parent != authoredParent) return;
        var padlock = GetComponent<AdaptiveAR.Steps.PlacementLock>();
        if (padlock != null && padlock.IsLocked) return;

        Vector3 start = AuthoredWorldPosition();

        // Gravity is world -Y, so "fell away" means it dropped well below where it began.
        float fallen = start.y - transform.position.y;
        bool fellThrough = fallen > maxFallBelowStart;
        bool wayOff = Vector3.Distance(transform.position, start) > maxDistanceFromStart;

        if (fellThrough || wayOff)
            RecoverToAuthoredPose();
    }

    /// <summary>
    /// [RestingPart] when a loose part has come to rest: where it sits relative to the surface
    /// under it and whether any of its colliders is embedded in a tray / oil pan / surface. A
    /// part embedded more than 2 mm is lifted out along the separating direction and allowed
    /// to settle again (at most three times), instead of being left intersecting and asleep.
    /// </summary>
    private void AuditRestingGeometry()
    {
        if (_renderer == null) _renderer = GetComponentInChildren<Renderer>(true);
        Bounds b = _renderer != null ? _renderer.bounds : new Bounds(transform.position, Vector3.one * 0.02f);

        // the surface under the part: first solid hit below its centre that is not the part itself
        float floorY = float.NaN, trayTopY = float.NaN; string under = "none";
        foreach (RaycastHit h in Physics.RaycastAll(b.center + Vector3.up * 0.05f, Vector3.down, 0.6f))
        {
            if (h.collider == null || h.collider.attachedRigidbody == rb || h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<DropIntoTray>() != null && h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue; // another loose part
            floorY = h.point.y; trayTopY = h.collider.bounds.max.y; under = h.collider.name; break;
        }

        bool pen = DeepestPenetration(out Collider surface, out Vector3 dir, out float depth, out bool supported);
        float gap = float.IsNaN(floorY) ? float.NaN : b.min.y - floorY;
        Debug.Log($"[RestingPart] part={name}#{gameObject.GetInstanceID()} boundsMinY={b.min.y:F4} trayTopY={(float.IsNaN(trayTopY) ? "n/a" : trayTopY.ToString("F4"))} surfaceBelow={under} surfaceY={(float.IsNaN(floorY) ? "n/a" : floorY.ToString("F4"))} " +
                  $"gapAboveSurface={(float.IsNaN(gap) ? "n/a" : (gap * 1000f).ToString("F1") + "mm")} penetration={(pen ? (depth * 1000f).ToString("F1") + "mm into " + surface.name : (supported ? "none" : "n/a"))} " +
                  $"sleeping={rb.IsSleeping()} kinematic={rb.isKinematic} parent={(transform.parent != null ? transform.parent.name : "<root>")} collision={rb.collisionDetectionMode} resolves={_resolveCount}");

        if (pen && depth > 0.002f && _resolveCount < 3)
        {
            _resolveCount++;
            Vector3 lift = dir.normalized * (depth + 0.002f);
            transform.position += lift;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            rb.collisionDetectionMode = EffectiveCollisionDetection;
            rb.WakeUp();
            _resting = false; _wasMoving = true; _stillSince = -1f;
            Debug.LogError($"[RestingPart] '{name}' was embedded {depth * 1000f:F1} mm in '{surface.name}' - lifted {lift.magnitude * 1000f:F1} mm along {dir} to settle again (resolve {_resolveCount}/3).");
        }
        else if (pen && depth > 0.002f)
            Debug.LogError($"[RestingPart] '{name}' still embedded {depth * 1000f:F1} mm in '{surface.name}' after 3 lifts - left as is (geometry overlap between part and surface colliders).");
    }

    private void ApplyPhysicsProfile()
    {
        if (rb == null) return;

        rb.collisionDetectionMode = EffectiveCollisionDetection;
        rb.interpolation = EffectiveInterpolation;

        if (physicsProfile != PhysicsProfile.Tuned) return;

        // When overlapping geometry is made solid again (a rejected near-target release)
        // the engine separates it. The project default of 10 m/s flings a small part
        // across the bench; this is a gentle push instead.
        rb.maxDepenetrationVelocity = tunedMaxDepenetrationVelocity;

        // Only this object's own colliders: a joined kit child keeps its own setting.
        foreach (Collider c in GetComponents<Collider>())
        {
            if (c == null) continue;
            c.contactOffset = Mathf.Max(0.0005f, tunedContactOffset);
        }
    }

    /// <summary>Records the pose this part was authored at, used as the recovery target.</summary>
    private void CaptureAuthoredPose()
    {
        authoredLocalPosition = transform.localPosition;
        authoredLocalRotation = transform.localRotation;
        authoredParent = transform.parent;
        authoredPoseCaptured = true;
    }

    /// <summary>
    /// Session start: a loose part begins in its tray and settles under gravity, whatever
    /// its authored flag says. Not for installed or locked parts.
    /// </summary>
    public void ReleaseLoose()
    {
        if (rb == null) return;
        var padlock = GetComponent<AdaptiveAR.Steps.PlacementLock>();
        if (padlock != null && padlock.IsLocked) return;
        if (transform.parent != authoredParent) return;
        if (GetComponent<Collider>() == null) return;      // a kit root with no collider must stay still

        recoveryCount = 0;
        ReleaseToPhysics();
        AdaptiveAR.Steps.GrabParity.ReassertBroadcasters(gameObject, "released loose", false);
    }

    private Vector3 AuthoredWorldPosition()
    {
        return transform.parent != null
            ? transform.parent.TransformPoint(authoredLocalPosition)
            : authoredLocalPosition;
    }

    /// <summary>Makes the part dynamic so gravity and collisions act on it.</summary>
    public void ReleaseToPhysics()
    {
        if (rb == null) return;

        rb.collisionDetectionMode = EffectiveCollisionDetection;
        rb.isKinematic = false;
    }

    /// <summary>Freezes the part in place and clears any motion.</summary>
    public void Hold()
    {
        if (rb == null) return;

        rb.isKinematic = true;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    /// <summary>Returns the part to where it was authored, cancelling all motion.</summary>
    public void RecoverToAuthoredPose()
    {
        if (rb == null || !authoredPoseCaptured) return;
        if (transform.parent != authoredParent) return;

        AdaptiveAR.Steps.PartWatch.Log("DropIntoTray.Recover(before)", transform);

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        transform.localPosition = authoredLocalPosition;
        transform.localRotation = authoredLocalRotation;

        recoveryCount++;
        AdaptiveAR.Steps.PartWatch.Log("DropIntoTray.Recover(after)", transform, "recovery #" + recoveryCount);

        // Let it settle naturally the first few times. If it keeps escaping there is a real
        // gap in the colliders, so freeze it rather than loop forever.
        bool freeze = recoveryCount >= maxRecoveriesBeforeHolding;
        if (freeze)
            Hold();

        if (logRecovery)
            Debug.Log($"[DropIntoTray] '{name}' fell away and was returned to its start pose " +
                      $"(recovery {recoveryCount}{(freeze ? ", now frozen" : "")}).", this);
        AdaptiveAR.Steps.GrabParity.OnSettled(gameObject, "recovered to tray");
    }
}

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

    [Header("Debug")]
    [SerializeField] private bool logRecovery = true;

    private Rigidbody rb;

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

        // Let it settle naturally the first few times. If it keeps escaping there is a real
        // gap in the colliders, so freeze it rather than loop forever.
        bool freeze = recoveryCount >= maxRecoveriesBeforeHolding;
        if (freeze)
            Hold();

        if (logRecovery)
            Debug.Log($"[DropIntoTray] '{name}' fell away and was returned to its start pose " +
                      $"(recovery {recoveryCount}{(freeze ? ", now frozen" : "")}).", this);
    }
}

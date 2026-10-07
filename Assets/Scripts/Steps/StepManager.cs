using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TryAR.MarkerTracking;
using AdaptiveAR.MR;
using AdaptiveAR.Steps;

/// <summary>
/// Owns the anchoring step: when the engine appears, and where.
///
/// Two registration providers exist. The participant startup uses surface placement
/// (point at the desk, confirm). The ArUco marker path is kept intact and can be
/// re-enabled by turning useSurfacePlacement off; nothing of it was deleted.
/// Downstream systems only ever see <see cref="AnchorLocked"/> and EngineAnchor, so
/// they do not know which provider was used.
/// </summary>
public class StepManager : MonoBehaviour
{
    [Header("UI")]
    public TextMeshProUGUI captionText;
    public Button beginButton;

    [Header("Tracking")]
    public GameObject oilPan;
    public ArUcoTrackingAppCoordinator arucoCoordinator;
    public ArUcoMarkerTracking arucoTracking;
    public int blockMarkerId;

    [Header("Workspace placement (participant startup)")]
    [Tooltip("ON: the participant places the workspace on the desk; no marker is needed and " +
             "ArUco never runs. OFF: the original marker-anchored startup, unchanged.")]
    public bool useSurfacePlacement = true;

    [Tooltip("Optional. Left empty, a WorkspacePlacement is created on this object at runtime " +
             "with its default settings. Add one to the scene to tune it in the Inspector.")]
    public WorkspacePlacement placement;

    [Tooltip("While surface placement is in use, also switch off the passthrough camera " +
             "stream and the CV debug quad so no camera permission or CPU is spent on them.")]
    public bool disableCameraAccessWhenBypassed = true;

    [Header("Work surface and trays")]
    [Tooltip("Create the virtual piston work surface once the desk is known, and pose the " +
             "ghost kits on it.")]
    public bool createWorkSurface = true;

    [Tooltip("Optional. Created at runtime with default settings when empty.")]
    public AssemblyWorkSurface workSurface;

    [Tooltip("Fit thick container colliders to the trays at startup so parts cannot pass " +
             "through their floor or walls.")]
    public bool fitTrayColliders = true;

    [Header("Parts")]
    public GameObject crankshaftPrefab;
    public Vector3 crankshaftSpawnOffset;

    [Header("Step Sequence (optional)")]
    [Tooltip("Leave EMPTY to keep the original hard-coded behaviour exactly as-is. " +
             "Assign a StepRunner to hand the first instruction over to the " +
             "configurable step / support-level system instead.")]
    public StepRunner stepRunner;

    private GameObject spawnedCrankshaft;

    private bool sessionStarted = false;
    private bool anchorLocked = false;
    private bool partsSpawned = false;

    /// <summary>True once the workspace has been placed (or the ArUco anchor found) and frozen.</summary>
    public bool AnchorLocked { get { return anchorLocked; } }

    /// <summary>The placement provider in use, or null on the marker path.</summary>
    public WorkspacePlacement Placement { get { return placement; } }

    /// <summary>The piston work surface, once created.</summary>
    public AssemblyWorkSurface WorkSurface { get { return workSurface; } }

    private AdaptiveAR.UI.AdaptivePanelController _panels;

    /// <summary>
    /// Starts the session from outside. The onboarding flow calls this instead of
    /// wiring the button straight to StartSession: that wiring also ran
    /// beginButton.SetActive(false), which hid the shared onboarding button after the
    /// first press and left the later screens with no way forward.
    /// </summary>
    public void StartSessionExternal()
    {
        StartSession();
    }

    void Awake()
    {
        // Set up before anyone's Start() so the onboarding can find the placement
        // provider on its first frame.
        if (useSurfacePlacement)
            EnsurePlacement();
    }

    void Start()
    {
        if (beginButton != null)
            beginButton.onClick.AddListener(StartSession);

        if (captionText != null)
            captionText.text = "";

        if (beginButton != null)
            beginButton.gameObject.SetActive(true);

        // Hidden until the workspace exists.
        if (oilPan != null)
            oilPan.SetActive(false);

        if (useSurfacePlacement)
        {
            BypassAruco();

            // Only the main panel until the workspace exists.
            _panels = FindAnyObjectByType<AdaptiveAR.UI.AdaptivePanelController>(FindObjectsInactive.Include);
            if (_panels != null && placement != null && !placement.IsPlaced)
                _panels.SetPlacementMode(true);
        }

        if (fitTrayColliders && oilPan != null)
            TrayColliders.Ensure(oilPan.transform.root);
    }

    // ---------------- SURFACE PLACEMENT ----------------

    private void EnsurePlacement()
    {
        if (placement == null)
            placement = FindAnyObjectByType<WorkspacePlacement>(FindObjectsInactive.Include);

        if (placement == null)
        {
            placement = gameObject.AddComponent<WorkspacePlacement>();
            Debug.Log("[StepManager] WorkspacePlacement created at runtime with default settings. " +
                      "Add one to the scene to tune it in the Inspector.");
        }

        // MarkerAnchor is the top of the anchor chain; EngineAnchor hangs under it. Posing the
        // root keeps CalibrationOffset, the trays, the ghosts and the PanelRig exactly as
        // they were relative to the marker.
        Transform root = oilPan != null ? oilPan.transform.root : null;
        placement.Configure(root, Camera.main != null ? Camera.main.transform : null);

        placement.OnPlaced -= HandleWorkspacePlaced;
        placement.OnPlaced += HandleWorkspacePlaced;
    }

    private void OnDestroy()
    {
        if (placement != null)
            placement.OnPlaced -= HandleWorkspacePlaced;
    }

    /// <summary>
    /// Keeps the ArUco implementation in the project but out of the participant path:
    /// no detection, no camera stream, no debug quad. Turning useSurfacePlacement off
    /// restores all of it.
    /// </summary>
    private void BypassAruco()
    {
        if (arucoCoordinator != null)
            arucoCoordinator.enabled = false;

        // The coordinator's Start() normally hides this quad. With the coordinator off that
        // never runs, so hide it here or a blank quad sits in front of the camera.
        var debugQuad = FindAnyObjectByType<CameraImageAduster>(FindObjectsInactive.Include);
        if (debugQuad != null)
            debugQuad.gameObject.SetActive(false);

        if (disableCameraAccessWhenBypassed)
        {
            var cam = FindAnyObjectByType<Meta.XR.PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (cam != null)
                cam.gameObject.SetActive(false);
        }

        Debug.Log("[StepManager] ArUco startup bypassed: surface placement provides the workspace pose.");
    }

    private void HandleWorkspacePlaced(bool reposition)
    {
        if (reposition)
        {
            if (captionText != null && !anchorLocked)
                captionText.text = "Workspace moved.";
            PlaceWorkSurface();
            return;
        }

        // Same effect the marker lock had: freeze the pose, reveal the engine.
        anchorLocked = true;

        if (oilPan != null)
            oilPan.SetActive(true);

        if (captionText != null)
            captionText.text = "Workspace placed.";

        PlaceWorkSurface();
    }

    /// <summary>
    /// Creates (once) and poses the piston work surface beside the engine, a hand height
    /// above the desk, then arranges the ghost kits on it.
    /// </summary>
    private void PlaceWorkSurface()
    {
        if (!createWorkSurface || placement == null || oilPan == null) return;

        if (workSurface == null)
            workSurface = FindAnyObjectByType<AssemblyWorkSurface>(FindObjectsInactive.Include);

        if (workSurface == null)
        {
            var go = new GameObject("WorkSurface");
            workSurface = go.AddComponent<AssemblyWorkSurface>();
            Debug.Log("[StepManager] AssemblyWorkSurface created at runtime with default settings.");
        }

        Transform head = Camera.main != null ? Camera.main.transform : null;
        workSurface.Place(oilPan.transform, placement.LastPlacedPosition.y, head);

        var registry = GetComponent<GuidanceRegistry>();
        if (registry != null)
            workSurface.ArrangeKitGhosts(registry, head);
    }

    // ---------------- SESSION ----------------

    void StartSession()
    {
        // Idempotent. MultiInputTrigger invokes this button's onClick on every index
        // trigger press - the same button used to grab parts - so without this guard
        // each grab would overwrite the current step's instruction caption.
        if (sessionStarted) return;

        sessionStarted = true;

        // The walkthrough ran on the main panel alone; the full participant layout
        // (relative to the placed workspace) appears as the assembly begins.
        if (_panels != null && _panels.PlacementMode)
            _panels.SetPlacementMode(false);

        // Only hide a button this component owns. The onboarding button is shared across
        // four screens and must survive.
        if (beginButton != null)
            beginButton.gameObject.SetActive(false);

        if (captionText != null && !anchorLocked)
            captionText.text = useSurfacePlacement
                ? "Place the workspace to begin."
                : "Look at the marker to anchor the engine.";
    }

    void Update()
    {
        if (!sessionStarted) return;

        // Marker path only. With surface placement the lock comes from HandleWorkspacePlaced.
        if (!useSurfacePlacement && !anchorLocked && IsMarkerDetected(blockMarkerId))
        {
            ActivateAndLockBlock();
        }

        // Spawn parts / begin the sequence once the workspace exists.
        if (anchorLocked && !partsSpawned)
        {
            SpawnParts();
        }
    }

    // ---------------- MARKER DETECTION (ArUco path, unchanged) ----------------

    bool IsMarkerDetected(int markerId)
    {
        if (arucoTracking == null) return false;

        var idsField = typeof(ArUcoMarkerTracking)
            .GetField("_detectedMarkerIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (idsField == null) return false;

        var mat = idsField.GetValue(arucoTracking) as OpenCVForUnity.CoreModule.Mat;

        if (mat == null || mat.total() == 0) return false;

        for (int i = 0; i < mat.total(); i++)
        {
            if ((int)mat.get(i, 0)[0] == markerId)
                return true;
        }

        return false;
    }

    // ---------------- LOCK BLOCK ----------------

    void ActivateAndLockBlock()
    {
        anchorLocked = true;

        // now show block (it will already be aligned by ArUco)
        oilPan.SetActive(true);

        // STOP tracking -> no jitter forever
        if (arucoCoordinator != null)
            arucoCoordinator.enabled = false;

        if (captionText != null)
            captionText.text = "Engine anchored.";
    }

    // ---------------- SPAWN PARTS ----------------

    void SpawnParts()
    {
        partsSpawned = true;

        // If a StepRunner is assigned, hand the first instruction over to the
        // configurable step / support-level system. If it is NOT assigned, the
        // original hard-coded path below runs unchanged.
        if (stepRunner != null)
        {
            if (stepRunner.BeginSequence())
                return;

            Debug.LogWarning("[StepManager] StepRunner could not begin; falling back to the built-in crankshaft step.");
        }

        captionText.text = "Pick up the crankshaft.";

        spawnedCrankshaft = Instantiate(crankshaftPrefab);

        spawnedCrankshaft.transform.position =
            oilPan.transform.position +
            oilPan.transform.rotation * crankshaftSpawnOffset;

        spawnedCrankshaft.transform.rotation =
            oilPan.transform.rotation;

        spawnedCrankshaft.transform.localScale = Vector3.one;
    }
}

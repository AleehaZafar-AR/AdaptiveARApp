using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class StepManager : MonoBehaviour
{
    [Header("UI")]
    public TextMeshProUGUI captionText;
    public Button confirmPlacementButton;

    [Header("Markers (GameObjects driven by ArUco)")]
    public Transform staticBlockMarker;
    public Transform crankshaftMarker;

    [Header("Ghost")]
    public GameObject crankshaftGhostPrefab;
    private GameObject currentGhost;

    [Header("Offsets (relative to block marker)")]
    public Vector3 crankshaftPositionOffset;
    public Vector3 crankshaftRotationOffset;

    [Header("Validation")]
    public float positionTolerance = 0.01f;
    public float rotationTolerance = 10f;

    private bool stepActive = false;
    private bool placementReady = false;
    private bool anchorLocked = false;

    private float stableTimer = 0f;

    // Marker tracking helpers
    private Vector3 lastBlockPos;
    private Vector3 lastCrankPos;

    private float blockMoveTimer = 0f;
    private float crankMoveTimer = 0f;

    private float detectionThreshold = 0.001f;

    void Start()
    {
        confirmPlacementButton.gameObject.SetActive(false);
        confirmPlacementButton.onClick.AddListener(ConfirmPlacement);

        ShowDemo();
    }

    // ---------------- DEMO ----------------

    void ShowDemo()
    {
        captionText.text =
            "Welcome.\n\n" +
            "Follow instructions to assemble the engine.\n\n" +
            "Press GOT IT to begin.";
    }

    public void StartStep1()
    {
        captionText.text = "Step 1: Place the crankshaft onto the engine block.";
        stepActive = true;
    }

    // ---------------- UPDATE ----------------

    void Update()
    {
        if (!stepActive) return;

        // 🔒 Anchor locking logic
        if (!anchorLocked)
        {
            bool blockDetected = IsMarkerTracked(staticBlockMarker, ref lastBlockPos, ref blockMoveTimer);

            if (!blockDetected)
            {
                captionText.text = "Looking for engine block...";
                return;
            }

            // wait until stable
            if (blockMoveTimer < 0.2f)
            {
                stableTimer += Time.deltaTime;

                if (stableTimer > 0.5f)
                {
                    LockAnchor();
                }
            }
            else
            {
                stableTimer = 0f;
            }

            return;
        }

        // 🟢 Spawn ghost AFTER lock
        if (currentGhost == null)
        {
            SpawnGhost();
        }

        // 🔍 Crankshaft detection
        bool crankDetected = IsMarkerTracked(crankshaftMarker, ref lastCrankPos, ref crankMoveTimer);

        if (!crankDetected)
        {
            captionText.text = "Pick up the crankshaft.";
            return;
        }

        // ✅ Validate placement
        ValidatePlacement();
    }

    // ---------------- LOCK ANCHOR ----------------

    void LockAnchor()
    {
        anchorLocked = true;

        // Detach from marker updates → NO JITTER
        staticBlockMarker.parent = null;

        captionText.text = "Block locked. Proceed with placement.";
    }

    // ---------------- MARKER DETECTION ----------------

    bool IsMarkerTracked(Transform marker, ref Vector3 lastPos, ref float timer)
    {
        float dist = Vector3.Distance(marker.position, lastPos);

        if (dist > detectionThreshold)
        {
            timer = 0f;
            lastPos = marker.position;
            return true;
        }
        else
        {
            timer += Time.deltaTime;

            if (timer > 0.5f)
                return false;

            return true;
        }
    }

    // ---------------- GHOST ----------------

    void SpawnGhost()
    {
        currentGhost = Instantiate(crankshaftGhostPrefab);

        currentGhost.transform.position =
            staticBlockMarker.position +
            staticBlockMarker.rotation * crankshaftPositionOffset;

        currentGhost.transform.rotation =
            staticBlockMarker.rotation *
            Quaternion.Euler(crankshaftRotationOffset);
    }

    // ---------------- VALIDATION ----------------

    void ValidatePlacement()
    {
        Vector3 targetPos = currentGhost.transform.position;
        Quaternion targetRot = currentGhost.transform.rotation;

        Vector3 currentPos = crankshaftMarker.position;
        Quaternion currentRot = crankshaftMarker.rotation;

        float posError = Vector3.Distance(currentPos, targetPos);
        float rotError = Quaternion.Angle(currentRot, targetRot);

        if (posError < positionTolerance && rotError < rotationTolerance)
        {
            stableTimer += Time.deltaTime;

            if (stableTimer > 0.5f && !placementReady)
            {
                PlacementValid();
            }
        }
        else
        {
            stableTimer = 0f;
        }
    }

    void PlacementValid()
    {
        placementReady = true;

        captionText.text = "Alignment correct. Confirm placement.";
        confirmPlacementButton.gameObject.SetActive(true);
    }

    public void ConfirmPlacement()
    {
        captionText.text = "Step complete!";
        confirmPlacementButton.gameObject.SetActive(false);

        stepActive = false;
    }
}
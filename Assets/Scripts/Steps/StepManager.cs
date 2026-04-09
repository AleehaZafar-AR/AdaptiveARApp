using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TryAR.MarkerTracking;

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

    [Header("Parts")]
    public GameObject crankshaftPrefab;
    public Vector3 crankshaftSpawnOffset;

    private GameObject spawnedCrankshaft;

    private bool sessionStarted = false;
    private bool anchorLocked = false;
    private bool partsSpawned = false;

    void Start()
    {
        beginButton.onClick.AddListener(StartSession);

        captionText.text = "Welcome.\n\nPress BEGIN to start.";
        beginButton.gameObject.SetActive(true);

        // ❌ hide block initially
        oilPan.SetActive(false);
    }

    void StartSession()
    {
        sessionStarted = true;
        beginButton.gameObject.SetActive(false);

        captionText.text = "Look at the marker to place the engine block.";
    }

    void Update()
    {
        if (!sessionStarted) return;

        // 🟢 Detect marker directly (correct way)
        if (!anchorLocked && IsMarkerDetected(blockMarkerId))
        {
            ActivateAndLockBlock();
        }

        // 🟢 Spawn parts after lock
        if (anchorLocked && !partsSpawned)
        {
            SpawnParts();
        }
    }

    // ---------------- MARKER DETECTION ----------------

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

        // ✅ now show block (it will already be aligned by ArUco)
        oilPan.SetActive(true);

        // 🔒 STOP tracking → no jitter forever
        if (arucoCoordinator != null)
            arucoCoordinator.enabled = false;

        captionText.text = "Block placed.";
    }

    // ---------------- SPAWN PARTS ----------------

    void SpawnParts()
    {
        partsSpawned = true;

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
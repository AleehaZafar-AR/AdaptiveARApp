using UnityEditor;
using UnityEngine.UI;
using System.IO;
using System;
using System.Linq;
using TMPro;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SnapToPosition : MonoBehaviour
{
    public static SnapToPosition activeSnapper; // Static reference to track the currently active snapper

    public Transform snapTarget; // The target position to snap to
    public GameObject snapIndicator; // Indicator that shows when snapping is close
    public float positionThreshold = 10.0f; // Distance within which snapping occurs for position
    public float rotationThreshold = 0.1f; // Threshold for rotation in degrees

    private bool isPlaced = false; // To ensure the object snaps only once

    void Update()
    {
        if (!isPlaced && (activeSnapper == null || activeSnapper == this))
        {
            StartCoroutine(SnappingToPosition());
        }
    }

    IEnumerator SnappingToPosition()
    {
        // Check if the object is within the interaction threshold
        if (Vector3.Distance(transform.position, snapTarget.position) < 0.1f)
        {
            print(Vector3.Distance(transform.position, snapTarget.position));
            if (activeSnapper == null)
            {
                activeSnapper = this; // Set this as the active snapper
                snapIndicator.SetActive(true); // Activate snap indicator when close
            }

            // Check if the object is within the precise position and rotation threshold
            if (Math.Round(Vector3.Distance(transform.position, snapTarget.position), 2) < positionThreshold && Vector3.Distance(transform.localEulerAngles, snapTarget.localEulerAngles) < rotationThreshold)
            {
                Debug.Log(gameObject.name + " has been placed correctly!"); // Log message
                DisableInteractions(); // Disable further interactions
                yield return new WaitForSeconds(1); // Optional delay to demonstrate the snapping process
                transform.position = snapTarget.position; // Snap position
                transform.rotation = snapTarget.rotation; // Snap rotation
                isPlaced = true; // Set the flag so it doesn't keep snapping
                snapIndicator.SetActive(false); // Deactivate indicator once snapped
                activeSnapper = null; // Clear the active snapper
            }
        }
        else if (activeSnapper == this)
        {
            snapIndicator.SetActive(false); // Ensure indicator is off if not within threshold
            activeSnapper = null; // Clear the active snapper
        }
    }

    private void DisableInteractions()
    {
        // This function can disable any interaction component or script like Rigidbody, Collider, or Custom Drag Script
        // Here disabling the Collider component to prevent further interactions
        GetComponent<Collider>().enabled = false;

        // Optionally, if you have a drag script or other interaction scripts, disable them as well
        // GetComponent<DragPiston>().enabled = false; // Assuming the script is named DragPiston
    }
}

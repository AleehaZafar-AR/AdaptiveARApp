// File: MultiInputTrigger.cs
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MultiInputTrigger : MonoBehaviour
{
    public Button gotItButton;

    void Update()
    {
        if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space) || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            if (gotItButton != null)
                gotItButton.onClick.Invoke();
        }
    }
}

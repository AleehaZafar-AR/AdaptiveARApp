// File: StepData.cs
using UnityEngine;

[CreateAssetMenu(fileName = "StepData", menuName = "XRAssembly/Step Data")]
public class StepData : ScriptableObject
{
    [Header("UI")]
    public string stepTitle;
    public string stepDescription;

    [Header("3D Overlay")]
    public GameObject ghostPrefab;

    [Header("Audio Instruction")]
    public AudioClip instructionAudio;

    [Header("Arrow Guide (Optional)")]
    public GameObject arrowPrefab;

    void Start()
    {
        ghostPrefab = GameObject.Find("crankshaftGhostOverlay");
    }
}

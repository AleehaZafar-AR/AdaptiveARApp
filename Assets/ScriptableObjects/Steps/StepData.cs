// File: StepData.cs
// One researcher-authored assembly step.
//
// The original four fields are retained unchanged so the existing
// Step_0_Demo / Step_1_Crankshaft assets keep deserialising. They now act as the
// STEP-LEVEL DEFAULTS: whatever a support level leaves empty falls back to them.
//
// The three StepSupportContent blocks are the per-level guidance. They ship empty
// by design - L1/L2/L3 contents are authored from the literature, not in code.

using AdaptiveAR.Steps;
using AdaptiveAR.Support;
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

    [Header("Identity")]
    [Tooltip("Stable identifier used in logs. Falls back to the asset name if left empty.")]
    public string stepId;

    [Tooltip("Short label for the task list, e.g. \"Crankshaft\". The full stepTitle is a " +
             "whole sentence and is too long for a list row.")]
    public string displayName;

    [Header("Step Defaults")]
    [Tooltip("Offset from the anchor root used when a support level spawns the default ghostPrefab.")]
    public Vector3 defaultGhostSpawnOffset;

    [Header("Decision Inputs")]
    [Tooltip("Researcher-authored task complexity for this step. " +
             "Leave at 0 until defined; it is a decision-layer input, not a policy value.")]
    public int taskComplexity;

    [Header("Validation")]
    [Tooltip("Does this step check that a part was placed correctly? " +
             "Off means the step advances only when the operator confirms.")]
    public bool requiresValidation = true;

    [Tooltip("GuidanceRegistry key for the part the operator must move, e.g. \"part.crankshaft\".")]
    public string validationPartKey;

    [Tooltip("GuidanceRegistry key for the pose it must reach, e.g. \"ghost.crankshaft\".")]
    public string validationTargetKey;

    [Tooltip("Other parts that are equally acceptable for this step. The four pistons are " +
             "interchangeable, so picking up any unplaced one should count as correct rather " +
             "than being marked an error. Leave empty for a unique part.")]
    public string[] interchangeablePartKeys;

    /// <summary>Every part key this step will accept, primary first.</summary>
    public System.Collections.Generic.List<string> AllAcceptedPartKeys()
    {
        var keys = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(validationPartKey)) keys.Add(validationPartKey);

        if (interchangeablePartKeys != null)
        {
            foreach (string k in interchangeablePartKeys)
                if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }

    [Tooltip("How close the part must get, in metres.")]
    public float positionToleranceMeters = 0.04f;

    [Tooltip("How closely the part's rotation must match, in degrees.")]
    public float rotationToleranceDegrees = 25f;

    [Tooltip("Snap the part exactly onto the target pose once it is accepted.")]
    public bool snapOnSuccess = true;

    [Header("Actions (substeps)")]
    [Tooltip("The concrete things the participant does inside this stage, in order. " +
             "The participant sees ONE at a time, which is what keeps L3 from becoming a " +
             "paragraph. A stage is complete only when every enabled action is validated.")]
    public System.Collections.Generic.List<AssemblyAction> actions =
        new System.Collections.Generic.List<AssemblyAction>();

    /// <summary>First enabled action at or after an index, or -1 when none remain.</summary>
    public int NextEnabledActionIndex(int from)
    {
        if (actions == null) return -1;
        for (int i = Mathf.Max(0, from); i < actions.Count; i++)
            if (actions[i] != null && actions[i].enabled) return i;
        return -1;
    }

    [Header("Timing")]
    [Tooltip("Researcher-authored expected duration in seconds. Fed to the decision layer " +
             "as context. 0 means unset and is logged as null.")]
    public float expectedDurationSeconds;

    [Header("Support Level")]
    [Tooltip("Level this step opens at when it is entered.")]
    public SupportLevel defaultSupportLevel = SupportLevel.L1_Minimal;

    [Tooltip("L1 Minimal Support. Author from the literature; empty fields inherit the step defaults above.")]
    public StepSupportContent l1Minimal = new StepSupportContent();

    [Tooltip("L2 Guided Support. Author from the literature; empty fields inherit the step defaults above.")]
    public StepSupportContent l2Guided = new StepSupportContent();

    [Tooltip("L3 Assisted Support. Author from the literature; empty fields inherit the step defaults above.")]
    public StepSupportContent l3Assisted = new StepSupportContent();

    /// <summary>
    /// Identifier used for logging and debug output.
    /// </summary>
    public string StepIdentifier
    {
        get { return string.IsNullOrEmpty(stepId) ? name : stepId; }
    }

    /// <summary>
    /// Returns the authored content block for a support level.
    /// Never returns null; an unauthored block is simply empty, and the presenter
    /// falls back per-field to the step-level defaults.
    /// </summary>
    public StepSupportContent GetContent(SupportLevel level)
    {
        switch (level)
        {
            case SupportLevel.L3_Assisted:
                return l3Assisted ?? (l3Assisted = new StepSupportContent());

            case SupportLevel.L2_Guided:
                return l2Guided ?? (l2Guided = new StepSupportContent());

            case SupportLevel.L1_Minimal:
            default:
                return l1Minimal ?? (l1Minimal = new StepSupportContent());
        }
    }

    /// <summary>
    /// True when a level has no authored content and will render entirely from
    /// the step-level defaults. Useful for an authoring checklist later.
    /// </summary>
    public bool IsLevelUnauthored(SupportLevel level)
    {
        return GetContent(level).IsEmpty;
    }
}

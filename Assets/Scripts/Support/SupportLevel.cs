// File: SupportLevel.cs
// The three researcher-defined levels of instructional support.
//
// Contents and transition policy for L1/L2/L3 are defined separately from the
// literature and authored per step in StepData. Nothing in this file encodes
// what a level contains or when it should change.

namespace AdaptiveAR.Support
{
    /// <summary>
    /// Level of instructional support currently presented to the operator.
    /// Explicit numeric values are stable identifiers for logging and must not be reordered.
    /// </summary>
    public enum SupportLevel
    {
        /// <summary>L1 - Minimal Support.</summary>
        L1_Minimal = 1,

        /// <summary>L2 - Guided Support.</summary>
        L2_Guided = 2,

        /// <summary>L3 - Assisted Support.</summary>
        L3_Assisted = 3
    }
}

// File: GuidanceText.cs
// The one rule for which sentence an action shows. Used by the participant card
// (which renders it), the presenter (which logs what the card must be showing for
// the action that owns the ghost) and the session's action diagnostic, so all three
// agree by construction.
//
//   L1 with an authored stage-level sentence  -> that sentence ("Repeat the procedure ...")
//   otherwise                                  -> the action's own instruction
//   no action                                  -> the stage title

using AdaptiveAR.Support;

namespace AdaptiveAR.Steps
{
    public static class GuidanceText
    {
        public static string InstructionFor(StepData stage, AssemblyAction action, SupportLevel level)
        {
            if (level == SupportLevel.L1_Minimal && stage != null)
            {
                StepSupportContent l1 = stage.GetContent(SupportLevel.L1_Minimal);
                if (l1 != null && !string.IsNullOrEmpty(l1.instructionText)) return l1.instructionText;
            }

            if (action != null && !string.IsNullOrEmpty(action.instruction)) return action.instruction;
            return stage != null && stage.stepTitle != null ? stage.stepTitle : "";
        }
    }
}

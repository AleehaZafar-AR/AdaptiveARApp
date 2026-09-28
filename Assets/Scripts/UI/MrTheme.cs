// File: MrTheme.cs
// The single source of truth for the spatial interface's visual language.
//
// Colours and sizes live here rather than being typed into the Inspector on each
// element, so the whole interface can be retuned for headset readability in one
// place - which matters because passthrough contrast on a Quest 3 is nothing like
// the Editor Game view (CLAUDE.md 4.1, 4.2).
//
// Design language, from docs/UIReference/Inspiration.png:
//   - dark translucent charcoal panels, subtle rounded corners, thin borders
//   - white type with a strong hierarchy, generous spacing
//   - cyan/teal is the guidance accent
//   - amber is reserved for warnings and misalignment
//   - restrained green means confirmed / complete
//   - no large saturated blocks; the physical engine stays the visual focus

using UnityEngine;

namespace AdaptiveAR.UI
{
    public static class MrTheme
    {
        // =====================================================================
        // Palette
        // =====================================================================

        /// <summary>Panel body. Dark charcoal, translucent so passthrough reads through it.</summary>
        public static readonly Color PanelFill = new Color32(0x16, 0x1A, 0x1E, 0xD4);

        /// <summary>Slightly lighter charcoal for a nested row or an inset field.</summary>
        public static readonly Color PanelFillRaised = new Color32(0x20, 0x26, 0x2B, 0xD9);

        /// <summary>Hairline border. Low alpha so it reads as an edge, not a frame.</summary>
        public static readonly Color PanelBorder = new Color32(0x6F, 0x7C, 0x85, 0x4D);

        /// <summary>Primary guidance accent.</summary>
        public static readonly Color Accent = new Color32(0x3F, 0xD8, 0xD4, 0xFF);

        /// <summary>Accent at low alpha, for fills behind an active element.</summary>
        public static readonly Color AccentSoft = new Color32(0x3F, 0xD8, 0xD4, 0x2E);

        /// <summary>Warnings and incorrect alignment only.</summary>
        public static readonly Color Warning = new Color32(0xF5, 0xA6, 0x23, 0xFF);
        public static readonly Color WarningSoft = new Color32(0xF5, 0xA6, 0x23, 0x2E);

        /// <summary>Confirmed / completed. Restrained, never a saturated block.</summary>
        public static readonly Color Success = new Color32(0x5F, 0xC9, 0x8B, 0xFF);
        public static readonly Color SuccessSoft = new Color32(0x5F, 0xC9, 0x8B, 0x2E);

        // --- typography ---
        public static readonly Color TextPrimary = new Color32(0xF4, 0xF7, 0xF8, 0xFF);
        public static readonly Color TextSecondary = new Color32(0xB6, 0xC0, 0xC6, 0xFF);
        public static readonly Color TextMuted = new Color32(0x7C, 0x88, 0x8F, 0xFF);

        /// <summary>Progress segment that has not been reached yet.</summary>
        public static readonly Color TrackEmpty = new Color32(0x3A, 0x43, 0x49, 0xFF);

        // =====================================================================
        // Type scale
        //
        // World-space canvases here run at roughly 1 unit = 1 mm at the canvas's
        // own scale, so these are tuned for arm's-length reading rather than for a
        // screen. Body text below ~22 gets unreliable on passthrough.
        // =====================================================================

        public const float SizeEyebrow = 20f;   // small uppercase label above a title
        public const float SizeTitle = 42f;   // the instruction headline
        public const float SizeBody = 26f;   // supporting sentence
        public const float SizeList = 24f;   // task list rows, numbered sub-steps
        public const float SizeButton = 24f;
        public const float SizeMetric = 28f;   // debug HUD values
        public const float SizeMetricLabel = 18f;

        /// <summary>Extra spacing for small uppercase labels, which need it to stay legible.</summary>
        public const float EyebrowCharacterSpacing = 8f;

        // =====================================================================
        // Metrics
        // =====================================================================

        public const float PanelPadding = 28f;
        public const float RowSpacing = 16f;
        public const float SectionSpacing = 24f;
        public const float CornerRadiusPx = 24f;
        public const float BorderThickness = 1.5f;

        /// <summary>Progress bar segment height and gap, matching the reference's segmented bar.</summary>
        public const float ProgressSegmentHeight = 10f;
        public const float ProgressSegmentGap = 5f;

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Colour for a step in the task list, by its state.</summary>
        public static Color TaskRowColor(bool done, bool current)
        {
            if (current) return Accent;
            return done ? TextMuted : TextSecondary;
        }

        /// <summary>Accent colour for a placement state: green in tolerance, amber outside it.</summary>
        public static Color AlignmentColor(bool inTolerance)
        {
            return inTolerance ? Success : Warning;
        }

        /// <summary>The same colour at a chosen alpha, for soft fills.</summary>
        public static Color WithAlpha(Color c, float alpha)
        {
            c.a = alpha;
            return c;
        }
    }
}

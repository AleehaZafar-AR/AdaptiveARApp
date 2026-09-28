// File: UiSpriteFactory.cs
// Generates the few sprites the spatial interface needs, so the design does not
// depend on imported art that does not exist in this project.
//
// Two 9-sliced sprites are produced:
//   UI_RoundedPanel   - a filled rounded rectangle
//   UI_RoundedBorder  - the same shape as a thin outline
//
// Nine-slicing means one 64px texture serves panels of any size without the
// corners stretching.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace AdaptiveAR.EditorTools
{
    public static class UiSpriteFactory
    {
        public const string Folder = "Assets/UI/Generated";
        public const string PanelSpritePath = Folder + "/UI_RoundedPanel.png";
        public const string BorderSpritePath = Folder + "/UI_RoundedBorder.png";

        private const int Size = 64;
        private const int Radius = 20;
        private const float BorderPx = 2.5f;

        [MenuItem("AdaptiveAR/UI/Generate Panel Sprites", false, 40)]
        public static void GenerateMenu()
        {
            EnsureSprites(true);
        }

        /// <summary>Creates the sprites if they are missing. Returns the filled panel sprite.</summary>
        public static Sprite EnsureSprites(bool log = false)
        {
            Directory.CreateDirectory(Folder);

            bool madeAny = false;
            if (AssetDatabase.LoadAssetAtPath<Sprite>(PanelSpritePath) == null)
            {
                WritePng(PanelSpritePath, BuildRounded(false));
                madeAny = true;
            }

            if (AssetDatabase.LoadAssetAtPath<Sprite>(BorderSpritePath) == null)
            {
                WritePng(BorderSpritePath, BuildRounded(true));
                madeAny = true;
            }

            if (madeAny)
            {
                AssetDatabase.Refresh();
                ConfigureImporter(PanelSpritePath);
                ConfigureImporter(BorderSpritePath);
                AssetDatabase.Refresh();
            }

            Sprite panel = AssetDatabase.LoadAssetAtPath<Sprite>(PanelSpritePath);

            if (log)
                Debug.Log($"[UiSpriteFactory] Panel sprites ready in {Folder} " +
                          $"({(madeAny ? "generated" : "already present")}).");

            return panel;
        }

        public static Sprite BorderSprite()
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(BorderSpritePath);
        }

        // =====================================================================

        private static Texture2D BuildRounded(bool outlineOnly)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float d = RoundedRectDistance(x + 0.5f, y + 0.5f);

                    // Negative inside the shape. Anti-alias across one pixel at the edge.
                    float outer = Mathf.Clamp01(0.5f - d);

                    float a = outlineOnly
                        ? Mathf.Clamp01(outer) * Mathf.Clamp01(d + BorderPx + 0.5f)
                        : outer;

                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
                }
            }

            tex.Apply();
            return tex;
        }

        /// <summary>Signed distance from the rounded-rectangle edge, in pixels.</summary>
        private static float RoundedRectDistance(float px, float py)
        {
            float half = Size * 0.5f;
            float qx = Mathf.Abs(px - half) - (half - Radius);
            float qy = Mathf.Abs(py - half) - (half - Radius);

            // Standard rounded-box signed distance: negative inside, zero on the edge.
            float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) +
                                       Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);

            return outside + inside;
        }

        private static void WritePng(string path, Texture2D tex)
        {
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private static void ConfigureImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            // Nine-slice borders keep the corner radius constant at any panel size.
            importer.spriteBorder = new Vector4(Radius, Radius, Radius, Radius);

            importer.SaveAndReimport();
        }
    }
}

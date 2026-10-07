// File: TrayColliders.cs
// Makes the two parts trays behave as solid containers.
//
// The trays are thin non-convex mesh shells scaled up ~60x. A thin shell is the
// worst case for a physics engine: a part released inside a wall is pushed out on
// whichever side is nearer, a part authored overlapping the floor can be pushed down
// through it, and a fast part can skip a thin triangle. Instead of relying on that
// shell alone, five thick box colliders are added at runtime from the mesh bounds:
// a slab under the floor and four walls that extend above the rim. Boxes have an
// unambiguous inside, so "out" always means up and over the rim.
//
// Runtime only: the scene and the tray meshes are untouched.

using UnityEngine;

namespace AdaptiveAR.MR
{
    public static class TrayColliders
    {
        /// <summary>Marker so the boxes are added once per tray.</summary>
        private class TrayContainerMarker : MonoBehaviour { }

        /// <summary>
        /// Adds container boxes to every object under <paramref name="root"/> whose name
        /// starts with "tray" and that has a MeshFilter. Returns how many trays were fitted.
        /// </summary>
        public static int Ensure(Transform root, float floorThickness = 0.05f, float wallThickness = 0.02f,
                                 float wallExtraHeight = 0.03f, bool log = true)
        {
            if (root == null) return 0;
            int fitted = 0;

            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (!mf.gameObject.name.StartsWith("tray")) continue;
                if (mf.GetComponent<TrayContainerMarker>() != null) continue;

                Transform t = mf.transform;
                Bounds b = mf.sharedMesh.bounds;              // local mesh units
                Vector3 s = t.lossyScale;                     // local -> world factor per axis

                // Thicknesses are wanted in metres; convert to local units per axis.
                float ft = floorThickness / Mathf.Max(1e-4f, Mathf.Abs(s.y));
                float wt = wallThickness / Mathf.Max(1e-4f, Mathf.Abs(s.x));
                float wtz = wallThickness / Mathf.Max(1e-4f, Mathf.Abs(s.z));
                float extra = wallExtraHeight / Mathf.Max(1e-4f, Mathf.Abs(s.y));

                float height = b.size.y + extra;
                float wallCentreY = b.min.y + height * 0.5f;

                // Floor slab: top face flush with the mesh's lowest point.
                AddBox(mf.gameObject, new Vector3(b.center.x, b.min.y - ft * 0.5f, b.center.z),
                       new Vector3(b.size.x, ft, b.size.z));

                // Walls: just inside the outer bounds, rising above the rim.
                AddBox(mf.gameObject, new Vector3(b.min.x + wt * 0.5f, wallCentreY, b.center.z),
                       new Vector3(wt, height, b.size.z));
                AddBox(mf.gameObject, new Vector3(b.max.x - wt * 0.5f, wallCentreY, b.center.z),
                       new Vector3(wt, height, b.size.z));
                AddBox(mf.gameObject, new Vector3(b.center.x, wallCentreY, b.min.z + wtz * 0.5f),
                       new Vector3(b.size.x, height, wtz));
                AddBox(mf.gameObject, new Vector3(b.center.x, wallCentreY, b.max.z - wtz * 0.5f),
                       new Vector3(b.size.x, height, wtz));

                mf.gameObject.AddComponent<TrayContainerMarker>();
                fitted++;

                if (log)
                    Debug.Log($"[TrayColliders] '{mf.gameObject.name}': container boxes added " +
                              $"({b.size.x * s.x:F2} x {b.size.y * s.y:F2} x {b.size.z * s.z:F2} m).", mf);
            }

            return fitted;
        }

        private static void AddBox(GameObject go, Vector3 centre, Vector3 size)
        {
            var box = go.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
        }
    }
}

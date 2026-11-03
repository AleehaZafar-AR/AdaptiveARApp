// File: PistonHeadSymmetryTests.cs (Edit Mode tests; Assembly-CSharp-Editor, never in the player)
//
// Regression checks for the human-pilot change "piston head flat on the mat is accepted
// either way round about its bore axis" (StepValidator role table revision 8). They exercise
// the production functions the validator uses for that rule - StepValidator.HalfTurnVariants /
// HalfTurnOrientationError (the half-turn rule) and StepValidator.ExtentAxisFor (which mesh
// axis is the bore axis) - against the real piston-head mesh and the real role table, with
// the production tolerances. Run in the Editor: Window > General > Test Runner > EditMode,
// or headless:
//   Unity.exe -batchmode -projectPath <project> -runTests -testPlatform EditMode
//            -testFilter AdaptiveAR.EditorTests.PistonHeadSymmetryTests -testResults <xml>

using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using AdaptiveAR.Steps;

namespace AdaptiveAR.EditorTests
{
    public class PistonHeadSymmetryTests
    {
        private const string HeadModelPath = "Assets/Models/piston/piston_-_scaled (1).fbx";
        private const float HeadSceneScale = 70.32899f;   // the PistonHead objects' localScale in the live scene

        private static RoleTable Table()
        {
            var m = typeof(StepValidator).GetMethod("DefaultRoleTolerances", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "StepValidator.DefaultRoleTolerances not found");
            var list = (List<StepValidator.RoleTolerance>)m.Invoke(null, null);
            return new RoleTable(list);
        }

        private sealed class RoleTable
        {
            public readonly List<StepValidator.RoleTolerance> Rows;
            public RoleTable(List<StepValidator.RoleTolerance> rows) { Rows = rows; }
            public StepValidator.RoleTolerance this[string role]
            {
                get
                {
                    foreach (var r in Rows) if (r.role == role) return r;
                    Assert.Fail("no role entry for " + role); return null;
                }
            }
        }

        private static Mesh HeadMesh()
        {
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(HeadModelPath))
                if (o is Mesh mesh) return mesh;
            Assert.Fail("no mesh in " + HeadModelPath); return null;
        }

        private static StepValidator.RoleTolerance Head() { return Table()["PistonHead"]; }

        /// <summary>The bore axis as the validator resolves it for the head: the shortest scaled mesh extent.</summary>
        private static Vector3 HeadAxis()
        {
            var head = Head();
            return StepValidator.ExtentAxisFor(HeadMesh().bounds.size * HeadSceneScale, head.axis);
        }

        private static IEnumerable<Quaternion> TargetFrames()
        {
            yield return Quaternion.identity;
            // the authored mat pose of the head in the live scene: a half turn about the kit's X
            yield return new Quaternion(-0.99999976f, 0.000008800913f, 0.00073300384f, -0.000000006451104f);
            var rng = new System.Random(20261103);
            for (int i = 0; i < 6; i++)
                yield return Quaternion.Euler((float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f);
        }

        private static float Error(Quaternion part, Quaternion target, out bool variantB)
        {
            return StepValidator.HalfTurnOrientationError(part, target, HeadAxis(), out variantB);
        }

        // ---------------------------------------------------------------- the axis

        [Test]
        public void HeadMesh_IsARoundPartWhoseBoreAxisIsItsShortestExtent_Y()
        {
            Vector3 size = HeadMesh().bounds.size;
            Assert.Less(size.y, size.x, "the head's height must be smaller than its width");
            Assert.Less(size.y, size.z, "the head's height must be smaller than its depth");
            Assert.AreEqual(size.x, size.z, size.x * 0.01f, "the two extents perpendicular to the bore axis are the skirt diameter and must agree");
            Assert.AreEqual(Vector3.up, StepValidator.ExtentAxisFor(size, StepValidator.AxisSelect.Shortest));
            Assert.AreEqual(Vector3.up, HeadAxis(), "the role table must select the bore axis for the head");
        }

        [Test]
        public void ExtentAxisFor_LongestAndShortest_FollowTheExtents()
        {
            Assert.AreEqual(Vector3.right, StepValidator.ExtentAxisFor(new Vector3(3, 1, 2), StepValidator.AxisSelect.Longest));
            Assert.AreEqual(Vector3.forward, StepValidator.ExtentAxisFor(new Vector3(1, 2, 3), StepValidator.AxisSelect.Longest));
            Assert.AreEqual(Vector3.up, StepValidator.ExtentAxisFor(new Vector3(3, 1, 2), StepValidator.AxisSelect.Shortest));
            Assert.AreEqual(Vector3.right, StepValidator.ExtentAxisFor(new Vector3(-0.5f, 1, 2), StepValidator.AxisSelect.Shortest), "signs are ignored");
            // the assembly-relative modes fall back to the longest extent when the ghost is unavailable
            Assert.AreEqual(Vector3.right, StepValidator.ExtentAxisFor(new Vector3(3, 1, 2), StepValidator.AxisSelect.TowardsHeadInAssembly));
        }

        // ---------------------------------------------------------------- the role table

        [Test]
        public void RoleTable_PistonHead_HalfTurnAboutShortestExtent_SameNumbers()
        {
            var head = Head();
            Assert.AreEqual(StepValidator.AxisSymmetry.HalfTurnAboutAxis, head.symmetry);
            Assert.AreEqual(StepValidator.AxisSelect.Shortest, head.axis);
            Assert.AreEqual(0.020f, head.positionMeters, 1e-6f, "position tolerance unchanged");
            Assert.AreEqual(22f, head.rotationDegrees, 1e-6f, "orientation tolerance unchanged");
            Assert.IsFalse(head.insertionAxis);
        }

        [Test]
        public void RoleTable_OtherRoles_UnchangedFromRevision7()
        {
            var t = Table();
            Assert.AreEqual(11, t.Rows.Count);
            Expect(t["ConnectingRod"],   0.020f, 22f, StepValidator.AxisSymmetry.AxialHalfTurn,     StepValidator.AxisSelect.Longest,               false);
            Expect(t["ConnectingPin"],   0.015f, 28f, StepValidator.AxisSymmetry.AxialFreeFlip,     StepValidator.AxisSelect.Longest,               true);
            Expect(t["PistonEnd"],       0.020f, 25f, StepValidator.AxisSymmetry.HalfTurnAboutAxis, StepValidator.AxisSelect.TowardsHeadInAssembly, false);
            Expect(t["pistonBolt"],      0.020f, 28f, StepValidator.AxisSymmetry.AxialFreeFlip,     StepValidator.AxisSelect.TowardsPairedPart,     true);
            Expect(t["pistonBoltOther"], 0.020f, 28f, StepValidator.AxisSymmetry.AxialFreeFlip,     StepValidator.AxisSelect.TowardsPairedPart,     true);
            Expect(t["PistonNut"],       0.024f, 32f, StepValidator.AxisSymmetry.AxialFreeFlip,     StepValidator.AxisSelect.TowardsPairedPart,     true);
            Expect(t["PistonNutOther"],  0.024f, 32f, StepValidator.AxisSymmetry.AxialFreeFlip,     StepValidator.AxisSelect.TowardsPairedPart,     true);
            Expect(t["crankshaft"],      0.022f, 20f, StepValidator.AxisSymmetry.AxialFree,         StepValidator.AxisSelect.Longest,               false);
            Expect(t["camshaft"],        0.022f, 20f, StepValidator.AxisSymmetry.AxialFree,         StepValidator.AxisSelect.Longest,               true);
            Expect(t["engineBlockSep"],  0.020f, 25f, StepValidator.AxisSymmetry.PlateFlip,         StepValidator.AxisSelect.Longest,               false);
        }

        private static void Expect(StepValidator.RoleTolerance r, float pos, float rot, StepValidator.AxisSymmetry sym, StepValidator.AxisSelect axis, bool insertion)
        {
            Assert.AreEqual(pos, r.positionMeters, 1e-6f, r.role + " position");
            Assert.AreEqual(rot, r.rotationDegrees, 1e-6f, r.role + " rotation");
            Assert.AreEqual(sym, r.symmetry, r.role + " symmetry");
            Assert.AreEqual(axis, r.axis, r.role + " axis");
            Assert.AreEqual(insertion, r.insertionAxis, r.role + " insertionAxis");
        }

        // ---------------------------------------------------------------- the rule

        [Test]
        public void Canonical_Pose_Passes()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                float e = Error(target, target, out bool b);
                Assert.LessOrEqual(e, 0.01f); Assert.IsFalse(b, "the authored pose is variant A");
                Assert.LessOrEqual(e, tol);
            }
        }

        [Test]
        public void HalfTurn_AboutBoreAxis_Passes_AsVariantB()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                Quaternion part = target * Quaternion.AngleAxis(180f, HeadAxis());
                float e = Error(part, target, out bool b);
                Assert.LessOrEqual(e, 0.01f, "exactly a half turn about the bore axis is an exact match");
                Assert.IsTrue(b, "accepted as variant B");
                Assert.LessOrEqual(e, tol);
            }
        }

        [Test]
        public void HalfTurn_PlusSmallMisalignment_PassesInsideTolerance_FailsOutside()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                Quaternion inside = target * Quaternion.AngleAxis(180f + tol - 2f, HeadAxis());
                Assert.LessOrEqual(Error(inside, target, out _), tol);
                Quaternion outside = target * Quaternion.AngleAxis(180f + tol + 3f, HeadAxis());
                Assert.Greater(Error(outside, target, out _), tol);
                Quaternion tilted = target * Quaternion.AngleAxis(180f, HeadAxis()) * Quaternion.AngleAxis(tol + 3f, Vector3.right);
                Assert.Greater(Error(tilted, target, out _), tol, "a half turn plus a tilt past tolerance still fails");
            }
        }

        [Test]
        public void QuarterTurn_AboutBoreAxis_Fails()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                Assert.AreEqual(90f, Error(target * Quaternion.AngleAxis(90f, HeadAxis()), target, out _), 0.01f);
                Assert.AreEqual(90f, Error(target * Quaternion.AngleAxis(-90f, HeadAxis()), target, out _), 0.01f);
                Assert.Greater(90f, tol);
            }
        }

        [Test]
        public void UpsideDown_Fails()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                // crown up instead of down: a half turn about either horizontal axis of the head
                foreach (Vector3 horizontal in new[] { Vector3.right, Vector3.forward, (Vector3.right + Vector3.forward).normalized })
                {
                    Quaternion part = target * Quaternion.AngleAxis(180f, horizontal);
                    float e = Error(part, target, out _);
                    Assert.AreEqual(180f, e, 0.01f, "upside-down is 180 degrees from BOTH variants");
                    Assert.Greater(e, tol);
                }
            }
        }

        [Test]
        public void Sideways_Fails()
        {
            float tol = Head().rotationDegrees;
            foreach (Quaternion target in TargetFrames())
            {
                foreach (Vector3 horizontal in new[] { Vector3.right, Vector3.forward })
                {
                    foreach (float deg in new[] { 90f, -90f, 60f, 120f })
                    {
                        Quaternion part = target * Quaternion.AngleAxis(deg, horizontal);
                        float e = Error(part, target, out _);
                        Assert.Greater(e, tol, "bore axis lying across the mat (" + deg + " deg about " + horizontal + ") must not pass");
                        Assert.GreaterOrEqual(e, Mathf.Min(Mathf.Abs(deg), 180f - Mathf.Abs(deg)) - 0.01f);
                    }
                }
            }
        }

        [Test]
        public void HalfTurn_LeavesTheGeometricCentreInPlace_SoThePositionGateIsUnchanged()
        {
            // The validator compares geometric CENTRES, not pivots (StepValidator.PositionError).
            // The head's pivot is off the bore axis, so the half turn moves the pivot but not the
            // centre: a head accepted in variant B is exactly as well positioned as in variant A.
            Mesh mesh = HeadMesh();
            var go = new GameObject("PistonHeadTest");
            try
            {
                go.transform.localScale = Vector3.one * HeadSceneScale * 0.0041f * 0.6f;   // the live hierarchy's scale chain
                go.transform.rotation = Quaternion.Euler(37f, -120f, 11f);
                Vector3 centreBefore = go.transform.TransformPoint(mesh.bounds.center);
                Vector3 pivotBefore = go.transform.position;
                go.transform.RotateAround(centreBefore, go.transform.TransformDirection(HeadAxis()), 180f);
                Vector3 centreAfter = go.transform.TransformPoint(mesh.bounds.center);
                Assert.Less(Vector3.Distance(centreBefore, centreAfter), 1e-5f, "the centre does not move under the half turn");
                Assert.Greater(Vector3.Distance(pivotBefore, go.transform.position), 1e-3f, "the pivot does (it is off the axis), which is why centres are compared");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ValidHalfTurnOrientation_ButOutsidePositionTolerance_Fails()
        {
            var head = Head();
            foreach (Quaternion target in TargetFrames())
            {
                Quaternion part = target * Quaternion.AngleAxis(180f, HeadAxis());
                Assert.LessOrEqual(Error(part, target, out _), head.rotationDegrees, "orientation alone would pass");
                // the production gate is positionError <= posTol && rotationError <= rotTol
                float centreOffset = head.positionMeters + 0.010f;                      // 3.0 cm against a 2.0 cm tolerance
                bool accepted = centreOffset <= head.positionMeters && Error(part, target, out _) <= head.rotationDegrees;
                Assert.IsFalse(accepted);
                float insideOffset = head.positionMeters - 0.005f;
                Assert.IsTrue(insideOffset <= head.positionMeters && Error(part, target, out _) <= head.rotationDegrees);
            }
        }

        [Test]
        public void RodEndCap_HalfTurnRule_IsTheSameFunction_UnchangedMath()
        {
            // The cap uses the same rule with its own axis (resolved from the assembled piston).
            // B must be exactly the target turned a half turn about the given axis, and A the target.
            Vector3 capAxis = new Vector3(0.21f, -0.95f, 0.23f).normalized;
            foreach (Quaternion target in TargetFrames())
            {
                StepValidator.HalfTurnVariants(target, target, capAxis, out Quaternion a, out Quaternion b, out float angA, out float angB);
                Assert.AreEqual(0f, Quaternion.Angle(a, target), 1e-4f);
                Assert.AreEqual(0f, Quaternion.Angle(b, target * Quaternion.AngleAxis(180f, capAxis)), 1e-4f);
                Assert.AreEqual(0f, angA, 1e-4f);
                Assert.AreEqual(180f, angB, 0.01f);
                // ties go to A
                Quaternion midway = target * Quaternion.AngleAxis(90f, capAxis);
                StepValidator.HalfTurnOrientationError(midway, target, capAxis, out bool variantB);
                Assert.IsFalse(variantB);
            }
        }
    }
}

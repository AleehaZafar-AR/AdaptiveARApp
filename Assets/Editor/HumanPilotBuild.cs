// File: HumanPilotBuild.cs (Editor only)
//
// Reproducible build of the human-pilot study APK. It builds with the committed build
// profile Assets/Settings/Build Profiles/HumanPilot.asset, which has
//   - NO Player Settings overrides: the global ProjectSettings apply (product
//     AdaptiveARPrototype, com.vuforia.engine.VuforiaEngine, IL2CPP, ARM64, OpenXR);
//     the Quest3 profile's uncommitted override carries a "TechnicalValidation" product
//     name for validation builds and must not label the study app;
//   - the one live scene (Assets/1 - ArUcoMarkerTracking.unity);
//   - ONE extra scripting define, ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION, which compiles
//     the Level 2 technical-validation bootstrap out of the player, so no
//     technical_validation.json left on a headset can ever start the runner in a
//     participant session.
// Nothing in ProjectSettings is modified and the active build profile is left alone.
//
// Editor:   AdaptiveAR > Build > Human pilot APK  (writes Builds/AdaptiveAR_HumanPilot_Final.apk)
// Headless: Unity.exe -batchmode -nographics -projectPath <project> -buildTarget Android
//                     -executeMethod AdaptiveAR.EditorTools.HumanPilotBuild.Build
//                     [-apkPath <file.apk>] -logFile <log> -quit

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AdaptiveAR.EditorTools
{
    public static class HumanPilotBuild
    {
        public const string Scene = "Assets/1 - ArUcoMarkerTracking.unity";
        public const string ProfilePath = "Assets/Settings/Build Profiles/HumanPilot.asset";
        public const string DisableTechnicalValidationDefine = "ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION";
        public const string DefaultApkName = "AdaptiveAR_HumanPilot_Final.apk";

        [MenuItem("AdaptiveAR/Build/Human pilot APK")]
        public static void BuildFromMenu()
        {
            BuildReport report = Build();
            if (report != null && report.summary.result == BuildResult.Succeeded)
                EditorUtility.RevealInFinder(report.summary.outputPath);
        }

        public static BuildReport Build()
        {
            string apk = ApkPathFromCommandLine() ?? Path.Combine(ProjectRoot(), "Builds", DefaultApkName);
            Directory.CreateDirectory(Path.GetDirectoryName(apk));

            if (!File.Exists(Scene)) throw new FileNotFoundException("live scene missing: " + Scene);
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
            if (profile == null) throw new FileNotFoundException("human-pilot build profile missing: " + ProfilePath);
            if (profile.scriptingDefines == null || Array.IndexOf(profile.scriptingDefines, DisableTechnicalValidationDefine) < 0)
                throw new InvalidOperationException(ProfilePath + " must carry the scripting define " + DisableTechnicalValidationDefine);

            var header = new StringBuilder();
            header.AppendLine("[HumanPilotBuild] profile: " + ProfilePath + " (no Player Settings overrides; global ProjectSettings apply)");
            header.AppendLine("[HumanPilotBuild] scene: " + Scene);
            header.AppendLine("[HumanPilotBuild] output: " + apk);
            header.AppendLine("[HumanPilotBuild] profile defines: " + string.Join(";", profile.scriptingDefines));
            header.AppendLine("[HumanPilotBuild] global product: " + PlayerSettings.productName + " " + PlayerSettings.bundleVersion
                              + " (" + PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android) + ", versionCode " + PlayerSettings.Android.bundleVersionCode + ")"
                              + " - as read with the currently active profile; the APK's label is checked with aapt after the build");
            header.AppendLine("[HumanPilotBuild] scripting backend: " + PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)
                              + ", architectures: " + PlayerSettings.Android.targetArchitectures
                              + ", min/target SDK: " + PlayerSettings.Android.minSdkVersion + "/" + PlayerSettings.Android.targetSdkVersion);
            header.AppendLine("[HumanPilotBuild] Unity " + Application.unityVersion + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            Debug.Log(header.ToString());

            var options = new BuildPlayerWithProfileOptions
            {
                buildProfile = profile,
                locationPathName = apk,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            string line = "[HumanPilotBuild] result: " + s.result + ", " + s.totalErrors + " error(s), " + s.totalWarnings + " warning(s), "
                          + s.totalSize + " bytes, " + s.totalTime.TotalSeconds.ToString("F0") + " s";
            if (s.result == BuildResult.Succeeded)
            {
                Debug.Log(line);
                try
                {
                    File.WriteAllText(Path.ChangeExtension(apk, ".build.txt"), header + line + Environment.NewLine
                                      + "apk size: " + new FileInfo(apk).Length + " bytes" + Environment.NewLine);
                }
                catch (Exception e) { Debug.LogWarning("[HumanPilotBuild] could not write the build note: " + e.Message); }
            }
            else
            {
                Debug.LogError(line);
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
            return report;
        }

        private static string ApkPathFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-apkPath", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(args[i + 1]);
            return null;
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }
}

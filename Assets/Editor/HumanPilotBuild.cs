// File: HumanPilotBuild.cs (Editor only)
//
// Reproducible build of the human-pilot study APK. It builds the one live scene with the
// project's global Player Settings (product AdaptiveARPrototype, IL2CPP, ARM64, OpenXR) and
// adds ONE scripting define for this build only - ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION -
// which compiles the Level 2 technical-validation bootstrap out of the player, so no
// technical_validation.json left on a headset can ever start the runner in a participant
// session. Nothing in ProjectSettings is modified; the define is passed through
// BuildPlayerOptions.extraScriptingDefines.
//
// Editor:   AdaptiveAR > Build > Human pilot APK  (writes Builds/AdaptiveAR_HumanPilot_Final.apk)
// Headless: Unity.exe -batchmode -nographics -projectPath <project> -buildTarget Android
//                     -executeMethod AdaptiveAR.EditorTools.HumanPilotBuild.Build
//                     [-apkPath <file.apk>] -logFile <log> -quit

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AdaptiveAR.EditorTools
{
    public static class HumanPilotBuild
    {
        public const string Scene = "Assets/1 - ArUcoMarkerTracking.unity";
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

            // Classic Android player settings: a plain APK, not an app bundle or a Gradle project.
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { DisableTechnicalValidationDefine }
            };

            var header = new StringBuilder();
            header.AppendLine("[HumanPilotBuild] scene: " + Scene);
            header.AppendLine("[HumanPilotBuild] output: " + apk);
            header.AppendLine("[HumanPilotBuild] product: " + PlayerSettings.productName + " " + PlayerSettings.bundleVersion
                              + " (" + PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android) + ", versionCode " + PlayerSettings.Android.bundleVersionCode + ")");
            header.AppendLine("[HumanPilotBuild] scripting backend: " + PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)
                              + ", architectures: " + PlayerSettings.Android.targetArchitectures
                              + ", min/target SDK: " + PlayerSettings.Android.minSdkVersion + "/" + PlayerSettings.Android.targetSdkVersion);
            header.AppendLine("[HumanPilotBuild] project defines (Android): " + PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Android));
            header.AppendLine("[HumanPilotBuild] extra define for this build: " + DisableTechnicalValidationDefine);
            header.AppendLine("[HumanPilotBuild] Unity " + Application.unityVersion + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            Debug.Log(header.ToString());

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

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Management;

namespace ThetaProjection.EditorTools
{
    /// <summary>
    /// Quest 3 向けビルドに必要なプロジェクト設定を一括適用するメニュー。
    /// 使い方: メニュー「THETA」→「1. Setup Project Settings」→「2. Create Viewer Scene」
    /// </summary>
    public static class ThetaProjectSetup
    {
        private const string ScenePath = "Assets/Scenes/ThetaViewer.unity";

        [MenuItem("THETA/1. Setup Project Settings (Android + OpenXR)")]
        public static void SetupProject()
        {
            // --- ビルドターゲット ---
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                if (!switched)
                {
                    Debug.LogError("[THETA Setup] Android へのプラットフォーム切替に失敗しました。" +
                                   "Unity Hub で Android Build Support (SDK/NDK/OpenJDK) を追加してください。");
                    return;
                }
            }

            // --- Player 設定 ---
            // Quest (OpenXR) は Gamma 非対応。Linear は GLES3/Vulkan が必須
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // RTSP 再生 (SurfaceTexture の外部テクスチャ共有) は GLES 専用のため GLES3 のみにする
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3
            });
            // Java 側 GL 呼び出しを Unity の GL コンテキストで行うため MT レンダリングは無効
            PlayerSettings.SetMobileMTRendering(BuildTargetGroup.Android, false);
            PlayerSettings.productName = "THETA Live Viewer";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.example.thetaliveviewer");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.forceInternetPermission = true;      // Web API 用
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // THETA は http://
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

            SetActiveInputHandlerToBoth();
            EnableOpenXRForAndroid();

            AssetDatabase.SaveAssets();
            Debug.Log("[THETA Setup] プロジェクト設定を適用しました。" +
                      "Input System の変更を反映するためエディタの再起動を求められた場合は再起動してください。");
        }

        [MenuItem("THETA/2. Create Viewer Scene")]
        public static void CreateViewerScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            var viewer = new GameObject("THETA Viewer");
            viewer.AddComponent<ThetaLivePreview>();
            viewer.AddComponent<RtspStreamPlayer>();
            viewer.AddComponent<ThetaViewerBootstrap>();

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log($"[THETA Setup] シーンを作成しました: {ScenePath} (Build Settings にも登録済み)");
        }

        [MenuItem("THETA/3. Build APK")]
        public static void BuildApk()
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/ThetaLiveViewer.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[THETA Setup] Build result: {report.summary.result} " +
                      $"(errors: {report.summary.totalErrors}, size: {report.summary.totalSize / (1024 * 1024)} MB)");
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new Exception($"APK build failed: {report.summary.totalErrors} error(s)");
        }

        /// <summary>
        /// Norton 等の HTTPS 検査ソフトが入った PC 向けの回避策。
        /// Norton のルート CA を cacerts に追加した JDK のコピー (C:\dev\UnityOpenJDK) を
        /// Android ビルドの JDK として使わせる。該当 PC でのみ実行すればよい。
        /// (Unity は Gradle 起動時に jvmargs や JAVA_TOOL_OPTIONS を上書きするため、
        ///  truststore を差し替えるにはこの方法が確実)
        /// </summary>
        [MenuItem("THETA/4. Use Patched JDK (HTTPS inspection workaround)")]
        public static void UsePatchedJdk()
        {
            // Unity 6 同梱の OpenJDK 17 のコピーに Norton のルート CA を追加したもの。
            // Unity は Gradle 起動時に jvmargs / JAVA_TOOL_OPTIONS を上書きするため、
            // truststore を差し替えるには cacerts へ直接追加した JDK を使うのが確実。
            const string jdkPath = @"C:\dev\UnityOpenJDK17";
            if (!System.IO.Directory.Exists(jdkPath))
            {
                Debug.LogError($"[THETA Setup] {jdkPath} がありません。README の手順で JDK のコピーと証明書追加を先に行ってください。");
                return;
            }
            UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = jdkPath;
            Debug.Log($"[THETA Setup] Android ビルド用 JDK を {jdkPath} に変更しました。");

            // SDK は Unity 6 同梱のもの (android-34+) をそのまま使う。
            // 2022.3 時代に設定した C:\dev\UnityAndroidSDK (android-33 まで) が
            // EditorPrefs に残っていると compileSdk 不足でビルドできないため明示的に戻す。
            UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = string.Empty;
            Debug.Log("[THETA Setup] Android SDK を Unity 同梱のものに戻しました。");
        }

        /// <summary>Active Input Handling を "Both" にする(TextMesh 等の旧 API と Input System を併用)。</summary>
        private static void SetActiveInputHandlerToBoth()
        {
            try
            {
                var playerSettings = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
                var so = new SerializedObject(playerSettings);
                var prop = so.FindProperty("activeInputHandler");
                if (prop != null && prop.intValue != 2)
                {
                    prop.intValue = 2; // 0: Old, 1: Input System, 2: Both
                    so.ApplyModifiedProperties();
                    Debug.Log("[THETA Setup] Active Input Handling を Both に設定しました(エディタ再起動後に有効)。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[THETA Setup] Active Input Handling の自動設定に失敗しました。" +
                                 "Project Settings > Player > Other Settings > Active Input Handling を 'Both' にしてください。\n" + e);
            }
        }

        /// <summary>Android 向けに OpenXR ローダーと Meta Quest Support 機能を有効化する。</summary>
        private static void EnableOpenXRForAndroid()
        {
            try
            {
                XRGeneralSettingsPerBuildTarget perBuildTarget;
                if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perBuildTarget) ||
                    perBuildTarget == null)
                {
                    perBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    if (!AssetDatabase.IsValidFolder("Assets/XR"))
                        AssetDatabase.CreateFolder("Assets", "XR");
                    AssetDatabase.CreateAsset(perBuildTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                    EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBuildTarget, true);
                }

                var settings = perBuildTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
                if (settings == null)
                {
                    settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                    var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                    settings.Manager = manager;
                    AssetDatabase.AddObjectToAsset(settings, perBuildTarget);
                    AssetDatabase.AddObjectToAsset(manager, perBuildTarget);
                    perBuildTarget.SetSettingsForBuildTarget(BuildTargetGroup.Android, settings);
                    AssetDatabase.SaveAssets();
                }

                bool assigned = XRPackageMetadataStore.AssignLoader(
                    settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android);
                if (!assigned)
                    Debug.LogWarning("[THETA Setup] OpenXR ローダーの割り当てに失敗しました。" +
                                     "Project Settings > XR Plug-in Management > Android タブで OpenXR にチェックを入れてください。");

                // OpenXR 機能: Meta Quest Support と Oculus Touch プロファイル
                FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
                EnableOpenXRFeature("com.unity.openxr.feature.metaquest", "Meta Quest Support");
                EnableOpenXRFeature("com.unity.openxr.feature.input.oculustouch", "Oculus Touch Controller Profile");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[THETA Setup] XR 設定の自動化に失敗しました。手動で以下を設定してください:\n" +
                                 "1. Project Settings > XR Plug-in Management > Android タブ > OpenXR にチェック\n" +
                                 "2. その下の OpenXR 設定で 'Meta Quest Support' を有効化\n" + e);
            }
        }

        private static void EnableOpenXRFeature(string featureId, string displayName)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, featureId);
            if (feature != null)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                Debug.Log($"[THETA Setup] OpenXR 機能を有効化: {displayName}");
            }
            else
            {
                Debug.LogWarning($"[THETA Setup] OpenXR 機能が見つかりません: {displayName} ({featureId})。" +
                                 "Project Settings > XR Plug-in Management > OpenXR で手動で有効化してください。");
            }
        }
    }
}

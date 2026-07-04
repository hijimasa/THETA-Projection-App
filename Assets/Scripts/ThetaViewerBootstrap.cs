using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace ThetaProjection
{
    /// <summary>
    /// シーンの配線役。映像ソースの選択・カメラへの HMD トラッキング追加・
    /// 投影球の生成・ステータス表示をまとめて行う。
    /// シーンには「Main Camera」と、このコンポーネントを付けた GameObject があればよい
    /// (ソースコンポーネントが無ければ自動で追加する)。
    /// </summary>
    public sealed class ThetaViewerBootstrap : MonoBehaviour
    {
        public enum VideoSourceMode
        {
            /// <summary>実機では RTSP (高画質)、エディタでは Web API を使う</summary>
            Auto,
            /// <summary>Web API getLivePreview (MJPEG, 低画質・プラグイン不要)</summary>
            WebApi,
            /// <summary>RTSP プラグイン (H.264, 高画質・要 THETA RTSP Streaming プラグイン)</summary>
            Rtsp,
        }

        [Header("映像ソース")]
        public VideoSourceMode sourceMode = VideoSourceMode.Auto;

        [Tooltip("未指定ならシーン内から検索し、無ければ自動生成する")]
        public SphereProjector sphere;

        private ILiveVideoSource _source;
        private TextMesh _statusText;
        private Transform _cameraTransform;

#if UNITY_EDITOR
        private Vector2 _editorLookAngles;
#endif

        private void Awake()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.transform.position = Vector3.zero;
            _cameraTransform = cam.transform;

            EnsureHeadTracking(cam);

            if (sphere == null)
                sphere = FindFirstObjectByType<SphereProjector>();
            if (sphere == null)
                sphere = new GameObject("Projection Sphere").AddComponent<SphereProjector>();

            _source = SelectSource();
            _source.TextureCreated += sphere.SetTexture;

            // 映像の左右向きはソース経路で異なる:
            //   Web API (Texture2D.LoadImage) → 反転が必要
            //   RTSP (SurfaceTexture 経由)    → そのままが正
            sphere.SetFlipHorizontal(_source is ThetaLivePreview);

            CreateStatusText(cam.transform);
        }

        /// <summary>設定に応じて映像ソースを 1 つだけ有効にする。</summary>
        private ILiveVideoSource SelectSource()
        {
            bool useRtsp;
            switch (sourceMode)
            {
                case VideoSourceMode.WebApi:
                    useRtsp = false;
                    break;
                case VideoSourceMode.Rtsp:
                    useRtsp = true;
                    break;
                default: // Auto
#if UNITY_ANDROID && !UNITY_EDITOR
                    useRtsp = true;
#else
                    useRtsp = false;
#endif
                    break;
            }

            var webApi = GetComponent<ThetaLivePreview>();
            var rtsp = GetComponent<RtspStreamPlayer>();

            if (useRtsp)
            {
                if (webApi != null)
                    webApi.enabled = false;
                if (rtsp == null)
                    rtsp = gameObject.AddComponent<RtspStreamPlayer>();
                rtsp.enabled = true;
                return rtsp;
            }

            if (rtsp != null)
                rtsp.enabled = false;
            if (webApi == null)
                webApi = gameObject.AddComponent<ThetaLivePreview>();
            webApi.enabled = true;
            return webApi;
        }

        /// <summary>HMD の姿勢をカメラに反映する TrackedPoseDriver を追加する。</summary>
        private static void EnsureHeadTracking(Camera cam)
        {
            if (cam.GetComponent<TrackedPoseDriver>() != null)
                return;

            var driver = cam.gameObject.AddComponent<TrackedPoseDriver>();
            var position = new InputAction(binding: "<XRHMD>/centerEyePosition");
            var rotation = new InputAction(binding: "<XRHMD>/centerEyeRotation");
            driver.positionInput = new InputActionProperty(position);
            driver.rotationInput = new InputActionProperty(rotation);
            position.Enable();
            rotation.Enable();
        }

        private void CreateStatusText(Transform cameraTransform)
        {
            var go = new GameObject("Status Text");
            go.transform.SetParent(cameraTransform, false);
            go.transform.localPosition = new Vector3(0f, -0.35f, 1.5f);
            go.transform.localScale = Vector3.one * 0.02f;

            _statusText = go.AddComponent<TextMesh>();
            _statusText.anchor = TextAnchor.MiddleCenter;
            _statusText.alignment = TextAlignment.Center;
            _statusText.fontSize = 48;
            _statusText.characterSize = 0.5f;
            _statusText.color = Color.white;
        }

        private void Update()
        {
            if (_source.IsStreaming)
            {
                // 配信中は何も表示しない (接続状態の表示は切断時のみ)
                _statusText.text = string.Empty;
            }
            else
            {
                _statusText.text = _source.StatusText +
                    "\n\nConnect Quest Wi-Fi to THETA (THETAYL*.OSC)";
                _statusText.color = Color.white;
            }

#if UNITY_EDITOR
            UpdateEditorMouseLook();
#endif
        }

#if UNITY_EDITOR
        /// <summary>エディタ再生時のみ: 右ドラッグで見回し(実機では HMD が姿勢を制御)。</summary>
        private void UpdateEditorMouseLook()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.isPressed)
                return;
            var delta = mouse.delta.ReadValue();
            _editorLookAngles.x += delta.x * 0.15f;
            _editorLookAngles.y = Mathf.Clamp(_editorLookAngles.y - delta.y * 0.15f, -89f, 89f);
            _cameraTransform.localRotation = Quaternion.Euler(_editorLookAngles.y, _editorLookAngles.x, 0f);
        }
#endif
    }
}

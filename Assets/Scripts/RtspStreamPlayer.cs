using System;
using UnityEngine;

namespace ThetaProjection
{
    /// <summary>
    /// THETA の RTSP プラグイン (THETA RTSP Streaming) からの H.264 ストリームを
    /// libVLC (Java ブリッジ com.thetaprojection.rtsp.ThetaRtspPlayer) で再生し、
    /// GL テクスチャとして球面投影へ渡すコンポーネント。
    /// ※ExoPlayer は SDP に sprop-parameter-sets が無いストリームを再生できないため libVLC を使用。
    ///
    /// Android 実機専用 (エディタでは Web API ソースを使うこと)。
    /// グラフィックス API は OpenGL ES 3、Multithreaded Rendering 無効が前提。
    /// </summary>
    public sealed class RtspStreamPlayer : MonoBehaviour, ILiveVideoSource
    {
        [Header("RTSP 接続設定 (THETA RTSP Streaming プラグイン)")]
        [Tooltip("resolution は 640x320 / 1024x512 / 1920x960 / 3840x1920")]
        public string url = "rtsp://192.168.1.1:8554/live?resolution=1920x960";

        [Header("受信テクスチャサイズ (URL の resolution と合わせる)")]
        public int textureWidth = 1920;
        public int textureHeight = 960;

        [Tooltip("エラー時に再接続を試みるまでの秒数。短すぎると THETA 側プラグインに負荷がかかる")]
        public float reconnectDelaySeconds = 5f;

        [Tooltip("RTP を TCP (RTSP 接続への多重化) で受信する。THETA の RTSP プラグインは UDP のみ対応で、" +
                 "TCP を要求するとデータが流れずプラグインが異常終了するため通常はオフのこと")]
        public bool useTcpTransport = false;

        public event Action<Texture> TextureCreated;
        public string StatusText { get; private set; } = "Idle";
        public bool IsStreaming { get; private set; }
        public int FramesPerSecond { get; private set; }

        private Texture2D _texture;
        private int _fpsCounter;
        private float _fpsTimer;
        private float _reconnectTimer;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _player;
        private AndroidJavaObject _activity;

        private void Start()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                }

                _player = new AndroidJavaObject("com.thetaprojection.rtsp.ThetaRtspPlayer");

                // GL リソース生成 (Unity メインスレッド = GL コンテキストがある前提)
                if (!_player.Call<bool>("initGL", textureWidth, textureHeight))
                {
                    StatusText = "GL init failed: " + _player.Call<string>("getError");
                    enabled = false;
                    return;
                }

                int texId = _player.Call<int>("getTargetTexture");
                _texture = Texture2D.CreateExternalTexture(
                    textureWidth, textureHeight, TextureFormat.RGBA32, false, false, (IntPtr)texId);
                TextureCreated?.Invoke(_texture);

                StatusText = "Connecting (RTSP)...";
                _player.Call("start", _activity, url, useTcpTransport);
            }
            catch (Exception e)
            {
                StatusText = "RTSP bridge error: " + e.Message;
                Debug.LogError($"[RtspStreamPlayer] {e}");
                enabled = false;
            }
        }

        private void Update()
        {
            if (_player == null)
                return;

            if (_player.Call<bool>("updateFrame"))
            {
                // Java 側の blit で GL 状態 (テクスチャバインド等) が変わるため、
                // Unity のキャッシュ済み GL 状態を無効化して再バインドさせる
                GL.InvalidateState();
                IsStreaming = true;
                _fpsCounter++;
            }

            UpdateFpsCounter();

            string state = _player.Call<string>("getState");
            switch (state)
            {
                case "playing":
                    StatusText = "Streaming (RTSP)";
                    break;
                case "buffering":
                case "connecting":
                    StatusText = "Connecting (RTSP)...";
                    break;
                case "error":
                case "ended":
                    IsStreaming = false;
                    StatusText = $"Disconnected: {_player.Call<string>("getError")} (retrying...)";
                    _reconnectTimer += Time.deltaTime;
                    if (_reconnectTimer >= reconnectDelaySeconds)
                    {
                        _reconnectTimer = 0f;
                        _player.Call("stop");
                        _player.Call("start", _activity, url, useTcpTransport);
                    }
                    break;
            }
        }

        private void OnDestroy()
        {
            if (_player != null)
            {
                _player.Call("release");
                _player.Dispose();
                _player = null;
            }
        }
#else
        private void Start()
        {
            StatusText = "RTSP source is Android-only. Use WebApi source in the Editor.";
        }

        private void Update()
        {
            UpdateFpsCounter();
        }
#endif

        private void UpdateFpsCounter()
        {
            _fpsTimer += Time.deltaTime;
            if (_fpsTimer >= 1f)
            {
                FramesPerSecond = _fpsCounter;
                _fpsCounter = 0;
                _fpsTimer = 0f;
            }
        }
    }
}

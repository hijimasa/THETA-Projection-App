using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ThetaProjection
{
    /// <summary>
    /// THETA の getLivePreview (MotionJPEG) をバックグラウンドで受信し続け、
    /// 最新フレームを Texture2D としてメインスレッドに公開するコンポーネント。
    /// 切断時は自動で再接続する。
    /// </summary>
    public sealed class ThetaLivePreview : MonoBehaviour, ILiveVideoSource
    {
        [Header("THETA 接続設定")]
        [Tooltip("AP モードの THETA は 192.168.1.1 固定")]
        public string cameraHost = "192.168.1.1";

        [Header("プレビュー形式 (THETA V: 1024x512@30 / 1920x960@8 / 640x320@30)")]
        public int previewWidth = 1024;
        public int previewHeight = 512;
        public int previewFramerate = 30;

        [Tooltip("切断時に再接続を試みるまでの秒数")]
        public float reconnectDelaySeconds = 2f;

        /// <summary>受信映像が書き込まれるテクスチャ。最初のフレーム受信時に生成される。</summary>
        public Texture2D Texture { get; private set; }

        /// <summary>テクスチャが生成されたときに一度呼ばれる(マテリアルへの割り当て用)。</summary>
        public event Action<Texture> TextureCreated;

        /// <summary>現在の接続状態(表示用・英語)。ワーカースレッドから更新される。</summary>
        public string StatusText => _status;

        /// <summary>直近 1 秒間に描画したフレーム数。</summary>
        public int FramesPerSecond { get; private set; }

        public bool IsStreaming { get; private set; }

        private volatile string _status = "Idle";
        private byte[] _pendingFrame;               // ワーカー → メインスレッドの受け渡し(常に最新のみ保持)
        private CancellationTokenSource _cts;
        private Task _workerTask;

        private int _fpsCounter;
        private float _fpsTimer;

        private void OnEnable()
        {
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => StreamLoopAsync(_cts.Token));
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            IsStreaming = false;
        }

        private void Update()
        {
            var frame = Interlocked.Exchange(ref _pendingFrame, null);
            if (frame != null)
            {
                bool created = false;
                if (Texture == null)
                {
                    // LoadImage が実サイズにリサイズするので初期サイズは仮でよい
                    Texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    Texture.wrapMode = TextureWrapMode.Repeat;   // 経度方向の継ぎ目対策
                    created = true;
                }
                if (Texture.LoadImage(frame))
                {
                    IsStreaming = true;
                    _fpsCounter++;
                    if (created)
                        TextureCreated?.Invoke(Texture);
                }
            }

            _fpsTimer += UnityEngine.Time.deltaTime;
            if (_fpsTimer >= 1f)
            {
                FramesPerSecond = _fpsCounter;
                _fpsCounter = 0;
                _fpsTimer = 0f;
            }
        }

        private async Task StreamLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var api = new ThetaWebApi(cameraHost))
                    {
                        _status = $"Connecting to THETA ({cameraHost})...";
                        await api.GetInfoAsync(ct);

                        // プレビュー形式の設定。動画モード時などは失敗するが致命的ではない
                        try
                        {
                            await api.SetPreviewFormatAsync(previewWidth, previewHeight, previewFramerate, ct);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[ThetaLivePreview] previewFormat setting skipped: {e.Message}");
                        }

                        _status = "Starting live preview...";
                        using (var raw = await api.OpenLivePreviewAsync(ct))
                        using (var stream = new BufferedStream(raw, 128 * 1024))
                        {
                            var reader = new MjpegStreamReader(stream);
                            _status = "Streaming";
                            while (!ct.IsCancellationRequested)
                            {
                                byte[] frame = reader.ReadFrame();
                                if (frame == null)
                                    throw new IOException("Preview stream ended");
                                // 描画が追いつかない場合は古いフレームを捨てて常に最新を保持
                                Interlocked.Exchange(ref _pendingFrame, frame);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    IsStreaming = false;
                    _status = $"Disconnected: {Shorten(e.Message)} (retrying...)";
                    Debug.LogWarning($"[ThetaLivePreview] {e.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            _status = "Stopped";
        }

        private static string Shorten(string s)
        {
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }
    }
}

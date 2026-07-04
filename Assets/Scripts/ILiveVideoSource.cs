using System;
using UnityEngine;

namespace ThetaProjection
{
    /// <summary>
    /// ライブ映像ソース (Web API MJPEG / RTSP) の共通インターフェース。
    /// ThetaViewerBootstrap はこれ経由でソースを扱う。
    /// </summary>
    public interface ILiveVideoSource
    {
        /// <summary>映像テクスチャが用意できたときに一度呼ばれる。</summary>
        event Action<Texture> TextureCreated;

        /// <summary>表示用の状態文字列(英語)。</summary>
        string StatusText { get; }

        /// <summary>映像を受信中かどうか。</summary>
        bool IsStreaming { get; }

        /// <summary>直近 1 秒間の描画フレーム数。</summary>
        int FramesPerSecond { get; }
    }
}

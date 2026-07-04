using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThetaProjection
{
    /// <summary>
    /// RICOH THETA Web API (OSC API v2.1) の最小クライアント。
    /// THETA をアクセスポイントモード (AP モード) で使う前提。認証(Digest)は
    /// クライアントモード専用のため未対応。
    /// </summary>
    public sealed class ThetaWebApi : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public ThetaWebApi(string host = "192.168.1.1", int port = 80)
        {
            _baseUrl = $"http://{host}:{port}";
            // ライブプレビューは無限に続くストリームなので HttpClient 全体の
            // タイムアウトは無効化し、個別リクエストで制御する。
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>GET /osc/info — 疎通確認とモデル名取得に使う。</summary>
        public async Task<string> GetInfoAsync(CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var res = await _http.GetAsync($"{_baseUrl}/osc/info", cts.Token);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new IOException($"/osc/info failed: HTTP {(int)res.StatusCode} {body}");
                return body;
            }
        }

        /// <summary>POST /osc/commands/execute — 単発コマンドの実行。</summary>
        public async Task<string> ExecuteAsync(string commandJson, CancellationToken ct, int timeoutSeconds = 10)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var content = new StringContent(commandJson, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync($"{_baseUrl}/osc/commands/execute", content, cts.Token);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new IOException($"THETA command failed: HTTP {(int)res.StatusCode} {body}");
                return body;
            }
        }

        /// <summary>
        /// ライブプレビューの解像度/フレームレートを設定する。
        /// THETA V で選べる主な組み合わせ: 1024x512@30fps, 1920x960@8fps, 640x320@30fps。
        /// 静止画モード以外ではエラーになることがあるので呼び出し側で握りつぶしてよい。
        /// </summary>
        public Task<string> SetPreviewFormatAsync(int width, int height, int framerate, CancellationToken ct)
        {
            string json = "{\"name\":\"camera.setOptions\",\"parameters\":{\"options\":{\"previewFormat\":{"
                        + $"\"width\":{width},\"height\":{height},\"framerate\":{framerate}"
                        + "}}}}";
            return ExecuteAsync(json, ct);
        }

        /// <summary>
        /// camera.getLivePreview を実行し、multipart/x-mixed-replace (MotionJPEG) の
        /// レスポンスストリームを開いて返す。ストリームは呼び出し側が Dispose すること。
        /// </summary>
        public async Task<Stream> OpenLivePreviewAsync(CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/osc/commands/execute")
            {
                Content = new StringContent("{\"name\":\"camera.getLivePreview\"}", Encoding.UTF8, "application/json")
            };
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                throw new IOException($"getLivePreview failed: HTTP {(int)res.StatusCode} {body}");
            }
            return await res.Content.ReadAsStreamAsync();
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}

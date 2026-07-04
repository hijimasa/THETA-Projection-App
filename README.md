# THETA-Projection-App

RICOH THETA V のライブ映像を Wi-Fi 経由で Meta Quest 3 に全天球表示するビューアアプリです。

映像ソースは 2 系統あり、実機では既定で RTSP(高画質)が使われます:

| ソース | 画質 | 遅延 | カメラ側の準備 |
|---|---|---|---|
| **RTSP** (既定) | 1920x960〜3840x1920 / 30fps H.264 | 0.5〜1.5 秒程度 | THETA RTSP Streaming プラグインの導入が必要 |
| **Web API** (フォールバック) | 1024x512 / 30fps MJPEG | 小 | 不要(標準機能) |

- 受信映像は内向き球メッシュ(equirectangular マッピング)に投影
- Quest 3 スタンドアロン動作(PC 不要)。XR は OpenXR プラグイン + Meta Quest Support
- RTSP 再生は libVLC を Java ブリッジ([ThetaRtspPlayer.java](Assets/Plugins/Android/ThetaRtspPlayer.java))経由で利用(追加費用なし)
  - ExoPlayer (Media3) は THETA プラグインの SDP に sprop-parameter-sets が無いため再生不可
    ([androidx/media#2208](https://github.com/androidx/media/issues/2208))。libVLC はストリーム内から SPS/PPS を取得できる
  - THETA の RTSP プラグインは RTP/UDP のみ対応。TCP インターリーブを要求するとプラグインが異常終了する

## 必要なもの

| 項目 | 内容 |
|---|---|
| Unity | 6000.3.19f1 (Unity 6.3 LTS) + **Android Build Support (SDK/NDK/OpenJDK 込み)** |
| ヘッドセット | Meta Quest 3 (開発者モード有効化済み、USB でビルド転送) |
| カメラ | RICOH THETA V (ファームウェア v3.00.1 以降) |

## セットアップ手順

### 1. Unity プロジェクト

1. **Unity Hub でこのフォルダをプロジェクトとして開く**(初回はパッケージ解決に数分かかります)
2. Input System 関連のダイアログ(バックエンド有効化 → エディタ再起動)が出たら **Yes**
3. メニュー **THETA → 1. Setup Project Settings (Android + OpenXR)** を実行
   - Android 切替、IL2CPP/ARM64、Linear Color Space、**GLES3 のみ + MT レンダリング無効**(RTSP 用)、OpenXR + Meta Quest Support まで設定されます
4. メニュー **THETA → 2. Create Viewer Scene** を実行(`Assets/Scenes/ThetaViewer.unity` 生成)
5. **File → Build Settings → Build And Run** で Quest 3 に書き込み(メニュー **THETA → 3. Build APK** でも可)

### 1b. HTTPS 検査ソフト (Norton 等) が入った PC でのビルド設定

この開発 PC では Norton が TLS を傍受するため、Unity 同梱 JDK から Maven への接続が
PKIX エラーで失敗します。以下の回避策を適用済みです:

1. **JDK のコピー + Norton ルート CA 追加** (`C:\dev\UnityOpenJDK17`)
   - Unity 6 同梱の OpenJDK 17 (`<Unityインストール先>\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK`) を
     `C:\dev\UnityOpenJDK17` にコピー
   - Windows 証明書ストアから Norton のルート CA をエクスポート
     (`Cert:\LocalMachine\Root` の "Norton Web/Mail Shield Root")し、
     `keytool -importcert -keystore C:\dev\UnityOpenJDK17\lib\security\cacerts -storepass changeit` で追加
   - Unity は Gradle 起動時に jvmargs / JAVA_TOOL_OPTIONS を上書きするため、
     gradle.properties や環境変数での truststore 指定は効かない(cacerts 直接追加が確実)
2. メニュー **THETA → 4. Use Patched JDK** で Unity に設定(EditorPrefs 保存、初回のみ)

HTTPS 検査ソフトが無い PC ではこの節の手順は一切不要です。
(なお Unity 2022.3 時代は media3 の compileSdk 33 要求のため SDK 差し替えも必要だったが、
Unity 6 は android-34+ 同梱のため不要になった)

### 2. THETA V に RTSP プラグインを導入(高画質に必須)

プラグインストアの Web サイトは閉鎖されましたが、配布は [GitHub (ricohapi/theta-plugins)](https://github.com/ricohapi/theta-plugins) で継続しています。

1. PC に [RICOH THETA アプリ(デスクトップ用)](https://support.ricoh360.com/)をインストール
2. [THETA RTSP Streaming のページ](https://github.com/ricohapi/theta-plugins/tree/main/plugins/com.sciencearts.rtspstreaming)を開き、**[Install on THETA]** ボタン → THETA を USB 接続してインストール
3. スマホの THETA 公式アプリ等でアクティブプラグインに「THETA RTSP Streaming」を設定
4. カメラ単体では**モードボタン長押し**でプラグインモードが起動(動画 LED が点灯すれば配信中)

動作確認は PC を THETA の Wi-Fi につないで VLC で
`rtsp://192.168.1.1:8554/live?resolution=1920x960` を開くのが手軽です。

## 使い方

1. THETA V を AP モードにし、RTSP プラグインを起動(モードボタン長押し)
2. Quest 3 の設定 → Wi-Fi で `THETAYLxxxxxxxx.OSC` に接続(パスワード初期値はシリアル数字 8 桁)
3. アプリ「THETA Live Viewer」を起動 → 自動で接続され全天球映像が表示されます

RTSP プラグインを入れていない場合は、シーンの `THETA Viewer` → `ThetaViewerBootstrap` →
**Source Mode** を `WebApi` にすると低画質モードで動きます。

### PC エディタでの動作確認(Quest 不要)

PC を THETA の Wi-Fi に接続して Play すると **Web API ソース**で受信できます(RTSP は実機専用)。
Game ビュー内を右ドラッグで見回せます。

## 設定項目

シーン内 `THETA Viewer` オブジェクトのインスペクタ:

- **ThetaViewerBootstrap**
  - `Source Mode`: `Auto`(実機=RTSP / エディタ=WebApi)、`WebApi`、`Rtsp`
- **RtspStreamPlayer**
  - `Url`: `rtsp://192.168.1.1:8554/live?resolution=1920x960`(`3840x1920` も指定可、帯域次第)
  - `Texture Width/Height`: URL の resolution と合わせる
- **ThetaLivePreview**(Web API 用)
  - `Preview Width/Height/Framerate`: `1024x512@30`(既定) / `1920x960@8` / `640x320@30`
- **SphereProjector**(実行時に自動生成される `Projection Sphere`)
  - `Flip Horizontal`(既定 ON)/ `Flip Vertical`: 映像が鏡像・上下逆の場合に切り替え
  - `Yaw Offset Degrees`: 正面方向の回転補正

## 既知の制限

- **単眼 360°**: 立体視・位置トラッキング(6DoF)には対応しません(カメラの原理上の制約)。
- **天頂補正なし**: THETA V はライブ配信中の天頂補正が効かないため、カメラは水平・正立で設置してください。
- **RTSP の遅延**: バッファ設定を低遅延寄りにしていますが 0.5〜1.5 秒程度は発生します。
- **発熱**: RTSP プラグインでの長時間連続配信は 30 分程度で熱停止する報告があります。給電・送風か解像度を下げて運用してください。
- **クライアントモード(CL)非対応**: AP モード接続のみ対応です。
- **4K (3840x1920)**: プラグイン公式の注記どおり帯域次第で不安定です。まず 1920x960 で確認を。

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| `Connecting (RTSP)...` のまま | RTSP プラグインが起動しているか(動画 LED 点灯)、Quest が THETA の Wi-Fi に接続済みか確認 |
| `Disconnected: ...` を繰り返す | PC + VLC で `rtsp://192.168.1.1:8554/live?...` が開けるか確認。開けなければカメラ側の問題 |
| 映像が上下逆/鏡像 | `Projection Sphere` の `Flip Vertical` / `Flip Horizontal` を切り替え |
| 映像がカクつく・崩れる | resolution を `1920x960` → `1024x512` に下げる。Wi-Fi は 5GHz を使用 |
| `Connecting to THETA...` のまま (WebApi) | THETA のスリープ解除、他アプリ(スマホ公式アプリ等)の接続を切断 |
| ビルドエラー (SDK not found) | Unity Hub → インストール → 歯車 → モジュール加算で Android Build Support 一式を追加 |

## アーキテクチャ

```
THETA V (AP モード, 192.168.1.1)
   ├─ [高画質] RTSP プラグイン :8554 (H.264)
   │     ▼
   │  libVLC (RTP/UDP 受信 + HW デコード) ── ThetaRtspPlayer.java
   │     ▼ SurfaceTexture (OES) → FBO blit → TEXTURE_2D
   │  Texture2D.CreateExternalTexture ── RtspStreamPlayer.cs
   │
   └─ [フォールバック] Web API getLivePreview (MotionJPEG)
         ▼
      ThetaWebApi / MjpegStreamReader ── ThetaLivePreview.cs
         ▼ Texture2D.LoadImage

   ▼ (どちらも ILiveVideoSource として ThetaViewerBootstrap が選択)
内向き球メッシュ + Theta/Equirect シェーダ ── SphereProjector
   ▼
OpenXR (Meta Quest Support, GLES3) で HMD 描画
```

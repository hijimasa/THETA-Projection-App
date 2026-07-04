# THETA-Projection-App

RICOH THETA V のライブ映像を Wi-Fi 経由で Meta Quest 3 に全天球表示するビューアアプリです。

- THETA Web API (OSC v2.1) の `camera.getLivePreview` で MotionJPEG を受信
- JPEG フレームを `Texture2D` にデコードし、内向き球メッシュ(equirectangular マッピング)に投影
- Quest 3 スタンドアロン動作(PC 不要)。XR は OpenXR プラグイン + Meta Quest Support で実装

## 必要なもの

| 項目 | 内容 |
|---|---|
| Unity | 2022.3.22f1 (LTS) + **Android Build Support (SDK/NDK/OpenJDK 込み)** |
| ヘッドセット | Meta Quest 3 (開発者モード有効化済み、USB でビルド転送) |
| カメラ | RICOH THETA V (ファームウェア最新推奨) |

## セットアップ手順

1. **Unity Hub でこのフォルダをプロジェクトとして開く**(初回はパッケージ解決に数分かかります)
2. Input System 関連のダイアログ(バックエンド有効化 → エディタ再起動)が出たら **Yes**
3. メニュー **THETA → 1. Setup Project Settings (Android + OpenXR)** を実行
   - Android への切替、IL2CPP/ARM64、Linear Color Space、HTTP 許可、OpenXR + Meta Quest Support までまとめて設定されます
   - コンソールに警告が出た場合は指示に従って手動設定してください
4. メニュー **THETA → 2. Create Viewer Scene** を実行(`Assets/Scenes/ThetaViewer.unity` が生成されます)
5. **File → Build Settings → Build And Run** で Quest 3 に書き込み

## 使い方

1. THETA V の電源を入れ、**AP モード**にする(側面の無線ボタンを押し、Wi-Fi マークが青点灯)
2. Quest 3 の設定 → Wi-Fi で `THETAYLxxxxxxxx.OSC` に接続
   - パスワードは初期状態ではシリアル番号の数字 8 桁(本体底面に記載)
   - この間 Quest はインターネットに繋がりません(仕様)
3. アプリ「THETA Live Viewer」を起動 → 自動で接続され全天球映像が表示されます

### PC エディタでの動作確認(Quest 不要)

PC を THETA の Wi-Fi に接続した状態で Play すると同じ映像が受信できます。
Game ビュー内を**右ドラッグ**で見回せます。

## 設定項目

シーン内 `THETA Viewer` オブジェクトのインスペクタで変更できます。

- **ThetaLivePreview**
  - `Camera Host`: AP モードでは `192.168.1.1` 固定のまま
  - `Preview Width/Height/Framerate`: THETA V は `1024x512@30`(既定) / `1920x960@8` / `640x320@30`
- **SphereProjector**(実行時に自動生成される `Projection Sphere`)
  - `Flip Horizontal`(既定 ON)/ `Flip Vertical`: 映像が鏡像・上下逆に見える場合に切り替え
  - `Yaw Offset Degrees`: 映像の正面方向の回転補正

## 既知の制限

- **画質**: Web API プレビューの上限は 1920x960@8fps / 1024x512@30fps。VR ではかなり粗く見えます。
  高画質化(1920x960〜4K/30fps)は THETA の RTSP プラグイン + ExoPlayer 再生への移行で対応予定(次フェーズ)。
- **単眼 360°**: 立体視・位置トラッキング(6DoF)には対応しません(カメラの原理上の制約)。
- **天頂補正なし**: THETA V はライブ配信中の天頂補正が効かないため、カメラは水平・正立で設置してください。
- **クライアントモード(CL)非対応**: Digest 認証未実装のため AP モード接続のみ対応です。
- **Unity プロジェクトは OneDrive 等の同期フォルダに置かないこと**: ビルド生成物が同期プロセスにロックされ、ビルドが失敗します。

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| `Connecting to THETA...` のまま | Quest が THETA の Wi-Fi に繋がっているか確認。THETA のスリープを解除 |
| すぐ切断される | THETA が他のアプリ(スマホの公式アプリ等)と接続中だと失敗します。他の接続を切る |
| 映像が鏡像/上下逆 | `Projection Sphere` の `Flip Horizontal` / `Flip Vertical` を切り替え |
| 映像がカクつく | `1024x512@30` になっているか確認(`1920x960` は 8fps が仕様上限) |
| ビルドエラー (SDK not found) | Unity Hub → インストール → 歯車 → モジュール加算で Android Build Support 一式を追加 |

## アーキテクチャ

```
THETA V (AP モード, 192.168.1.1)
   │  HTTP POST /osc/commands/execute {"name":"camera.getLivePreview"}
   ▼
multipart/x-mixed-replace (MotionJPEG) ── ThetaWebApi / MjpegStreamReader (バックグラウンドスレッド)
   ▼ 最新フレームのみ受け渡し
Texture2D.LoadImage ── ThetaLivePreview (メインスレッド)
   ▼
内向き球メッシュ + Theta/Equirect シェーダ ── SphereProjector
   ▼
OpenXR (Meta Quest Support) で HMD 描画 ── ThetaViewerBootstrap
```

package com.thetaprojection.rtsp;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.net.Uri;
import android.opengl.GLES11Ext;
import android.opengl.GLES20;
import android.opengl.GLES30;
import android.util.Log;
import android.view.Surface;

import org.videolan.libvlc.LibVLC;
import org.videolan.libvlc.Media;
import org.videolan.libvlc.MediaPlayer;
import org.videolan.libvlc.interfaces.IVLCVout;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.ArrayList;

/**
 * libVLC で RTSP/H.264 を受信し、SurfaceTexture (OES テクスチャ) にデコードさせたうえで、
 * Unity が sampler2D として扱える通常の TEXTURE_2D に FBO 経由でコピーするブリッジ。
 *
 * プレイヤーに libVLC を使う理由: THETA の RTSP プラグインは SDP に
 * sprop-parameter-sets (SPS/PPS) を含めず in-band で送るため、ExoPlayer (Media3) では
 * "missing sprop parameter" で再生できない (androidx/media#2208)。VLC はストリーム内から
 * SPS/PPS を取得できる。
 *
 * 前提:
 *  - グラフィックス API は OpenGL ES 3 (Vulkan 不可)
 *  - Multithreaded Rendering 無効 (Unity メインスレッドに GL コンテキストがある状態で
 *    initGL / updateFrame / release を呼ぶこと)
 */
public final class ThetaRtspPlayer {

    // --- GL リソース (Unity の GL スレッドで生成・使用) ---
    private int oesTextureId;
    private int targetTextureId;
    private int framebufferId;
    private int program;
    private int aPosLocation;
    private int aTexLocation;
    private int uStMatrixLocation;
    private FloatBuffer quadBuffer;
    private SurfaceTexture surfaceTexture;
    private Surface surface;
    private int texWidth;
    private int texHeight;
    private final float[] stMatrix = new float[16];
    private volatile boolean frameAvailable;
    private boolean loggedFirstBlit;

    // --- libVLC ---
    private LibVLC libVLC;
    private MediaPlayer mediaPlayer;

    private volatile String state = "idle";        // idle / connecting / buffering / playing / ended / error
    private volatile String errorMessage = "";
    private volatile int videoWidth;
    private volatile int videoHeight;

    // ------------------------------------------------------------------ GL 初期化

    /** Unity の GL スレッドから呼ぶ。成功したら true。 */
    public boolean initGL(int width, int height) {
        texWidth = width;
        texHeight = height;
        try {
            int[] tex = new int[2];
            GLES20.glGenTextures(2, tex, 0);
            oesTextureId = tex[0];
            targetTextureId = tex[1];

            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTextureId);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, 0);

            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, targetTextureId);
            GLES20.glTexImage2D(GLES20.GL_TEXTURE_2D, 0, GLES20.GL_RGBA, width, height, 0,
                    GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, null);
            GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_REPEAT);
            GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0);

            int[] fbo = new int[1];
            GLES20.glGenFramebuffers(1, fbo, 0);
            framebufferId = fbo[0];

            buildBlitProgram();

            float[] quad = {
                    -1f, -1f, 0f, 0f,
                     1f, -1f, 1f, 0f,
                    -1f,  1f, 0f, 1f,
                     1f,  1f, 1f, 1f,
            };
            quadBuffer = ByteBuffer.allocateDirect(quad.length * 4)
                    .order(ByteOrder.nativeOrder()).asFloatBuffer();
            quadBuffer.put(quad).position(0);

            surfaceTexture = new SurfaceTexture(oesTextureId);
            surfaceTexture.setDefaultBufferSize(width, height);
            surfaceTexture.setOnFrameAvailableListener(st -> frameAvailable = true);
            surface = new Surface(surfaceTexture);
            return true;
        } catch (Throwable t) {
            errorMessage = "initGL failed: " + t;
            state = "error";
            return false;
        }
    }

    private void buildBlitProgram() {
        String vs = "attribute vec2 aPos;\n"
                + "attribute vec2 aTex;\n"
                + "uniform mat4 uStMatrix;\n"
                + "varying vec2 vTex;\n"
                + "void main() {\n"
                + "  gl_Position = vec4(aPos, 0.0, 1.0);\n"
                + "  vTex = (uStMatrix * vec4(aTex, 0.0, 1.0)).xy;\n"
                + "}\n";
        String fs = "#extension GL_OES_EGL_image_external : require\n"
                + "precision mediump float;\n"
                + "varying vec2 vTex;\n"
                + "uniform samplerExternalOES uTexture;\n"
                + "void main() {\n"
                + "  gl_FragColor = texture2D(uTexture, vTex);\n"
                + "}\n";

        int v = compileShader(GLES20.GL_VERTEX_SHADER, vs);
        int f = compileShader(GLES20.GL_FRAGMENT_SHADER, fs);
        program = GLES20.glCreateProgram();
        GLES20.glAttachShader(program, v);
        GLES20.glAttachShader(program, f);
        GLES20.glLinkProgram(program);
        int[] status = new int[1];
        GLES20.glGetProgramiv(program, GLES20.GL_LINK_STATUS, status, 0);
        if (status[0] == 0) {
            throw new RuntimeException("blit program link failed: " + GLES20.glGetProgramInfoLog(program));
        }
        GLES20.glDeleteShader(v);
        GLES20.glDeleteShader(f);
        aPosLocation = GLES20.glGetAttribLocation(program, "aPos");
        aTexLocation = GLES20.glGetAttribLocation(program, "aTex");
        uStMatrixLocation = GLES20.glGetUniformLocation(program, "uStMatrix");
    }

    private static int compileShader(int type, String source) {
        int shader = GLES20.glCreateShader(type);
        GLES20.glShaderSource(shader, source);
        GLES20.glCompileShader(shader);
        int[] status = new int[1];
        GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, status, 0);
        if (status[0] == 0) {
            String log = GLES20.glGetShaderInfoLog(shader);
            GLES20.glDeleteShader(shader);
            throw new RuntimeException("shader compile failed: " + log);
        }
        return shader;
    }

    // ------------------------------------------------------------------ 再生制御

    /**
     * RTSP 再生を開始する。Context には UnityPlayer の Activity を渡す。
     * forceTcp: RTP を RTSP 接続に多重化する (UDP がルーティングされない環境向け。推奨 true)
     */
    public void start(final Context context, final String url, final boolean forceTcp) {
        stopPlayerInternal();
        state = "connecting";
        errorMessage = "";
        try {
            ArrayList<String> options = new ArrayList<>();
            options.add("--no-audio");            // ライブビュー用途。遅延要因を減らす
            options.add("--network-caching=300"); // 低遅延寄りのバッファ [ms]
            if (forceTcp) {
                options.add("--rtsp-tcp");
            }
            libVLC = new LibVLC(context, options);
            mediaPlayer = new MediaPlayer(libVLC);

            mediaPlayer.setEventListener(event -> {
                switch (event.type) {
                    case MediaPlayer.Event.Buffering:
                        if (!"playing".equals(state) || event.getBuffering() < 100f) {
                            state = "buffering";
                        }
                        break;
                    case MediaPlayer.Event.Playing:
                        state = "playing";
                        break;
                    case MediaPlayer.Event.EndReached:
                        state = "ended";
                        break;
                    case MediaPlayer.Event.EncounteredError:
                        // libVLC はエラー詳細をイベントに載せない (詳細は adb logcat -s VLC)
                        errorMessage = "libVLC playback error (see logcat, tag VLC)";
                        state = "error";
                        break;
                    default:
                        break;
                }
            });

            IVLCVout vout = mediaPlayer.getVLCVout();
            vout.setVideoSurface(surface, null);
            vout.setWindowSize(texWidth, texHeight);
            vout.attachViews((vlcVout, width, height, visibleWidth, visibleHeight, sarNum, sarDen) -> {
                if (width > 0 && height > 0) {
                    videoWidth = width;
                    videoHeight = height;
                }
            });

            Media media = new Media(libVLC, Uri.parse(url));
            media.setHWDecoderEnabled(true, false);
            media.addOption(":network-caching=300");
            if (forceTcp) {
                media.addOption(":rtsp-tcp");
            }
            mediaPlayer.setMedia(media);
            media.release();
            mediaPlayer.play();
        } catch (Throwable t) {
            errorMessage = "start failed: " + t;
            state = "error";
        }
    }

    /**
     * 新しいフレームがあれば OES → TEXTURE_2D へコピーする。
     * Unity の GL スレッドから毎フレーム呼ぶ。コピーしたら true。
     */
    public boolean updateFrame() {
        if (!frameAvailable || surfaceTexture == null) {
            return false;
        }
        frameAvailable = false;
        surfaceTexture.updateTexImage();
        surfaceTexture.getTransformMatrix(stMatrix);

        // --- Unity 側の GL 状態を退避 ---
        int[] prevFbo = new int[1];
        int[] prevViewport = new int[4];
        int[] prevProgram = new int[1];
        int[] prevActiveTexture = new int[1];
        int[] prevVao = new int[1];
        GLES20.glGetIntegerv(GLES20.GL_FRAMEBUFFER_BINDING, prevFbo, 0);
        GLES20.glGetIntegerv(GLES20.GL_VIEWPORT, prevViewport, 0);
        GLES20.glGetIntegerv(GLES20.GL_CURRENT_PROGRAM, prevProgram, 0);
        GLES20.glGetIntegerv(GLES20.GL_ACTIVE_TEXTURE, prevActiveTexture, 0);
        GLES20.glGetIntegerv(GLES30.GL_VERTEX_ARRAY_BINDING, prevVao, 0);
        boolean depthTest = GLES20.glIsEnabled(GLES20.GL_DEPTH_TEST);
        boolean blend = GLES20.glIsEnabled(GLES20.GL_BLEND);
        boolean cullFace = GLES20.glIsEnabled(GLES20.GL_CULL_FACE);
        boolean scissor = GLES20.glIsEnabled(GLES20.GL_SCISSOR_TEST);

        try {
            // ES3 では VAO がバインドされたままクライアント側頂点配列を使うと
            // GL_INVALID_OPERATION で描画されない。blit の間だけデフォルト VAO (0) に切り替える
            GLES30.glBindVertexArray(0);
            GLES20.glDisable(GLES20.GL_DEPTH_TEST);
            GLES20.glDisable(GLES20.GL_BLEND);
            GLES20.glDisable(GLES20.GL_CULL_FACE);
            GLES20.glDisable(GLES20.GL_SCISSOR_TEST);

            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, framebufferId);
            GLES20.glFramebufferTexture2D(GLES20.GL_FRAMEBUFFER, GLES20.GL_COLOR_ATTACHMENT0,
                    GLES20.GL_TEXTURE_2D, targetTextureId, 0);
            GLES20.glViewport(0, 0, texWidth, texHeight);

            GLES20.glUseProgram(program);
            GLES20.glUniformMatrix4fv(uStMatrixLocation, 1, false, stMatrix, 0);

            GLES20.glActiveTexture(GLES20.GL_TEXTURE0);
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTextureId);

            GLES20.glBindBuffer(GLES20.GL_ARRAY_BUFFER, 0);
            quadBuffer.position(0);
            GLES20.glVertexAttribPointer(aPosLocation, 2, GLES20.GL_FLOAT, false, 16, quadBuffer);
            GLES20.glEnableVertexAttribArray(aPosLocation);
            quadBuffer.position(2);
            GLES20.glVertexAttribPointer(aTexLocation, 2, GLES20.GL_FLOAT, false, 16, quadBuffer);
            GLES20.glEnableVertexAttribArray(aTexLocation);

            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4);

            if (!loggedFirstBlit) {
                loggedFirstBlit = true;
                Log.i("ThetaRtsp", "first blit done, glError=" + GLES20.glGetError()
                        + " fboStatus=" + GLES20.glCheckFramebufferStatus(GLES20.GL_FRAMEBUFFER)
                        + " (36053=COMPLETE)");
            }

            GLES20.glDisableVertexAttribArray(aPosLocation);
            GLES20.glDisableVertexAttribArray(aTexLocation);
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, 0);
        } finally {
            // --- Unity 側の GL 状態を復元 ---
            GLES30.glBindVertexArray(prevVao[0]);
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, prevFbo[0]);
            GLES20.glViewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
            GLES20.glUseProgram(prevProgram[0]);
            GLES20.glActiveTexture(prevActiveTexture[0]);
            if (depthTest) GLES20.glEnable(GLES20.GL_DEPTH_TEST);
            if (blend) GLES20.glEnable(GLES20.GL_BLEND);
            if (cullFace) GLES20.glEnable(GLES20.GL_CULL_FACE);
            if (scissor) GLES20.glEnable(GLES20.GL_SCISSOR_TEST);
        }
        return true;
    }

    // ------------------------------------------------------------------ 情報取得

    public int getTargetTexture() { return targetTextureId; }
    public String getState() { return state; }
    public String getError() { return errorMessage; }
    public int getVideoWidth() { return videoWidth; }
    public int getVideoHeight() { return videoHeight; }

    // ------------------------------------------------------------------ 終了処理

    /** プレイヤーだけ止める (GL リソースは維持し再接続に使う)。 */
    public void stop() {
        stopPlayerInternal();
        state = "idle";
    }

    private void stopPlayerInternal() {
        try {
            if (mediaPlayer != null) {
                mediaPlayer.setEventListener(null);
                mediaPlayer.stop();
                mediaPlayer.getVLCVout().detachViews();
                mediaPlayer.release();
                mediaPlayer = null;
            }
            if (libVLC != null) {
                libVLC.release();
                libVLC = null;
            }
        } catch (Throwable ignored) {
        }
    }

    /** GL リソースも含めて全解放。Unity の GL スレッドから呼ぶ。 */
    public void release() {
        stopPlayerInternal();
        if (surface != null) { surface.release(); surface = null; }
        if (surfaceTexture != null) { surfaceTexture.release(); surfaceTexture = null; }
        int[] tmp = new int[2];
        if (framebufferId != 0) { tmp[0] = framebufferId; GLES20.glDeleteFramebuffers(1, tmp, 0); framebufferId = 0; }
        if (program != 0) { GLES20.glDeleteProgram(program); program = 0; }
        tmp[0] = oesTextureId; tmp[1] = targetTextureId;
        GLES20.glDeleteTextures(2, tmp, 0);
        oesTextureId = 0;
        targetTextureId = 0;
    }
}

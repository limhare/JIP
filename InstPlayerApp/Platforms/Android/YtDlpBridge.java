package com.instplayer.app;

import android.content.Context;
import android.util.Log;
import com.yausername.youtubedl_android.YoutubeDL;
import com.yausername.youtubedl_android.YoutubeDLRequest;
import com.yausername.ffmpeg.FFmpeg;

/**
 * yt-dlp (youtubedl-android) wrapper.
 * PC 버전과 동일한 yt-dlp 파이프라인 — YoutubeExplode가 유튜브 변경에 깨질 때의 근본 대책.
 * C#에서 폴링(isDone/getProgress/getError)으로 상태 확인.
 */
public class YtDlpBridge {

    private static final String TAG = "JIP_YTDLP";
    private static volatile boolean initialized = false;
    private static volatile String initError = null;

    private volatile int progress = 0;
    private volatile boolean done = false;
    private volatile String result = null;
    private volatile String error = null;

    public int getProgress() { return progress; }
    public boolean isDone()  { return done;     }
    public String getResult() { return result;  }
    public String getError()  { return error;   }

    /** 앱 시작 시 백그라운드 초기화 (최초 1회 python 추출로 수 초 소요) */
    public static void initAsync(final Context ctx) {
        new Thread(new Runnable() {
            @Override public void run() { ensureInit(ctx); }
        }, "jip-ytdlp-init").start();
    }

    public static synchronized boolean ensureInit(Context ctx) {
        if (initialized) return true;
        try {
            YoutubeDL.getInstance().init(ctx);
            FFmpeg.getInstance().init(ctx);
            initialized = true;
            initError = null;
            Log.i(TAG, "yt-dlp init ok: " + YoutubeDL.getInstance().versionName(ctx));
            return true;
        } catch (Throwable t) {
            initError = t.getMessage();
            Log.e(TAG, "yt-dlp init failed", t);
            return false;
        }
    }

    /** 다운로드 시작 — outputTemplate 예: {dir}/%(title)s/%(title)s.%(ext)s */
    public void startDownload(final Context ctx, final String url,
                              final String outputTemplate, final String audioFormat) {
        progress = 0; done = false; result = null; error = null;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    if (!ensureInit(ctx))
                        throw new Exception("yt-dlp init failed: " + initError);
                    YoutubeDLRequest req = new YoutubeDLRequest(url);
                    req.addOption("-x");
                    req.addOption("--audio-format", audioFormat);
                    req.addOption("--audio-quality", "mp3".equals(audioFormat) ? "320K" : "0");
                    req.addOption("--no-playlist");
                    req.addOption("--no-mtime");
                    req.addOption("-o", outputTemplate);
                    YoutubeDL.getInstance().execute(req, null, new BoundProgressFn());
                    result = "ok";
                    progress = 100;
                    done = true;
                } catch (Throwable t) {
                    Log.e(TAG, "yt-dlp download failed", t);
                    error = t.getMessage() != null ? t.getMessage() : "yt-dlp error";
                    done = true;
                }
            }
        }, "jip-ytdlp-dl").start();
    }

    /** yt-dlp 자체 업데이트 (다운로드 실패 시 자동 복구용) */
    public void startUpdate(final Context ctx) {
        progress = 0; done = false; result = null; error = null;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    if (!ensureInit(ctx))
                        throw new Exception("yt-dlp init failed: " + initError);
                    YoutubeDL.getInstance().updateYoutubeDL(ctx, YoutubeDL.UpdateChannel._STABLE);
                    result = "ok";
                    done = true;
                } catch (Throwable t) {
                    Log.e(TAG, "yt-dlp update failed", t);
                    error = t.getMessage() != null ? t.getMessage() : "update error";
                    done = true;
                }
            }
        }, "jip-ytdlp-up").start();
    }

    private class BoundProgressFn implements kotlin.jvm.functions.Function3<Float, Long, String, kotlin.Unit> {
        @Override
        public kotlin.Unit invoke(Float p, Long etaSec, String line) {
            if (p != null && p >= 0f) progress = (int) Math.min(100f, p);
            return kotlin.Unit.INSTANCE;
        }
    }
}

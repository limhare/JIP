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

    // yt-dlp 실행과 자체 업데이트가 겹치지 않도록 (업데이트가 실행 중인 바이너리를 교체하지 않게)
    private static final Object EXEC_LOCK = new Object();

    private volatile int job = 0; // 이전 작업 스레드가 새 작업 상태를 덮어쓰지 않도록
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

    /**
     * 다운로드 시작 — outputTemplate 예: {dir}/%(title)s/%(title)s.%(ext)s
     * pathFile: 최종 저장 경로를 yt-dlp가 기록할 파일 (--print는 quiet를 강제해 진행률이 사라지므로 print-to-file 사용)
     */
    public void startDownload(final Context ctx, final String url,
                              final String outputTemplate, final String audioFormat,
                              final String pathFile) {
        final int myJob = ++job;
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
                    if (pathFile != null && !pathFile.isEmpty())
                        req.addCommands(java.util.Arrays.asList("--print-to-file", "after_move:filepath", pathFile));
                    synchronized (EXEC_LOCK) {
                        YoutubeDL.getInstance().execute(req, null, new BoundProgressFn(myJob));
                    }
                    if (myJob != job) return;
                    result = "ok";
                    progress = 100;
                    done = true;
                } catch (Throwable t) {
                    Log.e(TAG, "yt-dlp download failed", t);
                    if (myJob != job) return;
                    error = t.getMessage() != null ? t.getMessage() : "yt-dlp error";
                    done = true;
                }
            }
        }, "jip-ytdlp-dl").start();
    }

    /** yt-dlp 자체 업데이트 (다운로드 실패 시 자동 복구용) */
    public void startUpdate(final Context ctx) {
        final int myJob = ++job;
        progress = 0; done = false; result = null; error = null;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    if (!ensureInit(ctx))
                        throw new Exception("yt-dlp init failed: " + initError);
                    synchronized (EXEC_LOCK) {
                        YoutubeDL.getInstance().updateYoutubeDL(ctx, YoutubeDL.UpdateChannel._STABLE);
                    }
                    if (myJob != job) return;
                    result = "ok";
                    done = true;
                } catch (Throwable t) {
                    Log.e(TAG, "yt-dlp update failed", t);
                    if (myJob != job) return;
                    error = t.getMessage() != null ? t.getMessage() : "update error";
                    done = true;
                }
            }
        }, "jip-ytdlp-up").start();
    }

    private class BoundProgressFn implements kotlin.jvm.functions.Function3<Float, Long, String, kotlin.Unit> {
        private final int myJob;
        BoundProgressFn(int myJob) { this.myJob = myJob; }
        @Override
        public kotlin.Unit invoke(Float p, Long etaSec, String line) {
            if (myJob == job && p != null && p >= 0f) progress = (int) Math.min(100f, p);
            return kotlin.Unit.INSTANCE;
        }
    }
}

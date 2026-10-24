package com.instplayer.app;

import android.content.Context;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackParameters;
import androidx.media3.common.Player;
import androidx.media3.exoplayer.ExoPlayer;

import org.json.JSONObject;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

/**
 * Java-side builder: creates the app-wide shared ExoPlayer with the
 * SoundTouch audio pipeline. Called from C# via JNI, and reused by
 * JipMediaLibraryService (Android Auto).
 *
 * - 플레이어/프로세서는 프로세스 전역 싱글턴 (앱 재생과 Auto 재생이 같은 엔진 공유)
 * - 키: SoundTouch / 속도: PlaybackParameters(Sonic) — 재생 위치 계산이 정확하도록
 * - 오디오 포커스 직접 관리: 내비 duck 무시, 완전 손실 시 일시정지
 * - 녹음(_REC_, 녹음 폴더) 파일은 자동 원키/원속도 (사용자 설정값은 보존)
 */
public class SoundTouchExoPlayerBuilder {

    private static final Object LOCK = new Object();
    private static SoundTouchProcessor sProcessor;
    private static ExoPlayer sPlayer;
    private static AudioManager sAudioManager;
    private static AudioFocusRequest sFocusRequest;
    private static boolean sHasFocus = false;
    private static boolean sResumeOnGain = false;
    private static float sUserPitch = 0f;
    private static float sUserTempo = 0f;
    private static boolean sCurrentIsRec = false;
    private static boolean sUserSettingsLoaded = false;

    private static final AudioManager.OnAudioFocusChangeListener sFocusListener =
            new AudioManager.OnAudioFocusChangeListener() {
        @Override
        public void onAudioFocusChange(int focusChange) {
            switch (focusChange) {
                case AudioManager.AUDIOFOCUS_LOSS:
                    sHasFocus = false;
                    sResumeOnGain = false;
                    if (sPlayer != null) sPlayer.pause();
                    break;
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                    sHasFocus = false;
                    if (sPlayer != null && sPlayer.isPlaying()) {
                        sResumeOnGain = true;
                        sPlayer.pause();
                    }
                    break;
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                    // 내비게이션 안내 등 — 볼륨 유지 (덕킹 무시)
                    break;
                case AudioManager.AUDIOFOCUS_GAIN:
                    sHasFocus = true;
                    if (sResumeOnGain && sPlayer != null) {
                        sResumeOnGain = false;
                        sPlayer.play();
                    }
                    break;
            }
        }
    };

    /** @return 포커스 획득 여부. 실패(통화 중 등)면 false */
    private static boolean requestFocus() {
        if (sHasFocus || sAudioManager == null) return true;
        int result;
        if (android.os.Build.VERSION.SDK_INT >= 26) {
            if (sFocusRequest == null) {
                sFocusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                        .setAudioAttributes(new android.media.AudioAttributes.Builder()
                                .setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                                .setContentType(android.media.AudioAttributes.CONTENT_TYPE_MUSIC)
                                .build())
                        .setWillPauseWhenDucked(true) // duck 이벤트를 받아 '무시'하기 위함
                        .setOnAudioFocusChangeListener(sFocusListener)
                        .build();
            }
            result = sAudioManager.requestAudioFocus(sFocusRequest);
        } else {
            result = sAudioManager.requestAudioFocus(sFocusListener,
                    AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN);
        }
        sHasFocus = (result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED);
        return sHasFocus;
    }

    private static boolean isRecPath(String path) {
        if (path == null) return false;
        String lower = path.toLowerCase();
        return lower.contains("_rec_") || lower.contains("/녹음/") || lower.contains("/recordings/");
    }

    /** 현재 곡 종류에 맞춰 실제 엔진 키/속도 적용 (녹음 파일은 원키/원속도) */
    private static void applyEffective() {
        float pitch = sCurrentIsRec ? 0f : sUserPitch;
        float tempo = sCurrentIsRec ? 0f : sUserTempo;
        if (sProcessor != null) sProcessor.setPitch(pitch);
        if (sPlayer != null) {
            float speed = Math.max(0.25f, Math.min(4f, 1f + tempo / 100f));
            Runnable r = new Runnable() {
                @Override public void run() {
                    if (sPlayer != null) sPlayer.setPlaybackParameters(new PlaybackParameters(speed, 1f));
                }
            };
            if (Looper.myLooper() == sPlayer.getApplicationLooper()) r.run();
            else new Handler(sPlayer.getApplicationLooper()).post(r);
        }
    }

    /** 프로세스 전역 공유 플레이어 (Auto 서비스와 앱이 함께 사용) */
    public static ExoPlayer getSharedPlayer(Context context) {
        synchronized (LOCK) {
            if (sPlayer != null) return sPlayer;
            Context app = context.getApplicationContext();
            sProcessor = new SoundTouchProcessor();
            SoundTouchRenderersFactory factory = new SoundTouchRenderersFactory(app, sProcessor);
            ExoPlayer player = new ExoPlayer.Builder(app)
                    .setRenderersFactory(factory)
                    // 라우팅용 속성만 지정 — 포커스는 위에서 직접 관리 (자동 덕킹 방지)
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(C.USAGE_MEDIA)
                            .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                            .build(), /* handleAudioFocus= */ false)
                    .setHandleAudioBecomingNoisy(true)
                    .build();
            sAudioManager = (AudioManager) app.getSystemService(Context.AUDIO_SERVICE);
            player.addListener(new Player.Listener() {
                @Override
                public void onIsPlayingChanged(boolean isPlaying) {
                    if (isPlaying && !requestFocus() && sPlayer != null) {
                        sPlayer.pause(); // 통화 중 등 포커스 거부 시 재생하지 않음
                    }
                }

                @Override
                public void onMediaItemTransition(MediaItem mediaItem, int reason) {
                    // 녹음 파일은 원키/원속도, 일반 곡은 사용자 설정 (사용자 값은 덮어쓰지 않음)
                    String path = (mediaItem != null && mediaItem.localConfiguration != null)
                            ? mediaItem.localConfiguration.uri.getPath() : null;
                    sCurrentIsRec = isRecPath(path);
                    applyEffective();
                }
            });
            sPlayer = player;
            return player;
        }
    }

    /** Auto 서비스가 앱 없이 콜드 스타트한 경우 저장된 키/속도를 불러온다 */
    public static void loadUserSettingsOnce(Context context) {
        synchronized (LOCK) {
            if (sUserSettingsLoaded) return;
            sUserSettingsLoaded = true;
        }
        try {
            File f = new File(context.getFilesDir(), "settings.json");
            if (!f.exists()) return;
            JSONObject o = new JSONObject(new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8));
            sUserPitch = (float) o.optDouble("Pitch", 0);
            sUserTempo = (float) o.optDouble("Tempo", 0);
            applyEffective();
        } catch (Throwable t) {
            Log.w("JIP", "settings load failed: " + t.getMessage());
        }
    }

    public static float getUserPitch() { return sUserPitch; }
    public static float getUserTempo() { return sUserTempo; }

    public static void setUserPitch(float semitones) {
        boolean wasLoaded = sUserSettingsLoaded;
        sUserSettingsLoaded = true; // 앱이 값을 직접 지정 → 파일 로드 불필요
        // 같은 값 재지정(곡 전환 시 앱이 다시 보내는 값)은 녹음 원키 상태를 풀지 않음
        if (wasLoaded && semitones == sUserPitch) return;
        sUserPitch = semitones;
        sCurrentIsRec = false;      // 녹음 재생 중 키를 바꾸면 수동 조절로 전환 (PC와 동일)
        applyEffective();
    }

    public static void setUserTempo(float tempoPercent) {
        boolean wasLoaded = sUserSettingsLoaded;
        sUserSettingsLoaded = true;
        if (wasLoaded && tempoPercent == sUserTempo) return;
        sUserTempo = tempoPercent;
        sCurrentIsRec = false;
        applyEffective();
    }

    /** 사용자가 직접 일시정지/정지 — 통화 종료 후 자동 재개하지 않도록 */
    public static void onUserPause() {
        sResumeOnGain = false;
    }

    /** 포커스 일시 손실로 멈춘 상태인지 (곧 자동 재개됨) */
    public static boolean isTransientFocusPause() {
        return sResumeOnGain;
    }

    // ── C#(SoundTouchBridge)용 인스턴스 API — 내부적으로 싱글턴 위임 ──

    public ExoPlayer build(Context context) {
        return getSharedPlayer(context);
    }

    public void setPitch(float semitones) {
        setUserPitch(semitones);
    }

    public void setTempo(float tempoPercent) {
        setUserTempo(tempoPercent);
    }

    public void release() {
        // 공유 플레이어는 프로세스 수명 동안 유지 — 포커스만 반납
        synchronized (LOCK) {
            if (sAudioManager != null) {
                try {
                    if (android.os.Build.VERSION.SDK_INT >= 26 && sFocusRequest != null)
                        sAudioManager.abandonAudioFocusRequest(sFocusRequest);
                    else
                        sAudioManager.abandonAudioFocus(sFocusListener);
                } catch (Throwable ignored) { }
                sHasFocus = false;
            }
        }
    }
}

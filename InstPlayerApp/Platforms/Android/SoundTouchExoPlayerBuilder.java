package com.instplayer.app;

import android.content.Context;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.util.Log;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.Player;
import androidx.media3.exoplayer.ExoPlayer;

/**
 * Java-side builder: creates the app-wide shared ExoPlayer with the
 * SoundTouch audio pipeline. Called from C# via JNI, and reused by
 * JipMediaLibraryService (Android Auto).
 *
 * - 플레이어/프로세서는 프로세스 전역 싱글턴 (C# 재생과 Auto 재생이 같은 엔진 공유)
 * - 오디오 포커스 직접 관리: 내비 duck 무시, 완전 손실 시 일시정지
 * - 녹음(_REC_) 파일 재생 시 자동 원키/원속도
 */
public class SoundTouchExoPlayerBuilder {

    private static final Object LOCK = new Object();
    private static SoundTouchProcessor sProcessor;
    private static ExoPlayer sPlayer;
    private static AudioManager sAudioManager;
    private static AudioFocusRequest sFocusRequest;
    private static boolean sHasFocus = false;
    private static boolean sResumeOnGain = false;
    private static float sUserPitch = 0f;   // 사용자가 지정한 키 (REC 원키 복원용)
    private static float sUserTempo = 0f;

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

    private static void requestFocus() {
        if (sHasFocus || sAudioManager == null) return;
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
    }

    private static boolean isRecPath(String path) {
        if (path == null) return false;
        String lower = path.toLowerCase();
        return lower.contains("_rec_") || lower.contains("/녹음/") || lower.contains("/recordings/");
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
                    if (isPlaying) requestFocus();
                }

                @Override
                public void onMediaItemTransition(MediaItem mediaItem, int reason) {
                    // 녹음 파일은 항상 원키/원속도, 일반 곡은 사용자 설정 복원
                    if (sProcessor == null) return;
                    String uri = (mediaItem != null && mediaItem.localConfiguration != null)
                            ? String.valueOf(mediaItem.localConfiguration.uri) : null;
                    if (isRecPath(uri)) {
                        sProcessor.setPitch(0f);
                        sProcessor.setTempo(0f);
                    } else {
                        sProcessor.setPitch(sUserPitch);
                        sProcessor.setTempo(sUserTempo);
                    }
                }
            });
            sPlayer = player;
            return player;
        }
    }

    public static float getUserPitch() { return sUserPitch; }

    public static void setUserPitch(float semitones) {
        sUserPitch = semitones;
        if (sProcessor != null) sProcessor.setPitch(semitones);
    }

    public static void setUserTempo(float tempoPercent) {
        sUserTempo = tempoPercent;
        if (sProcessor != null) sProcessor.setTempo(tempoPercent);
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

package com.instplayer.app;

import android.content.Context;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.util.Log;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.Player;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.session.MediaSession;

/**
 * Java-side builder: creates ExoPlayer with SoundTouch audio pipeline.
 * Called from C# via JNI to avoid needing auto-generated Xamarin bindings.
 * 오디오 포커스 + MediaSession 등록 — 차량(Android Auto)/블루투스 라우팅과
 * 미디어 버튼 제어를 위해 필요.
 */
public class SoundTouchExoPlayerBuilder {

    private final SoundTouchProcessor processor = new SoundTouchProcessor();
    private MediaSession mediaSession;
    private ExoPlayer playerRef;
    private AudioManager audioManager;
    private AudioFocusRequest focusRequest;
    private boolean hasFocus = false;
    private boolean resumeOnGain = false;

    // 오디오 포커스를 직접 관리: 내비 안내의 duck(볼륨낮춤) 요청은 무시하고
    // 전화/다른 음악 앱 같은 완전한 포커스 손실에만 일시정지한다.
    private final AudioManager.OnAudioFocusChangeListener focusListener = new AudioManager.OnAudioFocusChangeListener() {
        @Override
        public void onAudioFocusChange(int focusChange) {
            switch (focusChange) {
                case AudioManager.AUDIOFOCUS_LOSS:
                    hasFocus = false;
                    resumeOnGain = false;
                    if (playerRef != null) playerRef.pause();
                    break;
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT:
                    if (playerRef != null && playerRef.isPlaying()) {
                        resumeOnGain = true;
                        playerRef.pause();
                    }
                    break;
                case AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK:
                    // 내비게이션 안내 등 — 볼륨 유지 (덕킹 무시)
                    break;
                case AudioManager.AUDIOFOCUS_GAIN:
                    hasFocus = true;
                    if (resumeOnGain && playerRef != null) {
                        resumeOnGain = false;
                        playerRef.play();
                    }
                    break;
            }
        }
    };

    private void requestFocus() {
        if (hasFocus || audioManager == null) return;
        int result;
        if (android.os.Build.VERSION.SDK_INT >= 26) {
            if (focusRequest == null) {
                focusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                        .setAudioAttributes(new android.media.AudioAttributes.Builder()
                                .setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                                .setContentType(android.media.AudioAttributes.CONTENT_TYPE_MUSIC)
                                .build())
                        // duck 이벤트를 콜백으로 받아 '무시'하기 위해 true
                        .setWillPauseWhenDucked(true)
                        .setOnAudioFocusChangeListener(focusListener)
                        .build();
            }
            result = audioManager.requestAudioFocus(focusRequest);
        } else {
            result = audioManager.requestAudioFocus(focusListener,
                    AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN);
        }
        hasFocus = (result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED);
    }

    /** Build and return a configured ExoPlayer instance. */
    public ExoPlayer build(Context context) {
        SoundTouchRenderersFactory factory = new SoundTouchRenderersFactory(context, processor);
        ExoPlayer player = new ExoPlayer.Builder(context)
                .setRenderersFactory(factory)
                // 라우팅용 속성만 지정, 포커스는 위에서 직접 관리 (자동 덕킹 방지)
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(C.USAGE_MEDIA)
                        .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                        .build(), /* handleAudioFocus= */ false)
                .setHandleAudioBecomingNoisy(true)
                .build();
        playerRef = player;
        audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
        player.addListener(new Player.Listener() {
            @Override
            public void onIsPlayingChanged(boolean isPlaying) {
                if (isPlaying) requestFocus();
            }
        });
        try {
            mediaSession = new MediaSession.Builder(context, player).build();
        } catch (Throwable t) {
            Log.w("JIP", "MediaSession create failed: " + t.getMessage());
        }
        return player;
    }

    public void release() {
        if (audioManager != null) {
            try {
                if (android.os.Build.VERSION.SDK_INT >= 26 && focusRequest != null)
                    audioManager.abandonAudioFocusRequest(focusRequest);
                else
                    audioManager.abandonAudioFocus(focusListener);
            } catch (Throwable ignored) { }
            hasFocus = false;
        }
        if (mediaSession != null) {
            try { mediaSession.release(); } catch (Throwable ignored) { }
            mediaSession = null;
        }
    }

    public void setPitch(float semitones) {
        processor.setPitch(semitones);
    }

    public void setTempo(float tempoPercent) {
        processor.setTempo(tempoPercent);
    }
}

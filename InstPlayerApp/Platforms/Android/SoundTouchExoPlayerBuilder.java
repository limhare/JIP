package com.instplayer.app;

import android.content.Context;
import android.util.Log;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
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

    /** Build and return a configured ExoPlayer instance. */
    public ExoPlayer build(Context context) {
        SoundTouchRenderersFactory factory = new SoundTouchRenderersFactory(context, processor);
        ExoPlayer player = new ExoPlayer.Builder(context)
                .setRenderersFactory(factory)
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(C.USAGE_MEDIA)
                        .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                        .build(), /* handleAudioFocus= */ true)
                .setHandleAudioBecomingNoisy(true)
                .build();
        try {
            mediaSession = new MediaSession.Builder(context, player).build();
        } catch (Throwable t) {
            Log.w("JIP", "MediaSession create failed: " + t.getMessage());
        }
        return player;
    }

    public void release() {
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

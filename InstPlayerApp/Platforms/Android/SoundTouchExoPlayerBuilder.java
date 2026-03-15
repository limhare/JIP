package com.instplayer.app;

import android.content.Context;
import androidx.media3.exoplayer.ExoPlayer;

/**
 * Java-side builder: creates ExoPlayer with SoundTouch audio pipeline.
 * Called from C# via JNI to avoid needing auto-generated Xamarin bindings.
 */
public class SoundTouchExoPlayerBuilder {

    private final SoundTouchProcessor processor = new SoundTouchProcessor();

    /** Build and return a configured ExoPlayer instance. */
    public ExoPlayer build(Context context) {
        SoundTouchRenderersFactory factory = new SoundTouchRenderersFactory(context, processor);
        return new ExoPlayer.Builder(context)
                .setRenderersFactory(factory)
                .build();
    }

    public void setPitch(float semitones) {
        processor.setPitch(semitones);
    }

    public void setTempo(float tempoPercent) {
        processor.setTempo(tempoPercent);
    }
}

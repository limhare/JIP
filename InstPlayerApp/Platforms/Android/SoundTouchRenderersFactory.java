package com.instplayer.app;

import android.content.Context;
import androidx.media3.exoplayer.DefaultRenderersFactory;
import androidx.media3.exoplayer.audio.AudioRendererEventListener;
import androidx.media3.exoplayer.audio.AudioSink;
import androidx.media3.exoplayer.audio.DefaultAudioSink;
import androidx.media3.common.audio.AudioProcessor;

/**
 * Custom RenderersFactory that injects SoundTouchProcessor into the audio pipeline.
 */
public class SoundTouchRenderersFactory extends DefaultRenderersFactory {

    private final SoundTouchProcessor soundTouchProcessor;

    public SoundTouchRenderersFactory(Context context, SoundTouchProcessor processor) {
        super(context);
        this.soundTouchProcessor = processor;
    }

    @Override
    protected AudioSink buildAudioSink(
            Context context,
            boolean enableFloatOutput,
            boolean enableAudioTrackPlaybackParams) {

        return new DefaultAudioSink.Builder(context)
                .setEnableFloatOutput(true) // Float output needed for SoundTouch
                .setAudioProcessors(new AudioProcessor[]{soundTouchProcessor})
                .build();
    }
}

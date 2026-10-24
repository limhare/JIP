package com.instplayer.app;

import android.content.Context;
import androidx.media3.common.PlaybackParameters;
import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.audio.AudioProcessorChain;
import androidx.media3.exoplayer.DefaultRenderersFactory;
import androidx.media3.exoplayer.audio.AudioSink;
import androidx.media3.exoplayer.audio.DefaultAudioSink;

/**
 * Custom RenderersFactory that injects SoundTouchProcessor into the audio pipeline.
 *
 * 체인 순서: SoundTouch(키) → 무음스킵 → Sonic(속도) → PcmTap(녹음용, 최종 출력 복사)
 * - 속도는 Sonic(PlaybackParameters)이 담당해야 ExoPlayer의 재생 위치 계산이 맞는다
 *   (SoundTouch로 속도를 바꾸면 위치가 어긋나 A-B 반복/진행바가 틀어짐).
 * - float 출력은 끈다: 켜면 고해상도/float 디코딩 시 커스텀 프로세서 전체를 우회한다.
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
                .setEnableFloatOutput(false)
                .setAudioProcessorChain(new TapAtEndChain(soundTouchProcessor, new PcmTapProcessor()))
                .build();
    }

    /** 기본 체인(SoundTouch + 무음스킵 + Sonic) 뒤에 탭 프로세서를 덧붙인다. */
    private static final class TapAtEndChain implements AudioProcessorChain {
        private final DefaultAudioSink.DefaultAudioProcessorChain inner;
        private final AudioProcessor[] processors;

        TapAtEndChain(AudioProcessor first, AudioProcessor tap) {
            inner = new DefaultAudioSink.DefaultAudioProcessorChain(first);
            AudioProcessor[] base = inner.getAudioProcessors();
            processors = new AudioProcessor[base.length + 1];
            System.arraycopy(base, 0, processors, 0, base.length);
            processors[base.length] = tap;
        }

        @Override public AudioProcessor[] getAudioProcessors() { return processors; }

        @Override public PlaybackParameters applyPlaybackParameters(PlaybackParameters p) {
            return inner.applyPlaybackParameters(p);
        }

        @Override public boolean applySkipSilenceEnabled(boolean enabled) {
            return inner.applySkipSilenceEnabled(enabled);
        }

        @Override public long getMediaDuration(long playoutDuration) {
            return inner.getMediaDuration(playoutDuration);
        }

        @Override public long getSkippedOutputFrameCount() {
            return inner.getSkippedOutputFrameCount();
        }
    }
}

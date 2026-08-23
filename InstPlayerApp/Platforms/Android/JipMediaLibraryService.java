package com.instplayer.app;

import android.net.Uri;
import android.os.Bundle;
import android.util.Log;

import androidx.media3.common.MediaItem;
import androidx.media3.common.MediaMetadata;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.session.CommandButton;
import androidx.media3.session.LibraryResult;
import androidx.media3.session.MediaLibraryService;
import androidx.media3.session.MediaSession;
import androidx.media3.session.SessionCommand;
import androidx.media3.session.SessionResult;

import com.google.common.collect.ImmutableList;
import com.google.common.util.concurrent.Futures;
import com.google.common.util.concurrent.ListenableFuture;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

/**
 * Android Auto용 미디어 브라우저 서비스.
 * 브라우즈 트리: 재생목록(설정 JSON) / 보관함(Downloads 곡별 폴더) / 저장된 목록(Playlists/*.json)
 * 커스텀 버튼: 키 올림/내림.
 * 재생 엔진은 SoundTouchExoPlayerBuilder의 공유 플레이어를 그대로 사용.
 */
public class JipMediaLibraryService extends MediaLibraryService {

    private static final String TAG = "JIP_AUTO";
    private static final String ROOT_ID  = "root";
    private static final String CUR_ID   = "cur";
    private static final String LIB_ID   = "lib";
    private static final String SAVED_ID = "saved";
    private static final String CMD_KEY_UP   = "com.instplayer.app.KEY_UP";
    private static final String CMD_KEY_DOWN = "com.instplayer.app.KEY_DOWN";

    private static final String[] AUDIO_EXTS =
            { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".webm" };

    private MediaLibrarySession session;

    private static final Comparator<File> BY_NAME = new Comparator<File>() {
        @Override public int compare(File a, File b) {
            return a.getName().compareToIgnoreCase(b.getName());
        }
    };

    private static final Comparator<File> BY_NAME_DESC = new Comparator<File>() {
        @Override public int compare(File a, File b) {
            return b.getName().compareToIgnoreCase(a.getName());
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        ExoPlayer player = SoundTouchExoPlayerBuilder.getSharedPlayer(this);
        MediaLibrarySession.Builder builder =
                new MediaLibrarySession.Builder(this, player, new LibraryCallback());
        try {
            CommandButton keyDown = new CommandButton.Builder()
                    .setDisplayName("키 내림")
                    .setSessionCommand(new SessionCommand(CMD_KEY_DOWN, Bundle.EMPTY))
                    .setIconResId(android.R.drawable.arrow_down_float)
                    .build();
            CommandButton keyUp = new CommandButton.Builder()
                    .setDisplayName("키 올림")
                    .setSessionCommand(new SessionCommand(CMD_KEY_UP, Bundle.EMPTY))
                    .setIconResId(android.R.drawable.arrow_up_float)
                    .build();
            builder.setCustomLayout(ImmutableList.of(keyDown, keyUp));
        } catch (Throwable t) {
            Log.w(TAG, "custom layout skipped: " + t.getMessage());
        }
        session = builder.build();
    }

    @Override
    public MediaLibrarySession onGetSession(MediaSession.ControllerInfo controllerInfo) {
        return session;
    }

    @Override
    public void onDestroy() {
        if (session != null) {
            session.release();
            session = null;
        }
        super.onDestroy();
    }

    // ── 데이터 읽기 (C# 쪽 저장 형식 그대로 파싱) ──

    private String filesDir() {
        return getFilesDir().getAbsolutePath();
    }

    private static boolean isAudio(String name) {
        String lower = name.toLowerCase();
        for (String e : AUDIO_EXTS) if (lower.endsWith(e)) return true;
        return false;
    }

    private static String titleOf(String path) {
        String name = new File(path).getName();
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(0, dot) : name;
    }

    private List<String> readCurrentPlaylist() {
        List<String> out = new ArrayList<>();
        try {
            File f = new File(filesDir(), "settings.json");
            if (!f.exists()) return out;
            String json = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
            JSONArray arr = new JSONObject(json).optJSONArray("Playlist");
            if (arr != null)
                for (int i = 0; i < arr.length(); i++) {
                    String p = arr.getString(i);
                    if (new File(p).exists()) out.add(p);
                }
        } catch (Throwable t) {
            Log.w(TAG, "settings read failed: " + t.getMessage());
        }
        return out;
    }

    private List<String> readSavedPlaylist(String name) {
        List<String> out = new ArrayList<>();
        try {
            File f = new File(new File(filesDir(), "Playlists"), name + ".json");
            if (!f.exists()) return out;
            String json = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
            JSONArray arr = new JSONArray(json);
            for (int i = 0; i < arr.length(); i++) {
                String p = arr.getString(i);
                if (new File(p).exists()) out.add(p);
            }
        } catch (Throwable t) {
            Log.w(TAG, "saved playlist read failed: " + t.getMessage());
        }
        return out;
    }

    // ── MediaItem 헬퍼 ──

    private static MediaItem folder(String id, String title) {
        return new MediaItem.Builder()
                .setMediaId(id)
                .setMediaMetadata(new MediaMetadata.Builder()
                        .setTitle(title)
                        .setIsBrowsable(true)
                        .setIsPlayable(false)
                        .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                        .build())
                .build();
    }

    private static MediaItem song(String path) {
        return new MediaItem.Builder()
                .setMediaId("song:" + path)
                .setUri(Uri.fromFile(new File(path)))
                .setMediaMetadata(new MediaMetadata.Builder()
                        .setTitle(titleOf(path))
                        .setIsBrowsable(false)
                        .setIsPlayable(true)
                        .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                        .build())
                .build();
    }

    /** mediaId → 재생 가능한 MediaItem 목록으로 확장 */
    private List<MediaItem> resolve(MediaItem item) {
        String id = item.mediaId;
        List<MediaItem> out = new ArrayList<>();
        if (id.startsWith("song:")) {
            out.add(song(id.substring(5)));
        } else if (id.startsWith("savedpl:")) {
            for (String p : readSavedPlaylist(id.substring(8))) out.add(song(p));
        } else if (id.equals(CUR_ID)) {
            for (String p : readCurrentPlaylist()) out.add(song(p));
        }
        return out;
    }

    // ── 콜백 ──

    private class LibraryCallback implements MediaLibrarySession.Callback {

        @Override
        public MediaSession.ConnectionResult onConnect(MediaSession session,
                                                       MediaSession.ControllerInfo controller) {
            MediaSession.ConnectionResult base =
                    MediaLibrarySession.Callback.super.onConnect(session, controller);
            try {
                return new MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                        .setAvailableSessionCommands(base.availableSessionCommands.buildUpon()
                                .add(new SessionCommand(CMD_KEY_UP, Bundle.EMPTY))
                                .add(new SessionCommand(CMD_KEY_DOWN, Bundle.EMPTY))
                                .build())
                        .build();
            } catch (Throwable t) {
                return base;
            }
        }

        @Override
        public ListenableFuture<SessionResult> onCustomCommand(MediaSession session,
                MediaSession.ControllerInfo controller, SessionCommand customCommand, Bundle args) {
            float p = SoundTouchExoPlayerBuilder.getUserPitch();
            if (CMD_KEY_UP.equals(customCommand.customAction)) {
                SoundTouchExoPlayerBuilder.setUserPitch(Math.min(8f, p + 1f));
                return Futures.immediateFuture(new SessionResult(SessionResult.RESULT_SUCCESS));
            }
            if (CMD_KEY_DOWN.equals(customCommand.customAction)) {
                SoundTouchExoPlayerBuilder.setUserPitch(Math.max(-8f, p - 1f));
                return Futures.immediateFuture(new SessionResult(SessionResult.RESULT_SUCCESS));
            }
            return MediaLibrarySession.Callback.super.onCustomCommand(session, controller, customCommand, args);
        }

        @Override
        public ListenableFuture<LibraryResult<MediaItem>> onGetLibraryRoot(
                MediaLibrarySession session, MediaSession.ControllerInfo browser,
                LibraryParams params) {
            return Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT_ID, "JIP"), params));
        }

        @Override
        public ListenableFuture<LibraryResult<MediaItem>> onGetItem(
                MediaLibrarySession session, MediaSession.ControllerInfo browser, String mediaId) {
            if (mediaId.startsWith("song:"))
                return Futures.immediateFuture(LibraryResult.ofItem(song(mediaId.substring(5)), null));
            return Futures.immediateFuture(LibraryResult.ofItem(folder(mediaId, "JIP"), null));
        }

        @Override
        public ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> onGetChildren(
                MediaLibrarySession session, MediaSession.ControllerInfo browser,
                String parentId, int page, int pageSize, LibraryParams params) {
            List<MediaItem> items = new ArrayList<>();
            try {
                switch (parentId) {
                    case ROOT_ID:
                        items.add(folder(CUR_ID, "재생목록"));
                        items.add(folder(LIB_ID, "보관함"));
                        items.add(folder(SAVED_ID, "저장된 목록"));
                        break;
                    case CUR_ID:
                        for (String p : readCurrentPlaylist()) items.add(song(p));
                        break;
                    case LIB_ID: {
                        File dl = new File(filesDir(), "Downloads");
                        File[] all = dl.listFiles();
                        List<File> dirList = new ArrayList<>();
                        if (all != null)
                            for (File f : all) if (f.isDirectory()) dirList.add(f);
                        File[] dirs = dirList.toArray(new File[0]);
                        Arrays.sort(dirs, BY_NAME);
                        for (File d : dirs)
                            items.add(folder("libfolder:" + d.getAbsolutePath(), d.getName()));
                        break;
                    }
                    case SAVED_ID: {
                        File pls = new File(filesDir(), "Playlists");
                        File[] allPl = pls.listFiles();
                        List<File> plList = new ArrayList<>();
                        if (allPl != null)
                            for (File f : allPl) if (f.isFile() && f.getName().endsWith(".json")) plList.add(f);
                        File[] files = plList.toArray(new File[0]);
                        {
                            Arrays.sort(files, BY_NAME);
                            for (File f : files) {
                                String name = f.getName().substring(0, f.getName().length() - 5);
                                items.add(new MediaItem.Builder()
                                        .setMediaId("savedpl:" + name)
                                        .setMediaMetadata(new MediaMetadata.Builder()
                                                .setTitle(name)
                                                .setIsBrowsable(false)
                                                .setIsPlayable(true)
                                                .setMediaType(MediaMetadata.MEDIA_TYPE_PLAYLIST)
                                                .build())
                                        .build());
                            }
                        }
                        break;
                    }
                    default:
                        if (parentId.startsWith("libfolder:")) {
                            File dir = new File(parentId.substring(10));
                            File[] files = dir.listFiles();
                            if (files != null) {
                                Arrays.sort(files, BY_NAME);
                                for (File f : files)
                                    if (f.isFile() && isAudio(f.getName()))
                                        items.add(song(f.getAbsolutePath()));
                                // 곡 폴더의 녹음\ 안까지 노출
                                File rec = new File(dir, "녹음");
                                File[] recs = rec.listFiles();
                                if (recs != null) {
                                    Arrays.sort(recs, BY_NAME_DESC);
                                    for (File f : recs)
                                        if (f.isFile() && isAudio(f.getName()))
                                            items.add(song(f.getAbsolutePath()));
                                }
                            }
                        }
                        break;
                }
            } catch (Throwable t) {
                Log.w(TAG, "onGetChildren failed: " + t.getMessage());
            }
            return Futures.immediateFuture(LibraryResult.ofItemList(ImmutableList.copyOf(items), params));
        }

        @Override
        public ListenableFuture<List<MediaItem>> onAddMediaItems(MediaSession mediaSession,
                MediaSession.ControllerInfo controller, List<MediaItem> mediaItems) {
            List<MediaItem> resolved = new ArrayList<>();
            for (MediaItem item : mediaItems) {
                List<MediaItem> r = resolve(item);
                if (r.isEmpty() && item.localConfiguration != null) resolved.add(item);
                else resolved.addAll(r);
            }
            return Futures.immediateFuture(resolved);
        }
    }
}

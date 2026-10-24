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
 * 곡을 고르면 같은 목록 전체가 대기열로 들어간다 (다음곡/이전곡 동작).
 * 커스텀 버튼: 키 올림/내림. 재생 엔진은 SoundTouchExoPlayerBuilder의 공유 플레이어.
 *
 * 미디어 ID 형식: "song:" + 부모키 + "\n" + 절대경로  (부모키: cur / lib:<폴더> / saved:<이름>)
 */
public class JipMediaLibraryService extends MediaLibraryService {

    private static final String TAG = "JIP_AUTO";
    private static final String ROOT_ID  = "root";
    private static final String CUR_ID   = "cur";
    private static final String LIB_ID   = "lib";
    private static final String SAVED_ID = "saved";
    private static final String SEP = "\n";
    private static final String CMD_KEY_UP   = "com.instplayer.app.KEY_UP";
    private static final String CMD_KEY_DOWN = "com.instplayer.app.KEY_DOWN";

    private static final String[] AUDIO_EXTS =
            { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".webm" };

    // 접속 허용 컨트롤러 (Auto, 시스템 미디어 컨트롤, 블루투스, 구글 어시스턴트)
    private static final String[] ALLOWED_PACKAGES = {
            "com.google.android.projection.gearhead",
            "com.google.android.carassistant",
            "com.google.android.googlequicksearchbox",
            "com.android.systemui",
            "com.android.bluetooth",
            "com.google.android.bluetooth",
    };

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

    private MediaLibrarySession session;

    @Override
    public void onCreate() {
        super.onCreate();
        ExoPlayer player = SoundTouchExoPlayerBuilder.getSharedPlayer(this);
        SoundTouchExoPlayerBuilder.loadUserSettingsOnce(this);
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

    // ── 경로/데이터 ──

    private File filesRoot() {
        return getFilesDir();
    }

    /** 앱 저장소(files) 안의 경로만 허용 — 외부에서 임의 경로를 탐색/재생하지 못하게 */
    private boolean isInsideFiles(File f) {
        try {
            String root = filesRoot().getCanonicalPath() + File.separator;
            return f.getCanonicalPath().startsWith(root);
        } catch (Exception e) {
            return false;
        }
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
            File f = new File(filesRoot(), "settings.json");
            if (!f.exists()) return out;
            String json = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
            JSONArray arr = new JSONObject(json).optJSONArray("Playlist");
            if (arr != null)
                for (int i = 0; i < arr.length(); i++) {
                    File p = new File(arr.getString(i));
                    if (p.isFile() && isInsideFiles(p)) out.add(p.getAbsolutePath());
                }
        } catch (Throwable t) {
            Log.w(TAG, "settings read failed: " + t.getMessage());
        }
        return out;
    }

    private List<String> readSavedPlaylist(String name) {
        List<String> out = new ArrayList<>();
        try {
            File f = new File(new File(filesRoot(), "Playlists"), name + ".json");
            if (!f.exists() || !isInsideFiles(f)) return out;
            String json = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
            JSONArray arr = new JSONArray(json);
            for (int i = 0; i < arr.length(); i++) {
                File p = new File(arr.getString(i));
                if (p.isFile() && isInsideFiles(p)) out.add(p.getAbsolutePath());
            }
        } catch (Throwable t) {
            Log.w(TAG, "saved playlist read failed: " + t.getMessage());
        }
        return out;
    }

    /** 곡 폴더의 오디오 파일 + 그 녹음 폴더(최신순) */
    private List<String> readLibFolder(File dir) {
        List<String> out = new ArrayList<>();
        if (!dir.isDirectory() || !isInsideFiles(dir)) return out;
        File[] files = dir.listFiles();
        if (files != null) {
            Arrays.sort(files, BY_NAME);
            for (File f : files)
                if (f.isFile() && isAudio(f.getName())) out.add(f.getAbsolutePath());
        }
        File[] recs = new File(dir, "녹음").listFiles();
        if (recs != null) {
            Arrays.sort(recs, BY_NAME_DESC);
            for (File f : recs)
                if (f.isFile() && isAudio(f.getName())) out.add(f.getAbsolutePath());
        }
        return out;
    }

    /** 부모키 → 그 목록의 곡 경로들 */
    private List<String> songsOfParent(String parentKey) {
        if (CUR_ID.equals(parentKey)) return readCurrentPlaylist();
        if (parentKey.startsWith("lib:")) return readLibFolder(new File(parentKey.substring(4)));
        if (parentKey.startsWith("saved:")) return readSavedPlaylist(parentKey.substring(6));
        return new ArrayList<>();
    }

    // ── MediaItem 헬퍼 ──

    private static MediaItem folder(String id, String title, boolean playable) {
        return new MediaItem.Builder()
                .setMediaId(id)
                .setMediaMetadata(new MediaMetadata.Builder()
                        .setTitle(title)
                        .setIsBrowsable(!playable)
                        .setIsPlayable(playable)
                        .setMediaType(playable ? MediaMetadata.MEDIA_TYPE_PLAYLIST
                                               : MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                        .build())
                .build();
    }

    private static MediaItem song(String parentKey, String path) {
        return new MediaItem.Builder()
                .setMediaId("song:" + parentKey + SEP + path)
                .setUri(Uri.fromFile(new File(path)))
                .setMediaMetadata(new MediaMetadata.Builder()
                        .setTitle(titleOf(path))
                        .setIsBrowsable(false)
                        .setIsPlayable(true)
                        .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                        .build())
                .build();
    }

    private List<MediaItem> songsAsItems(String parentKey) {
        List<MediaItem> out = new ArrayList<>();
        for (String p : songsOfParent(parentKey)) out.add(song(parentKey, p));
        return out;
    }

    /** "song:<parent>\n<path>" 파싱 → {parent, path} (형식 불일치면 null) */
    private String[] parseSongId(String id) {
        if (!id.startsWith("song:")) return null;
        String rest = id.substring(5);
        int i = rest.indexOf(SEP);
        if (i < 0) return new String[] { "", rest };
        return new String[] { rest.substring(0, i), rest.substring(i + 1) };
    }

    /** 미디어 ID 하나 → 재생 항목들 (허용된 앱 저장소 경로만) */
    private List<MediaItem> resolve(MediaItem item) {
        List<MediaItem> out = new ArrayList<>();
        String id = item.mediaId;
        String[] s = parseSongId(id);
        if (s != null) {
            File f = new File(s[1]);
            if (f.isFile() && isInsideFiles(f)) out.add(song(s[0], f.getAbsolutePath()));
        } else if (id.startsWith("savedpl:")) {
            out.addAll(songsAsItems("saved:" + id.substring(8)));
        } else if (id.equals(CUR_ID)) {
            out.addAll(songsAsItems(CUR_ID));
        }
        return out;
    }

    private boolean isAllowedController(MediaSession session, MediaSession.ControllerInfo c) {
        String pkg = c.getPackageName();
        if (getPackageName().equals(pkg)) return true;
        try {
            if (session.isMediaNotificationController(c)
                    || session.isAutomotiveController(c)
                    || session.isAutoCompanionController(c)) return true;
        } catch (Throwable ignored) { }
        if (c.isTrusted()) return true; // 시스템/MEDIA_CONTENT_CONTROL 권한 보유
        for (String p : ALLOWED_PACKAGES) if (p.equals(pkg)) return true;
        return false;
    }

    // ── 콜백 ──

    private class LibraryCallback implements MediaLibrarySession.Callback {

        @Override
        public MediaSession.ConnectionResult onConnect(MediaSession session,
                                                       MediaSession.ControllerInfo controller) {
            if (!isAllowedController(session, controller)) {
                Log.w(TAG, "rejected controller: " + controller.getPackageName());
                return MediaSession.ConnectionResult.reject();
            }
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
            return Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT_ID, "JIP", false), params));
        }

        @Override
        public ListenableFuture<LibraryResult<MediaItem>> onGetItem(
                MediaLibrarySession session, MediaSession.ControllerInfo browser, String mediaId) {
            String[] s = parseSongId(mediaId);
            MediaItem item;
            if (s != null) item = song(s[0], s[1]);
            else if (mediaId.equals(CUR_ID)) item = folder(CUR_ID, "재생목록", false);
            else if (mediaId.equals(LIB_ID)) item = folder(LIB_ID, "보관함", false);
            else if (mediaId.equals(SAVED_ID)) item = folder(SAVED_ID, "저장된 목록", false);
            else if (mediaId.startsWith("savedpl:")) item = folder(mediaId, mediaId.substring(8), true);
            else if (mediaId.startsWith("libfolder:")) item = folder(mediaId, new File(mediaId.substring(10)).getName(), false);
            else item = folder(ROOT_ID, "JIP", false);
            return Futures.immediateFuture(LibraryResult.ofItem(item, null));
        }

        @Override
        public ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> onGetChildren(
                MediaLibrarySession session, MediaSession.ControllerInfo browser,
                String parentId, int page, int pageSize, LibraryParams params) {
            List<MediaItem> items = new ArrayList<>();
            try {
                switch (parentId) {
                    case ROOT_ID:
                        items.add(folder(CUR_ID, "재생목록", false));
                        items.add(folder(LIB_ID, "보관함", false));
                        items.add(folder(SAVED_ID, "저장된 목록", false));
                        break;
                    case CUR_ID:
                        items.addAll(songsAsItems(CUR_ID));
                        break;
                    case LIB_ID: {
                        File[] all = new File(filesRoot(), "Downloads").listFiles();
                        List<File> dirList = new ArrayList<>();
                        if (all != null)
                            for (File f : all) if (f.isDirectory()) dirList.add(f);
                        File[] dirs = dirList.toArray(new File[0]);
                        Arrays.sort(dirs, BY_NAME);
                        for (File d : dirs)
                            items.add(folder("libfolder:" + d.getAbsolutePath(), d.getName(), false));
                        break;
                    }
                    case SAVED_ID: {
                        File[] allPl = new File(filesRoot(), "Playlists").listFiles();
                        List<File> plList = new ArrayList<>();
                        if (allPl != null)
                            for (File f : allPl) if (f.isFile() && f.getName().endsWith(".json")) plList.add(f);
                        File[] files = plList.toArray(new File[0]);
                        Arrays.sort(files, BY_NAME);
                        for (File f : files) {
                            String name = f.getName().substring(0, f.getName().length() - 5);
                            items.add(folder("savedpl:" + name, name, true));
                        }
                        break;
                    }
                    default:
                        if (parentId.startsWith("libfolder:"))
                            items.addAll(songsAsItems("lib:" + parentId.substring(10)));
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
            for (MediaItem item : mediaItems) resolved.addAll(resolve(item)); // 미확인 URI는 버림
            return Futures.immediateFuture(resolved);
        }

        @Override
        public ListenableFuture<MediaSession.MediaItemsWithStartPosition> onSetMediaItems(
                MediaSession mediaSession, MediaSession.ControllerInfo controller,
                List<MediaItem> mediaItems, int startIndex, long startPositionMs) {
            // 곡 하나를 골랐으면 그 곡이 속한 목록 전체를 대기열로 (다음/이전곡 가능)
            if (mediaItems.size() == 1) {
                String[] s = parseSongId(mediaItems.get(0).mediaId);
                if (s != null && !s[0].isEmpty()) {
                    List<String> paths = songsOfParent(s[0]);
                    int idx = paths.indexOf(s[1]);
                    if (idx >= 0) {
                        List<MediaItem> queue = new ArrayList<>();
                        for (String p : paths) queue.add(song(s[0], p));
                        return Futures.immediateFuture(
                                new MediaSession.MediaItemsWithStartPosition(queue, idx, startPositionMs));
                    }
                }
            }
            List<MediaItem> resolved = new ArrayList<>();
            for (MediaItem item : mediaItems) resolved.addAll(resolve(item));
            int start = (startIndex >= 0 && startIndex < resolved.size()) ? startIndex : 0;
            return Futures.immediateFuture(
                    new MediaSession.MediaItemsWithStartPosition(resolved, start, startPositionMs));
        }
    }
}

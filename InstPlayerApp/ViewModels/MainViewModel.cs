using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstPlayerApp.Models;
using InstPlayerApp.Services;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace InstPlayerApp.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAudioPlayerService _audio;
    private readonly AppSettings _settings;
    private IDispatcherTimer? _progressTimer;
    private readonly Random _rng = new();
    private readonly Stack<int> _playHistory = new();
    private PlaylistItem? _currentPlayingItem; // 재생 중인 아이템 참조 (드래그 리오더 대응)

    // AB repeat
    private TimeSpan _abPointA = TimeSpan.FromSeconds(-1);
    private TimeSpan _abPointB = TimeSpan.FromSeconds(-1);

    public ObservableCollection<PlaylistItem> Playlist { get; } = [];
    public ObservableCollection<string> LibraryFiles { get; } = [];
    public ObservableCollection<string> LibraryFolders { get; } = [];

    [ObservableProperty] private string nowPlayingText = "Just InstPlayer";
    [ObservableProperty] private string playPauseIcon = "\u25B6";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private double progressMax = 1;
    [ObservableProperty] private string currentTimeText = "0:00";
    [ObservableProperty] private string totalTimeText = "0:00";
    [ObservableProperty] private int currentIndex = -1;

    // Pitch: -8 ~ +8
    [ObservableProperty] private int pitch;
    [ObservableProperty] private string pitchText = "0";

    // Tempo: -100 ~ +100 (0 = 100% 정속도)
    [ObservableProperty] private float tempo;
    [ObservableProperty] private string tempoText = "100%";

    // Volume: 0 ~ 150
    [ObservableProperty] private double volume = 100;
    [ObservableProperty] private string volumeText = "100%";

    // VU
    [ObservableProperty] private float vuLevel;

    // AB
    [ObservableProperty] private string abInfoText = "A-B Off";
    public bool IsPointASet => _abPointA.TotalSeconds >= 0;
    public bool IsPointBSet => _abPointB.TotalSeconds >= 0;
    public bool IsAbRangeVisible => IsPointASet && IsPointBSet;
    private double AbStartRatio => ProgressMax > 0 ? _abPointA.TotalSeconds / ProgressMax : 0;
    private double AbEndRatio => ProgressMax > 0 ? _abPointB.TotalSeconds / ProgressMax : 0;
    // Slider actual size (set from code-behind SizeChanged)
    private double _sliderWidth;
    private double _sliderHeight = 44;
    private const double ThumbRadius = 10.0; // Android Material thumb ~20dp diameter
    public void UpdateSliderSize(double w, double h)
    {
        _sliderWidth = w; _sliderHeight = h;
        NotifyAbRects();
    }
    private double TrackX(double ratio) => _sliderWidth > 0
        ? ThumbRadius + ratio * Math.Max(_sliderWidth - 2 * ThumbRadius, 0)
        : 0;
    public Microsoft.Maui.Graphics.Rect AbRangeRect => IsAbRangeVisible && _sliderWidth > 0
        ? new(TrackX(AbStartRatio), 0, Math.Max(TrackX(AbEndRatio) - TrackX(AbStartRatio), 1), _sliderHeight)
        : new(0, 0, 0, 0);
    public Microsoft.Maui.Graphics.Rect AbLineARect => _sliderWidth > 0
        ? new(TrackX(AbStartRatio), 0, 2, _sliderHeight)
        : new(0, 0, 0, 0);
    public Microsoft.Maui.Graphics.Rect AbLineBRect => _sliderWidth > 0
        ? new(TrackX(AbEndRatio), 0, 2, _sliderHeight)
        : new(0, 0, 0, 0);
    private void NotifyAbRects()
    {
        OnPropertyChanged(nameof(AbRangeRect));
        OnPropertyChanged(nameof(AbLineARect));
        OnPropertyChanged(nameof(AbLineBRect));
    }
    partial void OnProgressMaxChanged(double value) => NotifyAbRects();

    // Modes
    [ObservableProperty] private bool repeatOne;
    [ObservableProperty] private bool repeatAll;
    [ObservableProperty] private bool shuffleMode;

    // Lyrics
    [ObservableProperty] private string lyricsText = "";
    [ObservableProperty] private bool lyricsEditMode;
    public string LyricsEditModeText => LyricsEditMode ? "Done" : "Edit";
    partial void OnLyricsEditModeChanged(bool value) => OnPropertyChanged(nameof(LyricsEditModeText));
    [ObservableProperty] private int lyricsFontSize = 14;

    // Panels
    [ObservableProperty] private bool showPlaylist = true;
    [ObservableProperty] private bool showLyrics;
    [ObservableProperty] private bool showYoutube;
    [ObservableProperty] private bool isWideLayout;
    public bool IsNarrowLayout => !IsWideLayout;
    partial void OnIsWideLayoutChanged(bool value) => OnPropertyChanged(nameof(IsNarrowLayout));

    [RelayCommand]
    private void ShowPlaylistTab() { ShowPlaylist = true; ShowLyrics = false; ShowYoutube = false; }
    [RelayCommand]
    private void ShowLyricsTab()   { ShowPlaylist = false; ShowLyrics = true;  ShowYoutube = false; }
    [RelayCommand]
    private void ShowYoutubeTab()  { ShowPlaylist = false; ShowLyrics = false; ShowYoutube = true;  }

    // YouTube
    [ObservableProperty] private string youtubeUrl = "";
    [ObservableProperty] private string youtubeStatusText = "";
    [ObservableProperty] private double youtubeProgress;
    [ObservableProperty] private bool isDownloading;

    // Export MP3
    [ObservableProperty] private bool isExporting;
    [ObservableProperty] private int exportProgress;       // 0~100
    [ObservableProperty] private string exportStatusText = "";

    // Language
    [ObservableProperty] private bool isKorean = true;

    public MainViewModel(IAudioPlayerService audio)
    {
        _audio = audio;
        _settings = AppSettings.Load();
        ApplySettings();

        Playlist.CollectionChanged += (_, e) =>
        {
            SaveSettings();
            if (e.NewItems != null)
                foreach (PlaylistItem item in e.NewItems)
                    item.PropertyChanged += OnPlaylistItemPropertyChanged;
            if (e.OldItems != null)
                foreach (PlaylistItem item in e.OldItems)
                    item.PropertyChanged -= OnPlaylistItemPropertyChanged;

            // 재생 중인 아이템의 새 위치 추적 (드래그 리오더/삭제/추가 모두 대응)
            if (_currentPlayingItem != null)
                CurrentIndex = Playlist.IndexOf(_currentPlayingItem);
        };

        _audio.PlaybackEnded += OnPlaybackEnded;
        _audio.LevelUpdated += level => VuLevel = level;
    }

    public void StartTimer(IDispatcher dispatcher)
    {
        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _progressTimer.Tick += OnProgressTick;
        _progressTimer.Start();
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (_audio.Status != PlaybackStatus.Playing) return;

        var pos = _audio.Position;
        var dur = _audio.Duration;
        ProgressMax = dur.TotalSeconds > 0 ? dur.TotalSeconds : 1;
        ProgressValue = pos.TotalSeconds;
        CurrentTimeText = FormatTime(pos);
        TotalTimeText = FormatTime(dur);

        // AB repeat check
        if (_abPointB.TotalSeconds > 0 && pos >= _abPointB)
            _audio.SeekTo(_abPointA);
    }

    // ── Playlist item selection change → update play icon ──

    private void OnPlaylistItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaylistItem.IsSelected)) return;
        RefreshPlayPauseIcon();
    }

    public void RefreshPlayPauseIcon()
    {
        var sel = Playlist.FirstOrDefault(p => p.IsSelected);
        if (sel != null && Playlist.IndexOf(sel) != CurrentIndex)
            PlayPauseIcon = "\u25B6"; // ▶ — 다른 곡 선택됨, 누르면 재생
        else
            PlayPauseIcon = _audio.Status == PlaybackStatus.Playing ? "\u25AE\u25AE" : "\u25B6";
    }

    // ── Playback Commands ──

    [RelayCommand]
    private void PlayPause()
    {
        // 선택된 곡이 현재 재생 곡과 다르면 해당 곡 재생
        var selected = Playlist.FirstOrDefault(p => p.IsSelected);
        if (selected != null && Playlist.IndexOf(selected) != CurrentIndex)
        {
            PlayTrack(Playlist.IndexOf(selected));
            return;
        }

        if (_audio.Status == PlaybackStatus.Playing)
        {
            _audio.Pause();
            PlayPauseIcon = "\u25B6";
        }
        else if (_audio.Status == PlaybackStatus.Paused)
        {
            _audio.Play();
            PlayPauseIcon = "\u25AE\u25AE";
        }
        else if (Playlist.Count > 0)
        {
            PlayTrack(CurrentIndex >= 0 ? CurrentIndex : 0);
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _audio.Stop();
        _currentPlayingItem = null;
        PlayPauseIcon = "\u25B6";
        ProgressValue = 0;
        CurrentTimeText = "0:00";
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        if (Playlist.Count == 0) return;
        if (ShuffleMode && _playHistory.Count > 0)
        {
            PlayTrack(_playHistory.Pop());
            return;
        }
        int idx = CurrentIndex - 1;
        if (idx < 0) idx = Playlist.Count - 1;
        PlayTrack(idx);
    }

    [RelayCommand]
    private void NextTrack()
    {
        if (Playlist.Count == 0) return;
        if (ShuffleMode && Playlist.Count > 1)
        {
            int next;
            do { next = _rng.Next(Playlist.Count); } while (next == CurrentIndex);
            _playHistory.Push(CurrentIndex);
            PlayTrack(next);
            return;
        }
        int idx = CurrentIndex + 1;
        if (idx >= Playlist.Count)
            idx = RepeatAll ? 0 : Playlist.Count - 1;
        PlayTrack(idx);
    }

    public void PlayTrack(int index)
    {
        if (index < 0 || index >= Playlist.Count) return;
        SaveCurrentLyrics(); // 이전 곡 가사 저장
        CurrentIndex = index;
        // 재생 중인 트랙 하이라이트
        for (int i = 0; i < Playlist.Count; i++)
            Playlist[i].IsSelected = (i == index);
        _abPointA = TimeSpan.FromSeconds(-1);
        _abPointB = TimeSpan.FromSeconds(-1);
        AbInfoText = "A-B Off";
        OnPropertyChanged(nameof(IsPointASet));
        OnPropertyChanged(nameof(IsPointBSet));
        OnPropertyChanged(nameof(IsAbRangeVisible));
        NotifyAbRects();

        var item = Playlist[index];
        _currentPlayingItem = item; // 참조 저장 (리오더 시 인덱스 추적용)
        _audio.LoadAndPlay(item.FilePath);
        _audio.SetPitchTempo(Pitch, Tempo);

        NowPlayingText = item.DisplayName;
        PlayPauseIcon = "\u25AE\u25AE";
        LoadLyrics(item.FilePath);

        // 가사가 있으면 Lyrics 패널 자동 표시
        if (!string.IsNullOrEmpty(LyricsText))
        {
            ShowYoutube = false;
            ShowLyrics = true;
            if (!IsWideLayout) ShowPlaylist = false;
        }
    }

    public void SeekTo(double seconds)
    {
        _audio.SeekTo(TimeSpan.FromSeconds(seconds));
    }

    // ── Pitch / Tempo ──

    partial void OnPitchChanged(int value)
    {
        PitchText = value.ToString("+0;-0;0");
        _audio.SetPitchTempo(value, Tempo);
    }

    partial void OnTempoChanged(float value)
    {
        TempoText = $"{100 + (int)value}%";
        _audio.SetPitchTempo(Pitch, value);
    }

    [RelayCommand] private void PitchUp() { if (Pitch < 8) Pitch++; }
    [RelayCommand] private void PitchDown() { if (Pitch > -8) Pitch--; }
    [RelayCommand] private void PitchReset() => Pitch = 0;
    [RelayCommand] private void TempoUp() { if (Tempo < 100) Tempo++; }
    [RelayCommand] private void TempoDown() { if (Tempo > -100) Tempo--; }
    [RelayCommand] private void TempoReset() => Tempo = 0;
    [RelayCommand] private void ResetAll() { Pitch = 0; Tempo = 0; }

    // ── MP3 Export ──

    [RelayCommand]
    private async Task ExportMp3()
    {
        if (IsExporting) return;
        if (CurrentIndex < 0 || CurrentIndex >= Playlist.Count)
        {
            await Shell.Current.DisplayAlert("MP3 내보내기", "재생 중인 파일이 없습니다.", "확인");
            return;
        }

        string inputPath = Playlist[CurrentIndex].FilePath;
        string baseName  = Path.GetFileNameWithoutExtension(inputPath);
        string pitchStr  = Pitch == 0 ? "" : $"_P{Pitch:+0;-0}";
        string tempoStr  = (int)Tempo == 0 ? "" : $"_T{100 + (int)Tempo}";
        string outName   = $"{baseName}{pitchStr}{tempoStr}.mp3";

        // Save to Music folder (no permission needed on API 29+)
#if ANDROID
        string musicDir = global::Android.OS.Environment
            .GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryMusic)!.AbsolutePath;
#else
        string musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
#endif
        Directory.CreateDirectory(musicDir);
        string outputPath = Path.Combine(musicDir, outName);

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusText = "변환 중...";

        var cts = new CancellationTokenSource();
        try
        {
            var prog = new Progress<int>(p =>
            {
                ExportProgress = p;
                ExportStatusText = $"변환 중... {p}%";
            });

            string result = await _audio.ExportMp3Async(inputPath, Pitch, Tempo, outputPath, prog, cts.Token);

            ExportStatusText = "완료!";
            ExportProgress = 100;

            // 내보낸 파일을 플레이리스트에 자동 추가
            if (Playlist.All(p => p.FilePath != result))
                Playlist.Add(new PlaylistItem { FilePath = result });

            // Share the file
#if ANDROID
            ShareExportedFile(result);
#endif
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "취소됨";
            await Shell.Current.DisplayAlert("MP3 내보내기", "취소되었습니다.", "확인");
        }
        catch (Exception ex)
        {
            ExportStatusText = $"오류";
            await Shell.Current.DisplayAlert("MP3 내보내기 오류", ex.Message, "확인");
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task ExportMp3HQ()
    {
        if (IsExporting) return;
        if (CurrentIndex < 0 || CurrentIndex >= Playlist.Count)
        {
            await Shell.Current.DisplayAlert("HQ 내보내기", "재생 중인 파일이 없습니다.", "확인");
            return;
        }

        string inputPath = Playlist[CurrentIndex].FilePath;
        string baseName  = Path.GetFileNameWithoutExtension(inputPath);
        string pitchStr  = Pitch == 0 ? "" : $"_P{Pitch:+0;-0}";
        string tempoStr  = (int)Tempo == 0 ? "" : $"_T{100 + (int)Tempo}";
        string outName   = $"{baseName}_HQ{pitchStr}{tempoStr}.mp3";

#if ANDROID
        string musicDir = global::Android.OS.Environment
            .GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryMusic)!.AbsolutePath;
#else
        string musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
#endif
        Directory.CreateDirectory(musicDir);
        string outputPath = Path.Combine(musicDir, outName);

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusText = "HQ 변환 중...";

        var cts = new CancellationTokenSource();
        try
        {
            var prog = new Progress<int>(p =>
            {
                ExportProgress = p;
                ExportStatusText = $"HQ 변환 중... {p}%";
            });

            string result = await _audio.ExportMp3HQAsync(inputPath, Pitch, Tempo, outputPath, prog, cts.Token);

            ExportStatusText = "HQ 완료!";
            ExportProgress = 100;

            if (Playlist.All(p => p.FilePath != result))
                Playlist.Add(new PlaylistItem { FilePath = result });

#if ANDROID
            ShareExportedFile(result);
#endif
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "취소됨";
            await Shell.Current.DisplayAlert("HQ 내보내기", "취소되었습니다.", "확인");
        }
        catch (Exception ex)
        {
            ExportStatusText = $"오류";
            await Shell.Current.DisplayAlert("HQ 내보내기 오류", ex.Message, "확인");
        }
        finally
        {
            IsExporting = false;
        }
    }

#if ANDROID
    private static void ShareExportedFile(string filePath)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var file = new Java.IO.File(filePath);
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx, ctx.PackageName + ".fileprovider", file);

            var shareIntent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
            shareIntent.SetType("audio/mpeg");
            shareIntent.PutExtra(global::Android.Content.Intent.ExtraStream, uri);
            shareIntent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);

            var chooser = global::Android.Content.Intent.CreateChooser(shareIntent, "MP3 공유/저장");
            chooser!.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(chooser);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("JIP", $"Share failed: {ex.Message}");
        }
    }
#endif

    // ── Export Folder ──

    [RelayCommand]
    private void OpenExportFolder()
    {
#if ANDROID
        try
        {
            // Music 폴더를 Files 앱에서 열기
            var uri = global::Android.Provider.DocumentsContract.BuildDocumentUri(
                "com.android.externalstorage.documents", "primary:Music");
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, "vnd.android.document/directory");
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            try
            {
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch
            {
                // Fallback: Files 앱 직접 실행
                var ctx = global::Android.App.Application.Context;
                var pm = ctx.PackageManager;
                var fallback = pm?.GetLaunchIntentForPackage("com.google.android.documentsui");
                if (fallback != null)
                {
                    fallback.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                    ctx.StartActivity(fallback);
                }
            }
        }
        catch { }
#endif
    }

    // ── Volume ──

    [ObservableProperty] private bool isMuted;
    private double _volumeBeforeMute = 100;
    public string VolumeIcon => IsMuted ? "🔇" : "🔊";

    partial void OnVolumeChanged(double value)
    {
        VolumeText = $"{(int)value}%";
        _audio.Volume = (float)(value / 100.0);
        if (value > 0 && IsMuted) { IsMuted = false; OnPropertyChanged(nameof(VolumeIcon)); }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            Volume = _volumeBeforeMute;
            IsMuted = false;
        }
        else
        {
            _volumeBeforeMute = Volume > 0 ? Volume : 50;
            Volume = 0;
            IsMuted = true;
        }
        OnPropertyChanged(nameof(VolumeIcon));
    }

    public void SyncVolumeFromSystem()
    {
        var sys = _audio.Volume; // 0.0~1.0 from AudioManager
        Volume = Math.Round(sys * 100);
    }

    // ── AB Repeat ──

    [RelayCommand]
    private void SetPointA()
    {
        _abPointA = _audio.Position;
        _abPointB = TimeSpan.FromSeconds(-1);
        AbInfoText = $"A: {FormatTime(_abPointA)}";
        OnPropertyChanged(nameof(IsPointASet));
        OnPropertyChanged(nameof(IsPointBSet));
        OnPropertyChanged(nameof(IsAbRangeVisible));
        NotifyAbRects();
    }

    [RelayCommand]
    private void SetPointB()
    {
        if (_abPointA.TotalSeconds < 0) return;
        var pos = _audio.Position;
        if (pos <= _abPointA) return;
        _abPointB = pos;
        AbInfoText = $"A: {FormatTime(_abPointA)} ~ {FormatTime(_abPointB)}";
        OnPropertyChanged(nameof(IsPointBSet));
        OnPropertyChanged(nameof(IsAbRangeVisible));
        NotifyAbRects();
    }

    [RelayCommand]
    private void ClearAB()
    {
        _abPointA = TimeSpan.FromSeconds(-1);
        _abPointB = TimeSpan.FromSeconds(-1);
        AbInfoText = "A-B Off";
        OnPropertyChanged(nameof(IsPointASet));
        OnPropertyChanged(nameof(IsPointBSet));
        OnPropertyChanged(nameof(IsAbRangeVisible));
        NotifyAbRects();
    }

    // ── Repeat / Shuffle ──

    // 반복 아이콘/배경 (Off → 1곡 → 전체 → Off)
    public string RepeatIcon => RepeatOne ? "1 Rep" : "Repeat";
    public Color RepeatBgColor => (RepeatOne || RepeatAll) ? Color.FromArgb("#4A9EFF") : Color.FromArgb("#2D2D30");

    [RelayCommand]
    private void CycleRepeat()
    {
        if (!RepeatOne && !RepeatAll)
        {
            // Off → 1곡 반복
            RepeatOne = true; RepeatAll = false;
        }
        else if (RepeatOne)
        {
            // 1곡 → 전체 반복
            RepeatOne = false; RepeatAll = true;
        }
        else
        {
            // 전체 → Off
            RepeatOne = false; RepeatAll = false;
        }
        OnPropertyChanged(nameof(RepeatIcon));
        OnPropertyChanged(nameof(RepeatBgColor));
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        ShuffleMode = !ShuffleMode;
        if (ShuffleMode) { RepeatOne = false; _playHistory.Clear(); OnPropertyChanged(nameof(RepeatIcon)); OnPropertyChanged(nameof(RepeatBgColor)); }
    }

    // ── Playlist ──

    [RelayCommand]
    private async Task AddFiles()
    {
        var result = await FilePicker.PickMultipleAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.Android, new[] { "audio/*" } }
            }),
            PickerTitle = IsKorean ? "오디오 파일 선택" : "Select audio files"
        });

        if (result == null) return;
        foreach (var file in result)
        {
            if (Playlist.All(p => p.FilePath != file.FullPath))
                Playlist.Add(new PlaylistItem { FilePath = file.FullPath });
        }
    }

    [RelayCommand]
    private async Task AddFolderToPlaylist()
    {
        try
        {
            var picked = await CommunityToolkit.Maui.Storage.FolderPicker.Default.PickAsync(default);
            if (!picked.IsSuccessful || picked.Folder == null) return;
            var folder = picked.Folder.Path;
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())
                    && Playlist.All(p => p.FilePath != file))
                    Playlist.Add(new PlaylistItem { FilePath = file });
            }
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveTrack(PlaylistItem? item)
    {
        if (item == null) return;
        int idx = Playlist.IndexOf(item);
        Playlist.Remove(item);
        if (idx == CurrentIndex) StopPlayback();
        else if (idx < CurrentIndex) CurrentIndex--;
    }

    public void PlaylistItemTapped(int index) => PlayTrack(index);

    [RelayCommand]
    private void RemoveSelectedTracks()
    {
        var selected = Playlist.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var item in selected)
        {
            int idx = Playlist.IndexOf(item);
            Playlist.Remove(item);
            if (idx == CurrentIndex) StopPlayback();
            else if (idx < CurrentIndex) CurrentIndex--;
        }
    }

    // ── Lyrics ──

    private static string LyricsFilePath(string trackPath)
    {
        var dir = Path.Combine(FileSystem.AppDataDirectory, "Lyrics");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(trackPath)));
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(trackPath));
        if (name.Length > 40) name = name[..40];
        return Path.Combine(dir, $"{name}_{hash}.txt");
    }

    private void LoadLyrics(string filePath)
    {
        var lf = LyricsFilePath(filePath);
        if (File.Exists(lf))
        {
            LyricsText = File.ReadAllText(lf);
            return;
        }
        // 별도 파일 없으면 MP3 태그에서 읽기 (하위 호환)
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            LyricsText = tagFile.Tag.Lyrics ?? "";
        }
        catch { LyricsText = ""; }
    }

    private void SaveCurrentLyrics()
    {
        if (CurrentIndex < 0 || CurrentIndex >= Playlist.Count) return;
        try { File.WriteAllText(LyricsFilePath(Playlist[CurrentIndex].FilePath), LyricsText); }
        catch { }
    }

    [RelayCommand] private void LyricsFontUp() { if (LyricsFontSize < 48) LyricsFontSize += 2; }
    [RelayCommand] private void LyricsFontDown() { if (LyricsFontSize > 8) LyricsFontSize -= 2; }

    [RelayCommand]
    private void ToggleLyricsEdit()
    {
        if (LyricsEditMode) SaveCurrentLyrics(); // Done 누를 때 저장
        LyricsEditMode = !LyricsEditMode;
    }

    [RelayCommand]
    private async Task CopyAllLyrics()
    {
        if (!string.IsNullOrEmpty(LyricsText))
            await Clipboard.Default.SetTextAsync(LyricsText);
    }

    [RelayCommand]
    private async Task PasteLyrics()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (text != null)
            LyricsText = text;
    }

    // ── Library ──

    [RelayCommand]
    private async Task AddLibraryFolder()
    {
        try
        {
            var result = await CommunityToolkit.Maui.Storage.FolderPicker.Default.PickAsync(default);
            if (result.IsSuccessful && result.Folder != null)
            {
                var path = result.Folder.Path;
                if (!LibraryFolders.Contains(path))
                {
                    LibraryFolders.Add(path);
                    ScanLibrary();
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveLibraryFolder(string? folder)
    {
        if (folder == null) return;
        LibraryFolders.Remove(folder);
        ScanLibrary();
    }

    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac"];

    public void ScanLibrary()
    {
        LibraryFiles.Clear();
        foreach (var folder in LibraryFolders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    if (AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        LibraryFiles.Add(file);
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    private void AddLibraryFileToPlaylist(string? filePath)
    {
        if (filePath == null) return;
        if (Playlist.All(p => p.FilePath != filePath))
            Playlist.Add(new PlaylistItem { FilePath = filePath });
    }

    // ── YouTube Download ──

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadYoutube()
    {
        if (string.IsNullOrWhiteSpace(YoutubeUrl)) return;

        IsDownloading = true;
        YoutubeProgress = 0;
        YoutubeStatusText = "영상 정보 가져오는 중...";
        DownloadYoutubeCommand.NotifyCanExecuteChanged();

        try
        {
            var url = YoutubeUrl.Trim();
            if (url.Contains("&list="))
                url = url[..url.IndexOf("&list=")];
            if (url.Contains("&start_radio="))
                url = url[..url.IndexOf("&start_radio=")];

            // Force all network work off the UI thread (Android NetworkOnMainThreadException)
            (string filePath, string fileName) result = await Task.Run(async () =>
            {
                using var httpClient = new HttpClient();
                var youtube = new YoutubeClient(httpClient);

                var video = await youtube.Videos.GetAsync(url).ConfigureAwait(false);
                MainThread.BeginInvokeOnMainThread(() =>
                    YoutubeStatusText = $"\"{video.Title}\" 스트림 검색 중...");

                var manifest = await youtube.Videos.Streams.GetManifestAsync(video.Id).ConfigureAwait(false);
                var streamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate()
                    ?? throw new Exception("오디오 스트림 없음");

                var ext = streamInfo.Container.Name;
                var safeTitle = string.Join("_", video.Title.Split(Path.GetInvalidFileNameChars()));
                if (safeTitle.Length > 80) safeTitle = safeTitle[..80];
                var fileName = $"{safeTitle}.{ext}";
                var savePath = Path.Combine(FileSystem.AppDataDirectory, "Downloads");
                Directory.CreateDirectory(savePath);
                var filePath = Path.Combine(savePath, fileName);

                MainThread.BeginInvokeOnMainThread(() =>
                    YoutubeStatusText = $"다운로드 중: {video.Title}");

                var progress = new Progress<double>(p => MainThread.BeginInvokeOnMainThread(() =>
                {
                    YoutubeProgress = p * 100;
                    YoutubeStatusText = $"다운로드 중... {p:P0}";
                }));

                await youtube.Videos.Streams.DownloadAsync(streamInfo, filePath, progress).ConfigureAwait(false);
                return (filePath, fileName);
            });

            YoutubeProgress = 100;
            YoutubeStatusText = $"완료: {result.fileName}";
            if (Playlist.All(p => p.FilePath != result.filePath))
                Playlist.Add(new PlaylistItem { FilePath = result.filePath });
            YoutubeUrl = "";
        }
        catch (Exception ex)
        {
            YoutubeStatusText = $"오류: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            DownloadYoutubeCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDownload() => !IsDownloading;

    // ── Natural End ──

    private void OnPlaybackEnded()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (RepeatOne)
            {
                PlayTrack(CurrentIndex);
            }
            else if (ShuffleMode && Playlist.Count > 1)
            {
                int next;
                do { next = _rng.Next(Playlist.Count); } while (next == CurrentIndex);
                _playHistory.Push(CurrentIndex);
                PlayTrack(next);
            }
            else if (CurrentIndex + 1 < Playlist.Count)
            {
                PlayTrack(CurrentIndex + 1);
            }
            else if (RepeatAll && Playlist.Count > 0)
            {
                PlayTrack(0);
            }
            else
            {
                PlayPauseIcon = "\u25B6";
                ProgressValue = 0;
                CurrentTimeText = "0:00";
            }
        });
    }

    // ── Settings ──

    private void ApplySettings()
    {
        // Volume은 SyncVolumeFromSystem()에서 시스템 값 읽음 (시스템 볼륨 유지)
        Pitch = _settings.Pitch;
        Tempo = _settings.Tempo;
        RepeatOne = _settings.RepeatOne;
        RepeatAll = _settings.RepeatAll;
        ShuffleMode = _settings.Shuffle;
        LyricsFontSize = _settings.LyricsFontSize;
        IsKorean = _settings.Language == "ko";

        foreach (var f in _settings.LibraryFolders)
            LibraryFolders.Add(f);
        foreach (var p in _settings.Playlist)
        {
            // content:// URIs (FilePicker on Android) can't be checked with File.Exists
            if (p.StartsWith("content://") || p.StartsWith("file://") || File.Exists(p))
                Playlist.Add(new PlaylistItem { FilePath = p });
        }
        ScanLibrary();

        // 리스트에 파일 있으면 첫 번째 항목 자동 선택
        if (Playlist.Count > 0)
            Playlist[0].IsSelected = true;
    }

    public void SaveSettings()
    {
        _settings.Pitch = Pitch;
        _settings.Tempo = Tempo;
        _settings.RepeatOne = RepeatOne;
        _settings.RepeatAll = RepeatAll;
        _settings.Shuffle = ShuffleMode;
        _settings.LyricsFontSize = LyricsFontSize;
        _settings.Language = IsKorean ? "ko" : "en";
        _settings.LibraryFolders = [.. LibraryFolders];
        _settings.Playlist = Playlist.Select(p => p.FilePath).ToList();
        _settings.Save();
    }

    // ── Playlist Save/Load ──

    private static string PlaylistsDir =>
        Path.Combine(FileSystem.AppDataDirectory, "Playlists");

    private static string DownloadsDir =>
        Path.Combine(FileSystem.AppDataDirectory, "Downloads");

    private static bool IsFileAccessible(string path) =>
        path.StartsWith("content://") || path.StartsWith("file://") || File.Exists(path);

    public string[] GetSavedPlaylistNames()
    {
        Directory.CreateDirectory(PlaylistsDir);
        return Directory.GetFiles(PlaylistsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n)
            .ToArray();
    }

    public void SaveNamedPlaylist(string name)
    {
        Directory.CreateDirectory(PlaylistsDir);
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(safe)) return;
        var json = JsonSerializer.Serialize(
            Playlist.Select(p => p.FilePath).ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(PlaylistsDir, $"{safe}.json"), json);
    }

    public void LoadNamedPlaylist(string name)
    {
        var path = Path.Combine(PlaylistsDir, $"{name}.json");
        if (!File.Exists(path)) return;
        try
        {
            var tracks = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
            Playlist.Clear();
            foreach (var t in tracks)
                if (IsFileAccessible(t))
                    Playlist.Add(new PlaylistItem { FilePath = t });
        }
        catch { }
    }

    // Downloads 폴더의 모든 파일
    public string[] GetAllDownloadedFiles()
    {
        if (!Directory.Exists(DownloadsDir)) return [];
        return Directory.GetFiles(DownloadsDir)
            .OrderBy(Path.GetFileNameWithoutExtension)
            .ToArray();
    }

    // Downloads 폴더의 모든 파일 (플레이리스트 미포함 파일만)
    public string[] GetDownloadedFilesNotInPlaylist()
    {
        if (!Directory.Exists(DownloadsDir)) return [];
        var inPlaylist = new HashSet<string>(Playlist.Select(p => p.FilePath));
        return Directory.GetFiles(DownloadsDir)
            .Where(f => !inPlaylist.Contains(f))
            .OrderBy(Path.GetFileNameWithoutExtension)
            .ToArray();
    }

    public void AddFilesToPlaylist(IEnumerable<string> filePaths)
    {
        foreach (var f in filePaths)
            if (Playlist.All(p => p.FilePath != f))
                Playlist.Add(new PlaylistItem { FilePath = f });
    }

    public void RemoveFileFromPlaylist(string filePath)
    {
        var item = Playlist.FirstOrDefault(p => p.FilePath == filePath);
        if (item != null) Playlist.Remove(item);
    }

    public string[] GetOrphanedDownloads()
    {
        if (!Directory.Exists(DownloadsDir)) return [];
        var inPlaylist = new HashSet<string>(Playlist.Select(p => p.FilePath));
        return Directory.GetFiles(DownloadsDir)
            .Where(f => !inPlaylist.Contains(f))
            .ToArray();
    }

    public void DeleteFiles(IEnumerable<string> filePaths)
    {
        foreach (var f in filePaths)
            try { File.Delete(f); } catch { }
    }

    // ── Demucs Accompaniment Extraction ──

    [ObservableProperty] private bool isExtracting;
    [ObservableProperty] private int extractionProgress;
    [ObservableProperty] private string extractionStatusText = "";
    private CancellationTokenSource? _extractCts;

    private const string ModelFileName = "ggml-model-htdemucs-4s-f16.bin";
    // Official model from demucs.cpp author (Retrobear on HuggingFace)
    private const string ModelUrl =
        "https://huggingface.co/datasets/Retrobear/demucs.cpp/resolve/main/" + ModelFileName;

    private static string ModelsDir   => Path.Combine(FileSystem.AppDataDirectory, "Models");
    private static string StemsDir    => Path.Combine(FileSystem.AppDataDirectory, "Stems");

#if ANDROID
    private InstPlayerApp.Platforms.Android.DemucsProcessorBridge? _demucsBridge;
#endif

    [RelayCommand]
    private void CancelExtraction()
    {
        _extractCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAccompaniment()
    {
        var item = Playlist.FirstOrDefault(p => p.IsSelected)
                   ?? (CurrentIndex >= 0 && CurrentIndex < Playlist.Count
                       ? Playlist[CurrentIndex] : null);
        if (item == null)
        {
            await Shell.Current.DisplayAlert("반주 추출", "추출할 트랙을 선택하세요.", "확인");
            return;
        }

        // 확인 다이얼로그
        bool confirmed = await Shell.Current.DisplayAlert(
            "반주 추출",
            "고성능 반주추출기는 기기의 성능에 따라 20~30분 정도 소요되는 작업입니다.\n\n" +
            "추출 작업을 진행하면서 다른 작업도 기기 성능에 따라 가능합니다.\n\n" +
            "진행하시겠습니까?",
            "진행", "취소");
        if (!confirmed) return;

        IsExtracting = true;
        ExtractionProgress = 0;
        ExtractionStatusText = "준비 중...";
        ExtractAccompanimentCommand.NotifyCanExecuteChanged();
#if ANDROID
        Android.App.Application.Context.StartForegroundService(
            new Android.Content.Intent(Android.App.Application.Context,
                typeof(ExtractionForegroundService)));
#endif

        _extractCts = new CancellationTokenSource();
        var cts = _extractCts;
        try
        {
#if ANDROID
            // 1. Ensure model exists
            ExtractionStatusText = "모델 확인 중...";
            string? modelPath = await EnsureDemucsModelAsync(cts.Token);
            if (modelPath == null) { ExtractionStatusText = "모델 없음"; return; }

            // 2. Init bridge (loads model once, kept until app exit)
            _demucsBridge ??= new Platforms.Android.DemucsProcessorBridge();
            ExtractionStatusText = "모델 로딩 중...";
            bool ok = await Task.Run(() => _demucsBridge.InitModel(modelPath));
            if (!ok)
            {
                await Shell.Current.DisplayAlert("반주 추출 오류", "모델 로드 실패.", "확인");
                ExtractionStatusText = "모델 로드 실패";
                return;
            }

            // 3. Output directory (hash of input path)
            string hash = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(item.FilePath)))[..8];
            string outDir = Path.Combine(StemsDir, hash);
            Directory.CreateDirectory(outDir);

            // 4. Separate (blocking on Java background thread, polls every 500ms)
            ExtractionStatusText = "반주 추출 중... (1~5분 소요)";
            var prog = new Progress<int>(p =>
            {
                ExtractionProgress = p;
                ExtractionStatusText = $"반주 추출 중... {p}%";
#if ANDROID
                ExtractionForegroundService.Instance?.UpdateProgress(p);
#endif
            });
            string wavPath = await _demucsBridge.SeparateAsync(item.FilePath, outDir, prog, cts.Token);

            // 5. WAV → MP3 via existing pipeline
            ExtractionStatusText = "MP3 변환 중...";
            string mp3Name = Path.GetFileNameWithoutExtension(item.FilePath) + "_반주.mp3";
            string mp3Path = Path.Combine(DownloadsDir, mp3Name);
            Directory.CreateDirectory(DownloadsDir);

            var mp3Prog = new Progress<int>(p =>
            {
                ExtractionProgress = p;
                ExtractionStatusText = $"MP3 변환 중... {p}%";
            });
            await _audio.ExportMp3Async(wavPath, 0, 0, mp3Path, mp3Prog, cts.Token);

            // 6. Replace original in playlist with accompaniment
            int origIdx = Playlist.IndexOf(item);
            var newItem = new PlaylistItem { FilePath = mp3Path };
            if (origIdx >= 0) { Playlist.Remove(item); Playlist.Insert(origIdx, newItem); }
            else              { Playlist.Add(newItem); }

            ExtractionProgress = 100;
            ExtractionStatusText = "완료!";
            await Shell.Current.DisplayAlert("반주 추출 완료",
                $"반주 파일이 추가되었습니다:\n{mp3Name}", "확인");
#else
            await Shell.Current.DisplayAlert("지원 안 됨", "반주 추출은 Android에서만 지원됩니다.", "확인");
            await Task.CompletedTask;
#endif
        }
        catch (OperationCanceledException)
        {
            ExtractionStatusText = "취소됨";
        }
        catch (Exception ex)
        {
            ExtractionStatusText = "오류";
            await Shell.Current.DisplayAlert("반주 추출 오류", ex.Message, "확인");
        }
        finally
        {
            IsExtracting = false;
            ExtractAccompanimentCommand.NotifyCanExecuteChanged();
#if ANDROID
            Android.App.Application.Context.StopService(
                new Android.Content.Intent(Android.App.Application.Context,
                    typeof(ExtractionForegroundService)));
#endif
        }
    }

    private bool CanExtract() => !IsExtracting;

#if ANDROID
    private async Task<string?> EnsureDemucsModelAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDir);
        string modelPath = Path.Combine(ModelsDir, ModelFileName);
        if (File.Exists(modelPath)) return modelPath;

        bool dl = await Shell.Current.DisplayAlert(
            "AI 모델 다운로드",
            "반주 추출에는 AI 모델 (81MB)이 필요합니다.\n와이파이 연결 상태에서 다운로드 하시겠습니까?",
            "다운로드", "취소");
        if (!dl) return null;

        try
        {
            ExtractionStatusText = "모델 다운로드 중...";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            string tmp = modelPath + ".tmp";
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create);
            var buf = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                {
                    int pct = (int)(downloaded * 100L / total);
                    ExtractionProgress = pct;
                    ExtractionStatusText = $"모델 다운로드 중... {pct}%";
                }
            }
            File.Move(tmp, modelPath, overwrite: true);
            return modelPath;
        }
        catch (Exception ex)
        {
            try { File.Delete(modelPath + ".tmp"); } catch { }
            await Shell.Current.DisplayAlert("다운로드 오류", ex.Message, "확인");
            return null;
        }
    }
#endif

    // ── Helpers ──

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ── Language ──

    public string S(string ko, string en) => IsKorean ? ko : en;

    [RelayCommand]
    private void ToggleLanguage()
    {
        IsKorean = !IsKorean;
        OnPropertyChanged(nameof(IsKorean));
    }

    public void Dispose()
    {
        SaveSettings();
        _progressTimer?.Stop();
        _audio.Dispose();
#if ANDROID
        _demucsBridge?.Dispose();
#endif
    }
}

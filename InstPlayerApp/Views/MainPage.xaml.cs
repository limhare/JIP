using InstPlayerApp.Models;
using InstPlayerApp.Services;
using InstPlayerApp.ViewModels;

namespace InstPlayerApp.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;
    private string? _lastClipboardUrl;
    private bool _isOfferingDownload; // 다운로드 다이얼로그 중복 방지
    private bool _orientationChangePending;
    private bool _pendingIsWide;

    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _vm.StartTimer(Dispatcher);
        _vm.SyncVolumeFromSystem();

        // 앱이 실행 중일 때 공유 이벤트 수신 (OnNewIntent → ShareReceiver.NotifyUrl)
        // 중복 등록 방지: 먼저 제거 후 등록
        ShareReceiver.UrlShared -= OnUrlShared;
        ShareReceiver.UrlShared += OnUrlShared;

        // Cold-start: HandleIntent가 이미 PendingUrl을 설정해둔 경우
        if (ShareReceiver.PendingUrl is string sharedUrl)
        {
            ShareReceiver.PendingUrl = null;
            // 공유 인텐트는 사용자가 이미 다운로드 의도를 표명한 것 → 다이얼로그 없이 즉시 실행
            // MAUI 렌더링 완료 대기 후 실행
            await Task.Delay(300);
            StartYoutubeDownload(sharedUrl);
            return;
        }

        // 클립보드에서 유튜브 URL 감지
        await CheckClipboardForYoutubeUrl();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ShareReceiver.UrlShared -= OnUrlShared;
        _vm.SaveCurrentLyrics(); // 편집 중이던 가사 유실 방지
        _vm.SaveSettings();
    }

    // 앱 실행 중 공유 수신 (OnNewIntent 경유) → 즉시 다운로드
    private void OnUrlShared(string url)
    {
        ShareReceiver.PendingUrl = null;
        StartYoutubeDownload(url);
    }

    private async Task CheckClipboardForYoutubeUrl()
    {
        try
        {
            // 앱 포커스 확보 대기 (Android 10+ 클립보드 접근 제한)
            await Task.Delay(400);
            if (!Clipboard.Default.HasText) return;
            var text = (await Clipboard.Default.GetTextAsync())?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            var url = ExtractYoutubeUrl(text);
            if (url == null) return;
            if (url == _lastClipboardUrl) return;
            _lastClipboardUrl = url;
            // 클립보드 비우기 → 앱 재진입 시 반복 감지 방지
            await Clipboard.Default.SetTextAsync(string.Empty);
            await OfferYoutubeDownload(url);
        }
        catch { }
    }

    // 공유 인텐트 경유: 다이얼로그 없이 바로 다운로드 시작
    private void StartYoutubeDownload(string url)
    {
        if (_isOfferingDownload) return;
        _vm.YoutubeUrl = url;
        _vm.ShowYoutube = true;
        _vm.ShowPlaylist = false;
        _vm.ShowLyrics = false;
        if (_vm.DownloadYoutubeCommand.CanExecute(null))
            _vm.DownloadYoutubeCommand.Execute(null);
    }

    private async Task OfferYoutubeDownload(string url)
    {
        if (_isOfferingDownload) return; // 중복 방지
        _isOfferingDownload = true;
        try
        {
            bool ok = await DisplayAlert("유튜브 다운로드", "유튜브 주소가 감지되었습니다.\n다운로드할까요?", "다운로드", "취소");
            if (!ok) return;
            _vm.YoutubeUrl = url;
            _vm.ShowYoutube = true;
            _vm.ShowPlaylist = false;
            _vm.ShowLyrics = false;
            if (_vm.DownloadYoutubeCommand.CanExecute(null))
                _vm.DownloadYoutubeCommand.Execute(null);
        }
        finally
        {
            _isOfferingDownload = false;
        }
    }

    // "Video Title https://youtu.be/XXXXX" 형식에서 URL만 추출
    private static string? ExtractYoutubeUrl(string text)
    {
        foreach (var part in text.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.Contains("youtube.com/watch") || part.Contains("youtu.be/"))
                return part;
        return null;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;
        _pendingIsWide = width > height;
        if (_orientationChangePending) return;
        _orientationChangePending = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () =>
        {
            _orientationChangePending = false;
            if (_vm.IsWideLayout == _pendingIsWide) return;
            _vm.IsWideLayout = _pendingIsWide;
            if (_pendingIsWide)
            {
                _vm.ShowPlaylist = true;
                if (!_vm.ShowLyrics && !_vm.ShowYoutube) _vm.ShowLyrics = true;
            }
            else
            {
                // Narrow: Playlist always visible in left column, ensure right panel is set
                if (!_vm.ShowLyrics && !_vm.ShowYoutube) _vm.ShowLyrics = true;
            }
        });
    }

    private void OnProgressSliderSizeChanged(object? sender, EventArgs e)
    {
        if (sender is Slider s) _vm.UpdateSliderSize(s.Width, s.Height);
    }

    private void OnProgressDragCompleted(object? sender, EventArgs e)
    {
        if (sender is Slider slider)
            _vm.SeekTo(slider.Value);
    }

    private void OnPitchDragCompleted(object? sender, EventArgs e)
    {
        if (sender is Slider slider)
            _vm.Pitch = (int)Math.Round(slider.Value);
    }

    private void OnPlaylistItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View view && view.BindingContext is PlaylistItem item)
        {
            // 모두 해제 후 이 항목 선택 (항상 하나 선택 유지)
            foreach (var p in _vm.Playlist)
                p.IsSelected = false;
            item.IsSelected = true;
            // PropertyChanged 이벤트 체인이 씹힐 수 있으므로 직접 갱신
            _vm.RefreshPlayPauseIcon();
        }
    }

    private void OnPlaylistItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View view && view.BindingContext is PlaylistItem item)
        {
            int idx = _vm.Playlist.IndexOf(item);
            if (idx >= 0) _vm.PlaylistItemTapped(idx);
        }
    }

    private void OnSwipeDeleteTrack(object? sender, EventArgs e)
    {
        if (sender is SwipeItem swipe && swipe.BindingContext is PlaylistItem item)
            _vm.RemoveTrackCommand.Execute(item);
    }

    private async void OnAddFiles(object? sender, EventArgs e)
    {
        var downloaded = _vm.GetAllDownloadedFiles();
        if (downloaded.Length > 0)
        {
            // 다운로드 파일이 있으면 모달 관리 페이지 표시
            await Navigation.PushModalAsync(
                new DownloadManagerPage(_vm, PickAudioFilesAsync));
        }
        else
        {
            // 다운로드 파일 없으면 바로 파일 피커
            var picked = await PickAudioFilesAsync();
            _vm.AddFilesToPlaylist(picked);
        }
    }

#if ANDROID
    private static async Task<string[]> PickAudioFilesAsync()
    {
        var activity = (MainActivity)Platform.CurrentActivity!;
        MainActivity.FilePickerTcs = new TaskCompletionSource<Android.Content.Intent?>();

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocument);
        intent.AddCategory(Android.Content.Intent.CategoryOpenable);
        intent.SetType("audio/*");
        intent.PutExtra(Android.Content.Intent.ExtraAllowMultiple, true);
        // 기본 경로: 외부 저장소 루트 (Recent 대신 파일 시스템 표시)
        intent.PutExtra("android.provider.extra.INITIAL_URI",
            Android.Net.Uri.Parse("content://com.android.externalstorage.documents/root/primary"));

        activity.StartActivityForResult(intent, MainActivity.FilePickRequestCode);

        var result = await MainActivity.FilePickerTcs.Task;
        if (result == null) return [];

        var uris = new List<string>();
        if (result.ClipData != null)
        {
            for (int i = 0; i < result.ClipData.ItemCount; i++)
            {
                var uri = result.ClipData.GetItemAt(i)?.Uri;
                if (uri != null)
                {
                    try { activity.ContentResolver?.TakePersistableUriPermission(uri, Android.Content.ActivityFlags.GrantReadUriPermission); } catch { }
                    uris.Add(uri.ToString()!);
                }
            }
        }
        else if (result.Data != null)
        {
            try { activity.ContentResolver?.TakePersistableUriPermission(result.Data, Android.Content.ActivityFlags.GrantReadUriPermission); } catch { }
            uris.Add(result.Data.ToString()!);
        }
        return [.. uris];
    }
#else
    private static async Task<string[]> PickAudioFilesAsync()
    {
        var result = await FilePicker.PickMultipleAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = ["audio/*"]
            })
        });
        return result?.Select(f => f.FullPath).ToArray() ?? [];
    }
#endif

    private async void OnSavePlaylist(object? sender, EventArgs e)
    {
        string? name = await DisplayPromptAsync(
            "플레이리스트 저장", "저장할 이름을 입력하세요",
            accept: "저장", cancel: "취소");
        if (!string.IsNullOrWhiteSpace(name))
            _vm.SaveNamedPlaylist(name);
    }

    private async void OnLoadPlaylist(object? sender, EventArgs e)
    {
        var names = _vm.GetSavedPlaylistNames();
        if (names.Length == 0)
        {
            await DisplayAlert("알림", "저장된 플레이리스트가 없습니다.", "확인");
            return;
        }
        string? chosen = await DisplayActionSheet("불러오기", "취소", null, names);
        if (!string.IsNullOrEmpty(chosen) && chosen != "취소")
            _vm.LoadNamedPlaylist(chosen);
    }

    private async void OnCleanupDownloads(object? sender, EventArgs e)
    {
        var orphans = _vm.GetOrphanedDownloads();
        if (orphans.Length == 0)
        {
            await DisplayAlert("정리", "삭제할 파일이 없습니다.", "확인");
            return;
        }
        long totalMB = orphans.Sum(f => new FileInfo(f).Length) / 1024 / 1024;
        bool ok = await DisplayAlert(
            "다운로드 파일 정리",
            $"{orphans.Length}개 파일 ({totalMB} MB)을 삭제할까요?\n현재 플레이리스트에 없는 파일만 삭제됩니다.",
            "삭제", "취소");
        if (ok) _vm.DeleteFiles(orphans);
    }

    private async void OnShowCredits(object? sender, EventArgs e)
    {
        await DisplayAlert("JIP",
            "Just Inst Player v1.1\n" +
            "\n" +
            "Vibe coded by Lim Jaewon\n" +
            "Built with Claude AI\n" +
            "\n" +
            ".NET MAUI 9 · Android\n" +
            "Last modified: 2026-03-09",
            "확인");
    }

    private void OnLyricsEditDone(object? sender, EventArgs e)
    {
        // Done 버튼 시 키보드 닫기
        LyricsEditorNarrow?.Unfocus();
        LyricsEditorWide?.Unfocus();
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity?.Window?.DecorView is Android.Views.View decorView)
        {
            var imm = (Android.Views.InputMethods.InputMethodManager?)
                activity.GetSystemService(Android.Content.Context.InputMethodService);
            imm?.HideSoftInputFromWindow(decorView.WindowToken, 0);
        }
#endif
    }

    private void OnMenuToggle(object? sender, EventArgs e)
    {
        if (_vm.IsWideLayout)
        {
            // Wide: Lyrics ↔ YouTube 전환 (Playlist는 항상 표시)
            bool showYt = !_vm.ShowYoutube;
            _vm.ShowLyrics = !showYt;
            _vm.ShowYoutube = showYt;
        }
        else
        {
            // Narrow: Playlist → Lyrics → YouTube → Playlist
            if (_vm.ShowPlaylist)
            {
                _vm.ShowPlaylist = false;
                _vm.ShowLyrics = true;
                _vm.ShowYoutube = false;
            }
            else if (_vm.ShowLyrics)
            {
                _vm.ShowPlaylist = false;
                _vm.ShowLyrics = false;
                _vm.ShowYoutube = true;
            }
            else
            {
                _vm.ShowPlaylist = true;
                _vm.ShowLyrics = false;
                _vm.ShowYoutube = false;
            }
        }
    }
}

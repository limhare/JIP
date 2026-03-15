namespace InstPlayerApp.Services;

public static class ShareReceiver
{
    public static string? PendingUrl { get; set; }

    // 앱이 이미 실행 중일 때 OnNewIntent → MainPage로 직접 전달
    public static event Action<string>? UrlShared;

    public static void NotifyUrl(string url)
    {
        PendingUrl = url;
        UrlShared?.Invoke(url);
    }
}

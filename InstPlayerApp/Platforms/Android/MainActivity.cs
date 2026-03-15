using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using InstPlayerApp.Services;

namespace InstPlayerApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain",
    Label = "JIP")]
public class MainActivity : MauiAppCompatActivity
{
    internal static TaskCompletionSource<Intent?>? FilePickerTcs;
    internal const int FilePickRequestCode = 9901;

    protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == FilePickRequestCode)
            FilePickerTcs?.TrySetResult(resultCode == Android.App.Result.Ok ? data : null);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // PendingUrl을 먼저 설정: base.OnCreate 내부에서 OnAppearing이 호출되기 전에 세팅
        HandleIntent(Intent);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var ch = new NotificationChannel(
                ExtractionForegroundService.ChannelId, "반주 추출",
                NotificationImportance.Low);
            ch.SetShowBadge(false);
            ((NotificationManager)GetSystemService(NotificationService)!)
                .CreateNotificationChannel(ch);
        }
        base.OnCreate(savedInstanceState);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // 앱이 이미 실행 중 → 이벤트로 직접 알림
        HandleIntent(intent);
    }

    private static void HandleIntent(Intent? intent)
    {
        if (intent?.Action != Intent.ActionSend || intent.Type != "text/plain") return;
        var text = intent.GetStringExtra(Intent.ExtraText)?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var url = ExtractYoutubeUrl(text);
        if (url != null) ShareReceiver.NotifyUrl(url);
    }

    // "Video Title https://youtu.be/XXXXX" 형식에서 URL만 추출
    private static string? ExtractYoutubeUrl(string text)
    {
        foreach (var part in text.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.Contains("youtube.com/watch") || part.Contains("youtu.be/"))
                return part;
        return null;
    }
}

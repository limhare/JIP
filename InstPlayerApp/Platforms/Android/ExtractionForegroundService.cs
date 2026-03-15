using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace InstPlayerApp;

[Service(Name = "com.instplayer.app.ExtractionForegroundService",
         Exported = false)]
public class ExtractionForegroundService : Service
{
    public const string ChannelId = "extraction_channel";
    private const int NotifId = 42;

    public static ExtractionForegroundService? Instance { get; private set; }

    private PowerManager.WakeLock? _wakeLock;
    private NotificationManager? _nm;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Instance = this;
        _nm = (NotificationManager?)GetSystemService(NotificationService);

        var pm = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "JIP:extraction");
        _wakeLock?.Acquire(30 * 60 * 1000L); // 최대 30분

        // API 29+(Android 10)부터 foreground service type 지정 가능
        // FOREGROUND_SERVICE_TYPE_DATA_SYNC = 0x1
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotifId, BuildNotification("반주 추출 중...", 0),
                (Android.Content.PM.ForegroundService)0x1);
        else
            StartForeground(NotifId, BuildNotification("반주 추출 중...", 0));
        return StartCommandResult.NotSticky;
    }

    public void UpdateProgress(int pct)
        => _nm?.Notify(NotifId, BuildNotification($"반주 추출 중... {pct}%", pct));

    public override void OnDestroy()
    {
        Instance = null;
        _wakeLock?.Release();
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private Notification BuildNotification(string text, int pct)
        => new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("JIP - 반주 추출")
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetProgress(100, pct, pct == 0)
            .SetOngoing(true)
            .SetSilent(true)
            .Build();
}

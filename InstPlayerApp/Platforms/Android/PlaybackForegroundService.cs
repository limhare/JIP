using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace InstPlayerApp;

/// <summary>
/// 재생 중 화면이 꺼져도 앱이 얼지 않도록 하는 미디어 재생 포그라운드 서비스.
/// (차량/블루투스 재생 시 화면 꺼짐 대응)
/// </summary>
[Service(Name = "com.instplayer.app.PlaybackForegroundService",
         Exported = false)]
public class PlaybackForegroundService : Service
{
    private const int NotifId = 43;

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var pm = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "JIP:playback");
        _wakeLock?.Acquire(4 * 60 * 60 * 1000L); // 최대 4시간

        // FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK = 0x2
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotifId, BuildNotification(),
                (Android.Content.PM.ForegroundService)0x2);
        else
            StartForeground(NotifId, BuildNotification());
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _wakeLock?.Release();
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private Notification BuildNotification()
        => new NotificationCompat.Builder(this, ExtractionForegroundService.ChannelId)
            .SetContentTitle("JIP")
            .SetContentText("재생 중")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetOngoing(true)
            .SetSilent(true)
            .Build();
}

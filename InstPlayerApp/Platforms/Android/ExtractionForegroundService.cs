using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace InstPlayerApp;

/// <summary>
/// 다운로드·반주 추출처럼 오래 걸리는 작업 중 화면이 꺼져도 앱이 얼지 않도록 하는 dataSync 서비스.
/// 여러 작업이 동시에 쓰므로 직접 시작/중지하지 말고 ForegroundJobs.Acquire/Release를 쓸 것.
/// </summary>
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

        if (_wakeLock == null)
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "JIP:work");
            _wakeLock?.Acquire(3 * 60 * 60 * 1000L); // 최대 3시간 (느린 반주 추출 대비)
        }

        // API 29+(Android 10)부터 foreground service type 지정 가능
        // FOREGROUND_SERVICE_TYPE_DATA_SYNC = 0x1
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotifId, BuildNotification("작업 진행 중...", 0),
                (Android.Content.PM.ForegroundService)0x1);
        else
            StartForeground(NotifId, BuildNotification("작업 진행 중...", 0));
        return StartCommandResult.NotSticky;
    }

    public void UpdateProgress(int pct)
        => _nm?.Notify(NotifId, BuildNotification($"작업 진행 중... {pct}%", pct));

    public void UpdateText(string text, int pct)
        => _nm?.Notify(NotifId, BuildNotification(text, pct));

    public override void OnDestroy()
    {
        Instance = null;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private Notification BuildNotification(string text, int pct)
        => new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("JIP")
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetProgress(100, pct, pct == 0)
            .SetOngoing(true)
            .SetSilent(true)
            .Build();
}

/// <summary>
/// 녹음 중 화면이 꺼져도 마이크 입력이 끊기지 않도록 하는 microphone 타입 서비스.
/// (dataSync 서비스로는 백그라운드 마이크가 무음 처리된다)
/// </summary>
[Service(Name = "com.instplayer.app.RecordingForegroundService",
         Exported = false)]
public class RecordingForegroundService : Service
{
    private const int NotifId = 44;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notif = new NotificationCompat.Builder(this, ExtractionForegroundService.ChannelId)
            .SetContentTitle("JIP")
            .SetContentText("녹음 중")
            .SetSmallIcon(Android.Resource.Drawable.PresenceAudioOnline)
            .SetOngoing(true)
            .SetSilent(true)
            .Build();
        // FOREGROUND_SERVICE_TYPE_MICROPHONE = 0x80
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            StartForeground(NotifId, notif, (Android.Content.PM.ForegroundService)0x80);
        else
            StartForeground(NotifId, notif);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}

/// <summary>
/// 포그라운드 작업 서비스 시작/중지. 같은 서비스를 여러 작업이 동시에 쓰므로 참조 카운트로 관리하고,
/// 시작 실패(백그라운드 시작 제한 등)는 작업 자체를 막지 않도록 삼킨다.
/// </summary>
public static class ForegroundJobs
{
    private static readonly object Gate = new();
    private static int _workCount;
    private static int _recordCount;

    public static void AcquireWork() => Acquire(ref _workCount, typeof(ExtractionForegroundService));
    public static void ReleaseWork() => Release(ref _workCount, typeof(ExtractionForegroundService));
    public static void AcquireRecording() => Acquire(ref _recordCount, typeof(RecordingForegroundService));
    public static void ReleaseRecording() => Release(ref _recordCount, typeof(RecordingForegroundService));

    private static void Acquire(ref int count, Type service)
    {
        lock (Gate)
        {
            count++;
            if (count != 1) return;
        }
        try
        {
            var ctx = Android.App.Application.Context;
            ctx.StartForegroundService(new Intent(ctx, service));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("JIP", $"foreground service start failed ({service.Name}): {ex.Message}");
        }
    }

    private static void Release(ref int count, Type service)
    {
        lock (Gate)
        {
            if (count == 0) return;
            count--;
            if (count != 0) return;
        }
        try
        {
            var ctx = Android.App.Application.Context;
            ctx.StopService(new Intent(ctx, service));
        }
        catch
        {
        }
    }
}

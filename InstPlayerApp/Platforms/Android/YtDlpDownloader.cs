using Android.Runtime;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// C# bridge to YtDlpBridge Java class (yt-dlp via youtubedl-android).
/// PC 버전과 동일한 yt-dlp 파이프라인. 폴링 방식 (다른 브리지들과 동일 패턴).
/// </summary>
internal class YtDlpDownloader : IDisposable
{
    private const string BridgeClass = "com/instplayer/app/YtDlpBridge";

    private nint _objRef;
    private nint _classRef;
    private nint _startDownloadId;
    private nint _startUpdateId;
    private nint _isDoneId;
    private nint _getProgressId;
    private nint _getResultId;
    private nint _getErrorId;

    /// <summary>앱 시작 시 백그라운드 초기화 (최초 1회 python 추출 수 초)</summary>
    public static void InitInBackground()
    {
        try
        {
            var cls = JNIEnv.FindClass(BridgeClass);
            var mid = JNIEnv.GetStaticMethodID(cls, "initAsync", "(Landroid/content/Context;)V");
            JNIEnv.CallStaticVoidMethod(cls, mid, new JValue(global::Android.App.Application.Context));
        }
        catch
        {
        }
    }

    private void EnsureInit()
    {
        if (_objRef != nint.Zero) return;

        _classRef = JNIEnv.FindClass(BridgeClass);
        var ctor = JNIEnv.GetMethodID(_classRef, "<init>", "()V");
        var local = JNIEnv.NewObject(_classRef, ctor);
        _objRef = JNIEnv.NewGlobalRef(local);
        JNIEnv.DeleteLocalRef(local);

        _startDownloadId = JNIEnv.GetMethodID(_classRef, "startDownload",
            "(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V");
        _startUpdateId = JNIEnv.GetMethodID(_classRef, "startUpdate", "(Landroid/content/Context;)V");
        _isDoneId      = JNIEnv.GetMethodID(_classRef, "isDone",      "()Z");
        _getProgressId = JNIEnv.GetMethodID(_classRef, "getProgress", "()I");
        _getResultId   = JNIEnv.GetMethodID(_classRef, "getResult",   "()Ljava/lang/String;");
        _getErrorId    = JNIEnv.GetMethodID(_classRef, "getError",    "()Ljava/lang/String;");
    }

    public async Task DownloadAsync(string url, string outputTemplate, string audioFormat,
                                    IProgress<int>? progress = null, CancellationToken ct = default)
    {
        EnsureInit();
        using var jUrl = new Java.Lang.String(url);
        using var jTpl = new Java.Lang.String(outputTemplate);
        using var jFmt = new Java.Lang.String(audioFormat);
        JNIEnv.CallVoidMethod(_objRef, _startDownloadId,
            new JValue(global::Android.App.Application.Context),
            new JValue(jUrl), new JValue(jTpl), new JValue(jFmt));
        await PollAsync(progress, ct);
    }

    public async Task UpdateAsync(CancellationToken ct = default)
    {
        EnsureInit();
        JNIEnv.CallVoidMethod(_objRef, _startUpdateId,
            new JValue(global::Android.App.Application.Context));
        await PollAsync(null, ct);
    }

    private async Task PollAsync(IProgress<int>? progress, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(300, ct);
            bool done = JNIEnv.CallBooleanMethod(_objRef, _isDoneId);
            int pct = JNIEnv.CallIntMethod(_objRef, _getProgressId);
            progress?.Report(pct);
            if (done) break;
        }
        nint errH = JNIEnv.CallObjectMethod(_objRef, _getErrorId);
        string? error = errH != nint.Zero
            ? JNIEnv.GetString(errH, JniHandleOwnership.TransferLocalRef)
            : null;
        if (!string.IsNullOrEmpty(error)) throw new Exception(error);
    }

    public void Dispose()
    {
        if (_objRef != nint.Zero)
        {
            JNIEnv.DeleteGlobalRef(_objRef);
            _objRef = nint.Zero;
        }
    }
}

using Android.Runtime;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// C# bridge to AudioExporter Java class.
/// Calls startExport() then polls isDone()/getProgress() — no Java→C# callback needed.
/// </summary>
internal class AudioExportBridge : IDisposable
{
    private const string ExporterClass = "com/instplayer/app/AudioExporter";

    private nint _exporterRef;
    private nint _exporterClass;
    private nint _startExportId;
    private nint _startExportHQId;
    private nint _cancelId;
    private nint _isDoneId;
    private nint _getProgressId;
    private nint _getResultId;
    private nint _getErrorId;

    private void EnsureInit()
    {
        if (_exporterRef != nint.Zero) return;

        _exporterClass = JNIEnv.FindClass(ExporterClass);
        var ctor  = JNIEnv.GetMethodID(_exporterClass, "<init>", "()V");
        var local = JNIEnv.NewObject(_exporterClass, ctor);
        _exporterRef = JNIEnv.NewGlobalRef(local);
        JNIEnv.DeleteLocalRef(local);

        _startExportId   = JNIEnv.GetMethodID(_exporterClass, "startExport",
            "(Ljava/lang/String;FFLjava/lang/String;)V");
        _startExportHQId = JNIEnv.GetMethodID(_exporterClass, "startExportHQ",
            "(Ljava/lang/String;FFLjava/lang/String;)V");
        _cancelId      = JNIEnv.GetMethodID(_exporterClass, "cancel",      "()V");
        _isDoneId      = JNIEnv.GetMethodID(_exporterClass, "isDone",      "()Z");
        _getProgressId = JNIEnv.GetMethodID(_exporterClass, "getProgress", "()I");
        _getResultId   = JNIEnv.GetMethodID(_exporterClass, "getResult",   "()Ljava/lang/String;");
        _getErrorId    = JNIEnv.GetMethodID(_exporterClass, "getError",    "()Ljava/lang/String;");
    }

    public async Task<string> ExportAsync(
        string inputPath, float pitchSemitones, float tempoPercent,
        string outputPath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        EnsureInit();

        using var jInput  = new Java.Lang.String(inputPath);
        using var jOutput = new Java.Lang.String(outputPath);

        // Start export (Java background thread)
        JNIEnv.CallVoidMethod(_exporterRef, _startExportId,
            new JValue(jInput),
            new JValue(pitchSemitones),
            new JValue(tempoPercent),
            new JValue(jOutput));

        // Poll until done
        while (true)
        {
            await Task.Delay(200, ct);

            bool done = JNIEnv.CallBooleanMethod(_exporterRef, _isDoneId);
            int  pct  = JNIEnv.CallIntMethod(_exporterRef, _getProgressId);
            progress?.Report(pct);

            if (done) break;
        }

        // Get result or error
        nint errorHandle  = JNIEnv.CallObjectMethod(_exporterRef, _getErrorId);
        nint resultHandle = JNIEnv.CallObjectMethod(_exporterRef, _getResultId);

        string? error = errorHandle != nint.Zero
            ? JNIEnv.GetString(errorHandle, JniHandleOwnership.TransferLocalRef)
            : null;

        if (error != null) throw new Exception(error);

        string result = resultHandle != nint.Zero
            ? JNIEnv.GetString(resultHandle, JniHandleOwnership.TransferLocalRef)
            : outputPath;

        return result;
    }

    public async Task<string> ExportHQAsync(
        string inputPath, float pitchSemitones, float tempoPercent,
        string outputPath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        EnsureInit();

        using var jInput  = new Java.Lang.String(inputPath);
        using var jOutput = new Java.Lang.String(outputPath);

        JNIEnv.CallVoidMethod(_exporterRef, _startExportHQId,
            new JValue(jInput),
            new JValue(pitchSemitones),
            new JValue(tempoPercent),
            new JValue(jOutput));

        while (true)
        {
            await Task.Delay(200, ct);
            bool done = JNIEnv.CallBooleanMethod(_exporterRef, _isDoneId);
            int  pct  = JNIEnv.CallIntMethod(_exporterRef, _getProgressId);
            progress?.Report(pct);
            if (done) break;
        }

        nint errorHandle  = JNIEnv.CallObjectMethod(_exporterRef, _getErrorId);
        nint resultHandle = JNIEnv.CallObjectMethod(_exporterRef, _getResultId);

        string? error = errorHandle != nint.Zero
            ? JNIEnv.GetString(errorHandle, JniHandleOwnership.TransferLocalRef)
            : null;

        if (error != null) throw new Exception(error);

        string result = resultHandle != nint.Zero
            ? JNIEnv.GetString(resultHandle, JniHandleOwnership.TransferLocalRef)
            : outputPath;

        return result;
    }

    public void Cancel()
    {
        if (_exporterRef == nint.Zero) return;
        JNIEnv.CallVoidMethod(_exporterRef, _cancelId);
    }

    public void Dispose()
    {
        if (_exporterRef != nint.Zero)
        {
            JNIEnv.DeleteGlobalRef(_exporterRef);
            _exporterRef = nint.Zero;
        }
    }
}

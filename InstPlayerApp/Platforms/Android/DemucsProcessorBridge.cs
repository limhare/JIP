using Android.Runtime;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// C# bridge to DemucsProcessor Java class.
/// Loads the Demucs model once, then separates audio files on demand.
/// Polls isDone()/getProgress() until complete — no Java→C# callback needed.
/// </summary>
internal class DemucsProcessorBridge : IDisposable
{
    private const string ProcessorClass = "com/instplayer/app/DemucsProcessor";

    private nint _objRef;       // global ref to DemucsProcessor instance
    private nint _classRef;

    private nint _initId;
    private nint _startSeparateId;
    private nint _cancelId;
    private nint _isDoneId;
    private nint _getProgressId;
    private nint _getResultId;
    private nint _getErrorId;
    private nint _destroyId;

    private void EnsureInit()
    {
        if (_objRef != nint.Zero) return;

        _classRef = JNIEnv.FindClass(ProcessorClass);

        // Constructor takes Context
        var ctorId = JNIEnv.GetMethodID(_classRef, "<init>", "(Landroid/content/Context;)V");
        var ctx    = global::Android.App.Application.Context;
        var local  = JNIEnv.NewObject(_classRef, ctorId, new JValue(ctx));
        _objRef = JNIEnv.NewGlobalRef(local);
        JNIEnv.DeleteLocalRef(local);

        _initId          = JNIEnv.GetMethodID(_classRef, "init",          "(Ljava/lang/String;)Z");
        _startSeparateId = JNIEnv.GetMethodID(_classRef, "startSeparate", "(Ljava/lang/String;Ljava/lang/String;)V");
        _cancelId        = JNIEnv.GetMethodID(_classRef, "cancel",        "()V");
        _isDoneId        = JNIEnv.GetMethodID(_classRef, "isDone",        "()Z");
        _getProgressId   = JNIEnv.GetMethodID(_classRef, "getProgress",   "()I");
        _getResultId     = JNIEnv.GetMethodID(_classRef, "getResult",     "()Ljava/lang/String;");
        _getErrorId      = JNIEnv.GetMethodID(_classRef, "getError",      "()Ljava/lang/String;");
        _destroyId       = JNIEnv.GetMethodID(_classRef, "destroy",       "()V");
    }

    /// <summary>Load the Demucs model (blocking, run on background thread).</summary>
    public bool InitModel(string modelPath)
    {
        EnsureInit();
        using var jPath = new Java.Lang.String(modelPath);
        return JNIEnv.CallBooleanMethod(_objRef, _initId, new JValue(jPath));
    }

    /// <summary>
    /// Separate accompaniment from inputPath and write accompaniment.wav to outputDir.
    /// Returns the WAV file path on success.
    /// </summary>
    public async Task<string> SeparateAsync(
        string inputPath,
        string outputDir,
        IProgress<int>? progress = null,
        CancellationToken ct     = default)
    {
        EnsureInit();

        using var jInput  = new Java.Lang.String(inputPath);
        using var jOutDir = new Java.Lang.String(outputDir);

        // Start background separation
        JNIEnv.CallVoidMethod(_objRef, _startSeparateId,
            new JValue(jInput), new JValue(jOutDir));

        // Poll until done
        while (true)
        {
            await Task.Delay(500, ct);

            if (ct.IsCancellationRequested)
            {
                JNIEnv.CallVoidMethod(_objRef, _cancelId);
                ct.ThrowIfCancellationRequested();
            }

            bool done = JNIEnv.CallBooleanMethod(_objRef, _isDoneId);
            int  pct  = JNIEnv.CallIntMethod(_objRef, _getProgressId);
            progress?.Report(pct);

            if (done) break;
        }

        // Check error
        nint errHandle = JNIEnv.CallObjectMethod(_objRef, _getErrorId);
        string? error = errHandle != nint.Zero
            ? JNIEnv.GetString(errHandle, JniHandleOwnership.TransferLocalRef)
            : null;
        if (!string.IsNullOrEmpty(error)) throw new Exception(error);

        // Get result path
        nint resHandle = JNIEnv.CallObjectMethod(_objRef, _getResultId);
        string wavPath = resHandle != nint.Zero
            ? JNIEnv.GetString(resHandle, JniHandleOwnership.TransferLocalRef)!
            : Path.Combine(outputDir, "accompaniment.wav");

        return wavPath;
    }

    public void Cancel()
    {
        if (_objRef != nint.Zero)
            JNIEnv.CallVoidMethod(_objRef, _cancelId);
    }

    public void Dispose()
    {
        if (_objRef != nint.Zero)
        {
            JNIEnv.CallVoidMethod(_objRef, _destroyId);
            JNIEnv.DeleteGlobalRef(_objRef);
            _objRef = nint.Zero;
        }
    }
}

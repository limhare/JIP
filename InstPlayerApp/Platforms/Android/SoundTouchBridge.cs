using Android.Content;
using Android.Runtime;
using AndroidX.Media3.ExoPlayer;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// C# JNI bridge to SoundTouchExoPlayerBuilder Java class.
/// Creates ExoPlayer with SoundTouch audio processor injected.
/// </summary>
internal class SoundTouchBridge : IDisposable
{
    private nint _builderRef;
    private nint _builderClass;
    private nint _setPitchMethod;
    private nint _setTempoMethod;

    private const string BuilderClassName = "com/instplayer/app/SoundTouchExoPlayerBuilder";
    private const string PlayerClassName  = "androidx/media3/exoplayer/ExoPlayer";

    public IExoPlayer CreatePlayer(Context context)
    {
        // Find Java class and constructor
        _builderClass = JNIEnv.FindClass(BuilderClassName);
        var ctorId = JNIEnv.GetMethodID(_builderClass, "<init>", "()V");

        // Create SoundTouchExoPlayerBuilder instance
        var localRef = JNIEnv.NewObject(_builderClass, ctorId);
        _builderRef = JNIEnv.NewGlobalRef(localRef);
        JNIEnv.DeleteLocalRef(localRef);

        // Cache method IDs
        _setPitchMethod = JNIEnv.GetMethodID(_builderClass, "setPitch", "(F)V");
        _setTempoMethod = JNIEnv.GetMethodID(_builderClass, "setTempo", "(F)V");

        // Call build(Context) → ExoPlayer
        var buildMethod = JNIEnv.GetMethodID(_builderClass, "build",
            "(Landroid/content/Context;)Landroidx/media3/exoplayer/ExoPlayer;");
        var playerLocal = JNIEnv.CallObjectMethod(_builderRef, buildMethod,
            new JValue(context));

        var player = Java.Lang.Object.GetObject<IExoPlayer>(playerLocal,
            JniHandleOwnership.TransferLocalRef)
            ?? throw new InvalidOperationException("SoundTouch: failed to create ExoPlayer");

        return player;
    }

    public void SetPitch(float semitones)
    {
        if (_builderRef == nint.Zero) return;
        JNIEnv.CallVoidMethod(_builderRef, _setPitchMethod, new JValue(semitones));
    }

    public void SetTempo(float tempoPercent)
    {
        if (_builderRef == nint.Zero) return;
        JNIEnv.CallVoidMethod(_builderRef, _setTempoMethod, new JValue(tempoPercent));
    }

    public void Dispose()
    {
        if (_builderRef != nint.Zero)
        {
            JNIEnv.DeleteGlobalRef(_builderRef);
            _builderRef = nint.Zero;
        }
    }
}

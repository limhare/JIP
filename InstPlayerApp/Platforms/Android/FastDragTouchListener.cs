using Android.OS;
using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace InstPlayerApp.Platforms.Android;

/// <summary>
/// RecyclerView에 부착하여 기본 롱프레스(500ms)보다 짧은 시간에
/// 드래그를 시작하게 하는 터치 리스너.
/// </summary>
public class FastDragTouchListener : Java.Lang.Object, RecyclerView.IOnItemTouchListener
{
    private readonly ItemTouchHelper _touchHelper;
    private readonly int _delayMs;
    private readonly Handler _handler;
    private readonly int _touchSlop;

    private float _downX, _downY;
    private RecyclerView? _recyclerView;
    private readonly Java.Lang.Runnable _longPressRunnable;

    public FastDragTouchListener(ItemTouchHelper touchHelper, int delayMs = 150)
    {
        _touchHelper = touchHelper;
        _delayMs = delayMs;
        _handler = new Handler(Looper.MainLooper!);
        _touchSlop = ViewConfiguration.Get(
            global::Android.App.Application.Context)?.ScaledTouchSlop ?? 8;

        _longPressRunnable = new Java.Lang.Runnable(() =>
        {
            if (_recyclerView == null) return;
            var child = _recyclerView.FindChildViewUnder(_downX, _downY);
            if (child == null) return;
            var vh = _recyclerView.GetChildViewHolder(child);
            if (vh == null) return;
            _touchHelper.StartDrag(vh);
        });
    }

    public bool OnInterceptTouchEvent(RecyclerView rv, MotionEvent e)
    {
        _recyclerView = rv;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                _handler.PostDelayed(_longPressRunnable, _delayMs);
                break;

            case MotionEventActions.Move:
                if (Math.Abs(e.GetX() - _downX) > _touchSlop ||
                    Math.Abs(e.GetY() - _downY) > _touchSlop)
                    _handler.RemoveCallbacks(_longPressRunnable);
                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _handler.RemoveCallbacks(_longPressRunnable);
                break;
        }
        return false;
    }

    public void OnTouchEvent(RecyclerView rv, MotionEvent e) { }

    public void OnRequestDisallowInterceptTouchEvent(bool disallowIntercept)
    {
        if (disallowIntercept)
            _handler.RemoveCallbacks(_longPressRunnable);
    }
}

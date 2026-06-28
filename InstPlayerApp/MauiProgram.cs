using CommunityToolkit.Maui;
using InstPlayerApp.Services;
using InstPlayerApp.ViewModels;
using InstPlayerApp.Views;
using Microsoft.Extensions.Logging;
#if ANDROID
using Microsoft.Maui.Controls.Handlers.Items;
#endif

namespace InstPlayerApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if ANDROID
        builder.Services.AddSingleton<IAudioPlayerService, InstPlayerApp.Platforms.Android.AudioPlayerService>();
#endif
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainPage>();

#if ANDROID
        // 드래그 리오더 롱프레스 딜레이를 500ms → 150ms로 단축
        CollectionViewHandler.Mapper.AppendToMapping("FastDrag", (handler, view) =>
        {
            if (view is not CollectionView cv || !cv.CanReorderItems) return;
            if (handler.PlatformView is not AndroidX.RecyclerView.Widget.RecyclerView rv) return;

            // MauiRecyclerView 내부의 _itemTouchHelper 참조 획득
            var field = rv.GetType().GetField("_itemTouchHelper",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(rv) is AndroidX.RecyclerView.Widget.ItemTouchHelper ith)
            {
                rv.AddOnItemTouchListener(
                    new InstPlayerApp.Platforms.Android.FastDragTouchListener(ith, 150));
            }
        });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

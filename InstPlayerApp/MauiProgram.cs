using CommunityToolkit.Maui;
using InstPlayerApp.Services;
using InstPlayerApp.ViewModels;
using InstPlayerApp.Views;
using Microsoft.Extensions.Logging;

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

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

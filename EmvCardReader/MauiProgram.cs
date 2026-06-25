using Microsoft.Extensions.Logging;
using EmvCardReader.Emv;
using EmvCardReader.Services;
using EmvCardReader.ViewModels;

namespace EmvCardReader;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Platform-specific NFC EMV reader.
#if ANDROID
		builder.Services.AddSingleton<INfcCardReader, EmvCardReader.Platforms.Android.AndroidNfcCardReader>();
#elif IOS
		builder.Services.AddSingleton<INfcCardReader, EmvCardReader.Platforms.iOS.IosNfcCardReader>();
#endif

		// Services.
		builder.Services.AddSingleton<IClipboardService, ClipboardService>();

		// View models + pages.
		builder.Services.AddTransient<MainViewModel>();
		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

using Microsoft.Extensions.Logging;
using EmvCardReader.Emv;

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

		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

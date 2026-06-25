namespace EmvCardReader;

/// <summary>
/// Resolves DI services from places where constructor injection isn't available
/// (e.g. a page created from a Shell DataTemplate).
/// </summary>
public static class ServiceHelper
{
    public static TService? GetService<TService>() => Current.GetService<TService>();

    public static IServiceProvider Current =>
        IPlatformApplication.Current?.Services
        ?? throw new InvalidOperationException("Service provider is not available yet.");
}

using Microsoft.Extensions.DependencyInjection;

namespace Musterdepot.App.Services;

/// <summary>
/// Zugriff auf den DI-Container für Views, die der XAML-Parser erzeugt (nur für reine View-Dienste wie das Theme;
/// ViewModels bekommen ihre Abhängigkeiten per Konstruktor).
/// </summary>
public static class AppServiceLocator
{
    private static IServiceProvider? _services;

    public static void Initialize(IServiceProvider services) => _services = services;

    public static T? Get<T>() where T : class => _services?.GetService<T>();
}

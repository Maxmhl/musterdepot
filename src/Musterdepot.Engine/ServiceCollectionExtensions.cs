using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Instruments;

namespace Musterdepot.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registriert <see cref="ITradingEngine"/> (Singleton <see cref="TradingEngine"/>), <see cref="ITradingCalendar"/>
    /// (<see cref="GettexTradingCalendar"/>), <see cref="IInstrumentResolver"/> (Hub-Adapter mit Heuristik-Rückfall) sowie –
    /// falls nicht vorhanden – Uhr, ID-Erzeugung, Zufall und <see cref="ExecutionConfig"/>. <see cref="IFxRateProvider"/>
    /// und <see cref="IMarketDataHub"/> sind optional.
    /// </summary>
    public static IServiceCollection AddMusterdepotEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IRandomSource>(_ => new SeededRandom(Environment.TickCount));
        services.TryAddSingleton(new ExecutionConfig());
        services.TryAddSingleton(EngineOptions.Default);
        services.TryAddSingleton<ITradingCalendar>(_ => new GettexTradingCalendar());
        services.TryAddSingleton<IInstrumentResolver>(sp =>
            new MarketDataHubInstrumentResolver(() => sp.GetService<IMarketDataHub>(), HeuristicInstrumentResolver.Instance));
        services.TryAddSingleton(sp => new TradingEngine(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetService<IFxRateProvider>(),
            sp.GetRequiredService<ITradingCalendar>(),
            sp.GetRequiredService<IInstrumentResolver>(),
            sp.GetRequiredService<ExecutionConfig>(),
            sp.GetService<EngineOptions>()));
        services.TryAddSingleton<ITradingEngine>(sp => sp.GetRequiredService<TradingEngine>());
        return services;
    }
}

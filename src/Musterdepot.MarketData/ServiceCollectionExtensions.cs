using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Bitvavo;
using Musterdepot.MarketData.Coinbase;
using Musterdepot.MarketData.Fake;
using Musterdepot.MarketData.Fx;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Hub;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Kraken;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData;

/// <summary>Namen der HttpClients (IHttpClientFactory).</summary>
public static class MarketDataHttpClients
{
    public const string Kraken = "musterdepot.kraken";
    public const string Bitvavo = "musterdepot.bitvavo";
    public const string Coinbase = "musterdepot.coinbase";
    public const string Gettex = "musterdepot.gettex";
    public const string Ecb = "musterdepot.ecb";
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registriert IMarketDataHub, IFxRateProvider und die aktivierten Quellen (bei <see cref="DataSourceSettings.UseFakeData"/>
    /// nur die Fake-Quelle), HttpClients sowie IClock/IRandomSource/IIdGenerator (per TryAdd).
    /// Optionen (KrakenOptions, GettexOptions …) können vorher als Singleton registriert werden, sonst gelten Standardwerte.
    /// </summary>
    public static IServiceCollection AddMusterdepotMarketData(this IServiceCollection services, DataSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (services.Any(d => d.ServiceType == typeof(MarketDataHub))) return services; // bereits registriert

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IRandomSource>(_ => new SeededRandom(Environment.TickCount));
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IWebSocketConnectionFactory, ClientWebSocketConnectionFactory>();

        services.TryAddSingleton(new KrakenOptions());
        services.TryAddSingleton(new BitvavoOptions());
        services.TryAddSingleton(new CoinbaseOptions());
        services.TryAddSingleton(new GettexOptions());
        services.TryAddSingleton(new FakeMarketDataOptions());
        services.TryAddSingleton(new EcbOptions());
        services.TryAddSingleton(new HubFxOptions());
        services.TryAddSingleton(new MarketDataHubOptions());

        services.AddLogging();
        void Http(string name, TimeSpan timeout) => services.AddHttpClient(name, c =>
        {
            c.Timeout = timeout;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Musterdepot/0.1");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        Http(MarketDataHttpClients.Kraken, TimeSpan.FromSeconds(20));
        Http(MarketDataHttpClients.Bitvavo, TimeSpan.FromSeconds(30));
        Http(MarketDataHttpClients.Coinbase, TimeSpan.FromSeconds(20));
        Http(MarketDataHttpClients.Gettex, TimeSpan.FromSeconds(60));
        Http(MarketDataHttpClients.Ecb, TimeSpan.FromSeconds(20));

        static HttpClient Client(IServiceProvider sp, string name) => sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        if (settings.UseFakeData)
        {
            services.TryAddSingleton(sp => new FakeMarketDataSource(sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRandomSource>(),
                sp.GetRequiredService<FakeMarketDataOptions>(), sp.GetService<ILogger<FakeMarketDataSource>>()));
            services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<FakeMarketDataSource>());
        }
        else
        {
            if (settings.KrakenEnabled)
            {
                services.TryAddSingleton(sp => new KrakenSource(sp.GetRequiredService<KrakenOptions>(), Client(sp, MarketDataHttpClients.Kraken),
                    sp.GetRequiredService<IWebSocketConnectionFactory>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRandomSource>(),
                    ConnectionRateLimiter.ForKraken(sp.GetRequiredService<IClock>()), sp.GetService<ILogger<KrakenSource>>()));
                services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<KrakenSource>());
            }
            if (settings.BitvavoEnabled)
            {
                services.TryAddSingleton(sp => new BitvavoSource(sp.GetRequiredService<BitvavoOptions>(), Client(sp, MarketDataHttpClients.Bitvavo),
                    sp.GetRequiredService<IWebSocketConnectionFactory>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRandomSource>(),
                    null, sp.GetService<ILogger<BitvavoSource>>()));
                services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<BitvavoSource>());
            }
            if (settings.CoinbaseEnabled)
            {
                services.TryAddSingleton(sp => new CoinbaseSource(sp.GetRequiredService<CoinbaseOptions>(), Client(sp, MarketDataHttpClients.Coinbase),
                    sp.GetRequiredService<IWebSocketConnectionFactory>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRandomSource>(),
                    null, sp.GetService<ILogger<CoinbaseSource>>()));
                services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<CoinbaseSource>());
            }
            if (settings.GettexEnabled)
            {
                services.TryAddSingleton(sp => new GettexSource(sp.GetRequiredService<GettexOptions>(), Client(sp, MarketDataHttpClients.Gettex),
                    sp.GetRequiredService<IClock>(), sp.GetService<ILogger<GettexSource>>()));
                services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<GettexSource>());
            }
            // CboeEnabled: Cboe Europe ist im Bauplan "ungeprüft" – noch keine Quelle implementiert.
        }

        services.TryAddSingleton(sp => new MarketDataHub(sp.GetServices<IMarketDataSource>(), sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<MarketDataHubOptions>(), sp.GetService<ILogger<MarketDataHub>>()));
        services.TryAddSingleton<IMarketDataHub>(sp => sp.GetRequiredService<MarketDataHub>());

        // Wechselkurse: Untertags-Näherung (USDC/EUR aus dem Hub), dann EZB-Referenzkurs; im Fake-Modus feste Kurse als Rückfall.
        services.TryAddSingleton(sp => new HubFxRateProvider(sp.GetRequiredService<IMarketDataHub>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<HubFxOptions>()));
        bool ecb = settings.EcbEnabled && !settings.UseFakeData;
        if (ecb)
            services.TryAddSingleton(sp => new EcbFxRateProvider(Client(sp, MarketDataHttpClients.Ecb), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<EcbOptions>(), sp.GetService<ILogger<EcbFxRateProvider>>()));
        services.TryAddSingleton(sp =>
        {
            var list = new List<IFxQuoteProvider> { sp.GetRequiredService<HubFxRateProvider>() };
            if (ecb) list.Add(sp.GetRequiredService<EcbFxRateProvider>());
            if (settings.UseFakeData) list.Add(StaticFxRateProvider.Default);
            return new CompositeFxRateProvider(list);
        });
        services.TryAddSingleton<IFxRateProvider>(sp => sp.GetRequiredService<CompositeFxRateProvider>());
        return services;
    }
}

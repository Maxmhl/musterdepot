using System.Collections.Concurrent;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Commands;

/// <summary>
/// KI-Clients aus <see cref="AppSettings.AiClients"/>. "ui" ist immer der Mensch (Autonomous, alle Rechte).
/// Modus/Rechte ändert nur <c>SetClientModeCommand</c> (Mensch).
/// </summary>
public sealed class ClientRegistry : IClientRegistry
{
    public const string UiClientId = "ui";
    private readonly SettingsService _settings;

    public ClientRegistry(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AiClientConfig> Clients => _settings.Current.AiClients;

    public AiClientConfig? Get(string clientId) => Clients.FirstOrDefault(c => c.ClientId == clientId);

    /// <summary>Identität für einen Client (ohne Prüfung der Quelle). Unbekannt/deaktiviert → null.</summary>
    public ClientIdentity? GetIdentity(string clientId)
    {
        if (clientId == UiClientId) return ClientIdentity.Ui;
        var c = Get(clientId);
        return c is { Enabled: true } ? new ClientIdentity(c.ClientId, c.Name, c.Mode, c.Scopes) : null;
    }

    /// <summary>
    /// Identität für einen Befehl: Source=Human nur mit ClientId "ui", Source=Ai nur mit registriertem,
    /// aktivem KI-Client. Sonst null.
    /// </summary>
    public ClientIdentity? Resolve(string clientId, CommandSource source) => source switch
    {
        CommandSource.Human => clientId == UiClientId ? ClientIdentity.Ui : null,
        CommandSource.Ai => clientId == UiClientId ? null : GetIdentity(clientId),
        _ => null,
    };

    public async Task<bool> SetModeAsync(string clientId, ClientMode mode, ClientScopes? scopes = null, CancellationToken ct = default)
    {
        var found = false;
        await _settings.UpdateAsync(s =>
        {
            var list = s.AiClients.Select(c =>
            {
                if (c.ClientId != clientId) return c;
                found = true;
                return c with { Mode = mode, Scopes = scopes ?? c.Scopes };
            }).ToList();
            return found ? s with { AiClients = list } : s;
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>Legt einen Client an oder ersetzt ihn (z. B. vom Token-Dienst nach dem Erzeugen).</summary>
    public Task UpsertAsync(AiClientConfig client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.ClientId == UiClientId) throw new ArgumentException("\"ui\" ist reserviert.", nameof(client));
        return _settings.UpdateAsync(s => s with
        {
            AiClients = s.AiClients.Where(c => c.ClientId != client.ClientId).Append(client).ToList(),
        }, ct);
    }

    public async Task<bool> RemoveAsync(string clientId, CancellationToken ct = default)
    {
        var found = false;
        await _settings.UpdateAsync(s =>
        {
            found = s.AiClients.Any(c => c.ClientId == clientId);
            return found ? s with { AiClients = s.AiClients.Where(c => c.ClientId != clientId).ToList() } : s;
        }, ct).ConfigureAwait(false);
        return found;
    }
}

/// <summary>Merkt Orders pro Client (gleitende Minute) und letzte Aktivität – für Ratenlimit und get_agent_status.</summary>
public sealed class ClientActivityTracker(IClock clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _orders = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivity = new();

    /// <summary>Prüft das Ratenlimit und zählt die Order bei Erfolg (atomar).</summary>
    public bool TryRegisterOrder(string clientId, int maxPerMinute)
    {
        var q = _orders.GetOrAdd(clientId, _ => new Queue<DateTimeOffset>());
        var now = clock.UtcNow;
        lock (q)
        {
            Trim(q, now);
            if (maxPerMinute > 0 && q.Count >= maxPerMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }

    public int OrdersLastMinute(string clientId)
    {
        if (!_orders.TryGetValue(clientId, out var q)) return 0;
        lock (q)
        {
            Trim(q, clock.UtcNow);
            return q.Count;
        }
    }

    public void Touch(string clientId) => _lastActivity[clientId] = clock.UtcNow;

    public DateTimeOffset? LastActivity(string clientId) => _lastActivity.TryGetValue(clientId, out var t) ? t : null;

    private static void Trim(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
    }
}

using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SymbolUniverseService
{
    private readonly SupabaseDataService _supabaseDataService;
    private readonly ILogger<SymbolUniverseService> _logger;

    public SymbolUniverseService(
        SupabaseDataService supabaseDataService,
        ILogger<SymbolUniverseService> logger)
    {
        _supabaseDataService = supabaseDataService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TrackedSymbol>> GetActiveSymbolsAsync(CancellationToken cancellationToken = default)
    {
        var symbols = await _supabaseDataService.GetActiveSymbolsAsync(cancellationToken);

        if (symbols.Count == 0)
        {
            _logger.LogInformation("No active tracked symbols were returned from Supabase.");
        }

        return symbols;
    }

    public async Task<IReadOnlyList<TrackedSymbol>> GetSymbolsAsync(CancellationToken cancellationToken = default)
    {
        return await _supabaseDataService.GetSymbolsAsync(cancellationToken);
    }

    public async Task<TrackedSymbol?> AddSymbolAsync(CreateTrackedSymbolRequest request, CancellationToken cancellationToken = default)
    {
        return await _supabaseDataService.AddSymbolAsync(request, cancellationToken);
    }

    public async Task<bool> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        return await _supabaseDataService.SetSymbolActiveAsync(id, isActive, cancellationToken);
    }
}

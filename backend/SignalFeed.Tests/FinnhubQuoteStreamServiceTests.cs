using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SignalFeed.Api.Services;
using SignalFeed.Tests.TestDoubles;

namespace SignalFeed.Tests;

public sealed class FinnhubQuoteStreamServiceTests
{
    [Fact]
    public async Task Subscribe_Deduplicates_AndHonorsMaxLimit()
    {
        var options = new FinnhubWebSocketOptions
        {
            Token = "test-token",
            MaxSubscribedSymbols = 2
        };
        var service = new FinnhubQuoteStreamService(new TestOptionsMonitor<FinnhubWebSocketOptions>(options), NullLogger<FinnhubQuoteStreamService>.Instance);

        await service.SubscribeAsync("AAPL");
        await service.SubscribeAsync("aapl"); // duplicate
        await service.SubscribeAsync("MSFT");
        await service.SubscribeAsync("NVDA"); // over limit

        var symbols = service.GetSubscribedSymbols();
        Assert.Equal(2, symbols.Count);
        Assert.Contains("AAPL", symbols);
        Assert.Contains("MSFT", symbols);
        Assert.DoesNotContain("NVDA", symbols);
    }

    [Fact]
    public async Task TradePayload_IsProcessed_AndFreshReadWorks()
    {
        var options = new FinnhubWebSocketOptions
        {
            Token = "test-token",
            StaleAfterSeconds = 15,
            MaxSubscribedSymbols = 10
        };
        var service = new FinnhubQuoteStreamService(new TestOptionsMonitor<FinnhubWebSocketOptions>(options), NullLogger<FinnhubQuoteStreamService>.Instance);
        await service.SubscribeAsync("AAPL");

        var processMethod = typeof(FinnhubQuoteStreamService)
            .GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(processMethod);
        processMethod!.Invoke(service, ["{\"type\":\"trade\",\"data\":[{\"s\":\"AAPL\",\"p\":189.45,\"v\":100,\"t\":1710000000000}]}"]);

        var found = service.TryGetFreshPrice("AAPL", out var snapshot);
        Assert.True(found);
        Assert.Equal(189.45m, snapshot.Price);
        Assert.Equal("AAPL", snapshot.Symbol);
    }
}

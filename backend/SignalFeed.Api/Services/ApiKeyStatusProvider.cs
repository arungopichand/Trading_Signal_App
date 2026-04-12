namespace SignalFeed.Api.Services;

public sealed class ApiKeyStatusProvider
{
    private static readonly string[] SensitiveTokens =
    [
        "apikey",
        "key",
        "token",
        "secret"
    ];

    public IReadOnlyDictionary<string, string> GetKeyStatus(IConfiguration configuration)
    {
        var output = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                continue;
            }

            if (!LooksLikeCredentialKey(item.Key))
            {
                continue;
            }

            output[item.Key] = string.IsNullOrWhiteSpace(item.Value) ? "missing" : "present";
        }

        return output;
    }

    private static bool LooksLikeCredentialKey(string key)
    {
        return SensitiveTokens.Any(token => key.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

namespace SignalFeed.Api.Services;

public static class PriceRangeResolver
{
    public static string GetPriceRange(decimal price)
    {
        if (price < 2m)
        {
            return "< $2";
        }

        if (price < 4m)
        {
            return "< $4";
        }

        if (price < 10m)
        {
            return "< $10";
        }

        return "> $10";
    }
}

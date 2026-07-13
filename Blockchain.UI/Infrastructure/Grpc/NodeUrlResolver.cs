using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace Blockchain.UI.Infrastructure.Grpc;

public static class NodeUrlResolver
{
    public static string Resolve(IConfiguration configuration, NavigationManager navigation)
    {
        string fallback = Normalize(configuration["NodeUrl"] ?? "https://localhost:7066");
        if (!bool.TryParse(configuration["AllowNodeUrlQueryOverride"], out var allowOverride) || !allowOverride)
        {
            return fallback;
        }

        try
        {
            var uri = new Uri(navigation.Uri);
            foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !string.Equals(Uri.UnescapeDataString(parts[0]), "nodeUrl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = Normalize(Uri.UnescapeDataString(parts[1]));
                if (IsHttpNodeUrl(value))
                {
                    return value;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('/');

    private static bool IsHttpNodeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

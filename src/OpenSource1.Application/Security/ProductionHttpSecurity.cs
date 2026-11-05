using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSource1.Application.Security;

public static class ProductionHttpSecurity
{
    public static void ConfigureProxy(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardLimit = 1;
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var entry in configuration.GetSection("ReverseProxy:KnownProxies").GetChildren())
        {
            if (!IPAddress.TryParse(entry.Value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("ReverseProxy:KnownProxies requires explicit IP addresses.");
            options.KnownProxies.Add(address);
        }
    }

    public static void AddProductionHttpSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options => ConfigureProxy(options, configuration));
        services.AddHealthChecks();
    }
}

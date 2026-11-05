using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using OpenSource1.Application.Security;

namespace OpenSource1.SmokeTests.Application.Security;

public sealed class ProductionHttpSecurityTests
{
    [Fact]
    public void Proxy_ConfiaSoloEnDireccionesExplicitasYUnSalto()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ReverseProxy:KnownProxies:0"] = "10.77.0.10" }).Build();
        var options = new ForwardedHeadersOptions();
        ProductionHttpSecurity.ConfigureProxy(options, configuration);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(System.Net.IPAddress.Parse("10.77.0.10"), options.KnownProxies);
        Assert.DoesNotContain(System.Net.IPAddress.Any, options.KnownProxies);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Fact]
    public void Proxy_DireccionInvalida_FallaCerrado()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ReverseProxy:KnownProxies:0"] = "*" }).Build();
        Assert.Throws<InvalidOperationException>(() => ProductionHttpSecurity.ConfigureProxy(new(), configuration));
    }
}

using LinqToRest.Server.Hosting;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace LinqToRest.Server.UnitTests.Hosting;

public sealed class CorsPolicyTests
{
    [Fact]
    public void Allows_what_the_clients_send_and_caches_preflights()
    {
        var policy = new CorsPolicyBuilder().AllowLinqToRest().Build();

        Assert.Equal(["QUERY", "POST", "GET"], policy.Methods);
        Assert.Equal(["Content-Type", "Authorization", "If-None-Match"], policy.Headers);
        Assert.Equal(["ETag"], policy.ExposedHeaders);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.PreflightMaxAge);
    }

    [Fact]
    public void Leaves_origins_and_credentials_to_the_app()
    {
        var policy = new CorsPolicyBuilder().AllowLinqToRest().Build();

        Assert.Empty(policy.Origins);
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public void Preflight_max_age_can_be_changed()
    {
        var policy = new CorsPolicyBuilder().AllowLinqToRest(TimeSpan.FromHours(1)).Build();

        Assert.Equal(TimeSpan.FromHours(1), policy.PreflightMaxAge);
    }

    [Fact]
    public void Non_positive_preflight_max_age_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorsPolicyBuilder().AllowLinqToRest(TimeSpan.Zero));
    }

    [Fact]
    public void Further_headers_are_added_to_the_standard_ones()
    {
        var policy = new CorsPolicyBuilder().WithOrigins("https://shop.example").AllowLinqToRest().WithHeaders("X-Tenant").Build();

        Assert.Equal(["Content-Type", "Authorization", "If-None-Match", "X-Tenant"], policy.Headers);
        Assert.Equal(["https://shop.example"], policy.Origins);
    }
}

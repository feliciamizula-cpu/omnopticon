using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Argus.ApiGateway.Tests;

public sealed class ApiGatewayRouteTests
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiGatewayRouteTests()
    {
        _factory = new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task RootEndpoint_ReturnsRouteList()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpForwarder();
            });
        }).CreateClient();

        var response = await client.GetAsync("/");
        Assert.True(response.IsSuccessStatusCode);

        var content = await response.Content.ReadAsJsonAsync<RootResponse>();
        Assert.Equal("Argus API Gateway", content?.Name);
        Assert.NotEmpty(content?.Routes ?? []);
    }

    [Fact]
    public async Task RootEndpoint_ContainsAllPublicRoutes()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpForwarder();
            });
        }).CreateClient();

        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsJsonAsync<RootResponse>();

        var routePaths = content?.Routes?.Select(r => r.Path).ToHashSet() ?? [];
        Assert.Contains("/programs", routePaths);
        Assert.Contains("/scopes", routePaths);
        Assert.Contains("/scope-validation", routePaths);
        Assert.Contains("/targets", routePaths);
        Assert.Contains("/assets", routePaths);
        Assert.Contains("/asset-types", routePaths);
        Assert.Contains("/tasks", routePaths);
        Assert.Contains("/workers", routePaths);
        Assert.Contains("/worker-types", routePaths);
        Assert.Contains("/worker-subscriptions", routePaths);
        Assert.Contains("/events", routePaths);
        Assert.Contains("/event-router", routePaths);
        Assert.Contains("/event-routes", routePaths);
        Assert.Contains("/findings", routePaths);
        Assert.Contains("/artifacts", routePaths);
        Assert.Contains("/rate-limits", routePaths);
        Assert.Contains("/settings", routePaths);
    }

    [Fact]
    public async Task RootEndpoint_DoesNotAdvertiseScanPlans()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpForwarder();
            });
        }).CreateClient();

        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsJsonAsync<RootResponse>();

        var routePaths = content?.Routes?.Select(r => r.Path).ToHashSet() ?? [];
        Assert.DoesNotContain("/scan-plans", routePaths);
    }

    [Fact]
    public async Task RootEndpoint_DoesNotAdvertiseWorkflowTypes()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpForwarder();
            });
        }).CreateClient();

        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsJsonAsync<RootResponse>();

        var routePaths = content?.Routes?.Select(r => r.Path).ToHashSet() ?? [];
        Assert.DoesNotContain("/workflow-types", routePaths);
    }

    [Fact]
    public async Task RootEndpoint_ContainsExpectedRouteCount()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpForwarder();
            });
        }).CreateClient();

        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsJsonAsync<RootResponse>();

        Assert.True((content?.Routes?.Length ?? 0) >= 17, $"Expected at least 17 routes, got {content?.Routes?.Length}");
    }
}

internal sealed record RootResponse(
    string Name,
    string? Version,
    RouteInfo[]? Routes);

internal sealed record RouteInfo(
    string Name,
    string Path,
    string Service,
    string Description);
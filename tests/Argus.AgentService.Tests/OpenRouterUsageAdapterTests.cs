namespace Argus.AgentService.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage.Adapters;
using NSubstitute;
using Xunit;

public sealed class OpenRouterUsageAdapterTests
{
    private static ProviderAccountRecord Account(string? secretName = "OPENROUTER_API_KEY") =>
        new()
        {
            AccountId = Guid.NewGuid(),
            ProviderKey = "openrouter",
            SecretName = secretName
        };

    [Fact]
    public async Task NotConfigured_WhenApiKeyMissing()
    {
        Environment.SetEnvironmentVariable("OPENROUTER_TEST_KEY_MISSING", null);
        var factory = Substitute.For<IHttpClientFactory>();
        var adapter = new OpenRouterUsageAdapter(factory);

        var result = await adapter.ProbeAsync(Account("OPENROUTER_TEST_KEY_MISSING"), CancellationToken.None);

        Assert.Single(result.Snapshots);
        Assert.Equal("not_configured", result.Snapshots[0].Status);
    }

    [Fact]
    public async Task ParsesKeyLimitRemaining()
    {
        var envKey = $"OPENROUTER_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envKey, "test-api-key");

        try
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    label = "test-key",
                    usage = 4.50,
                    limit = 10.00,
                    is_free_tier = false,
                    rate_limit = new { requests = 200, interval = "10s" }
                }
            });

            var httpClient = new HttpClient(new FakeHttpHandler(HttpStatusCode.OK, responseJson))
            {
                BaseAddress = new Uri("https://openrouter.ai")
            };
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

            var adapter = new OpenRouterUsageAdapter(factory);
            var result = await adapter.ProbeAsync(Account(envKey), CancellationToken.None);

            Assert.NotEmpty(result.Snapshots);
            var snap = result.Snapshots.First(s => s.WindowKind == "key_limit");
            Assert.Equal(10.00m, snap.LimitAmount);
            Assert.Equal(4.50m, snap.UsedAmount);
            Assert.Equal(5.50m, snap.RemainingAmount);
            Assert.Equal(55m, snap.RemainingPercent);
            Assert.Equal("credits", snap.Unit);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public async Task ParsesCreditBalance_WhenLimitIsNull()
    {
        var envKey = $"OPENROUTER_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envKey, "test-api-key");

        try
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                data = new
                {
                    label = "test-key",
                    usage = 2.00,
                    limit = (decimal?)null,
                    is_free_tier = false
                }
            });

            var httpClient = new HttpClient(new FakeHttpHandler(HttpStatusCode.OK, responseJson))
            {
                BaseAddress = new Uri("https://openrouter.ai")
            };
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

            var adapter = new OpenRouterUsageAdapter(factory);
            var result = await adapter.ProbeAsync(Account(envKey), CancellationToken.None);

            Assert.NotEmpty(result.Snapshots);
            var snap = result.Snapshots[0];
            Assert.Equal(2.00m, snap.UsedAmount);
            Assert.Null(snap.RemainingAmount);
            Assert.Null(snap.RemainingPercent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

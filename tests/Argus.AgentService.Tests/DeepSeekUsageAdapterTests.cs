namespace Argus.AgentService.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage.Adapters;
using NSubstitute;
using Xunit;

public sealed class DeepSeekUsageAdapterTests
{
    private static ProviderAccountRecord Account(string? secretName = "DEEPSEEK_API_KEY") =>
        new()
        {
            AccountId = Guid.NewGuid(),
            ProviderKey = "deepseek",
            SecretName = secretName
        };

    [Fact]
    public async Task NotConfigured_WhenApiKeyMissing()
    {
        // Ensure env var is not set during test
        Environment.SetEnvironmentVariable("DEEPSEEK_TEST_KEY_MISSING", null);
        var factory = Substitute.For<IHttpClientFactory>();
        var adapter = new DeepSeekUsageAdapter(factory);

        var account = Account("DEEPSEEK_TEST_KEY_MISSING");
        var result = await adapter.ProbeAsync(account, CancellationToken.None);

        Assert.Single(result.Snapshots);
        Assert.Equal("not_configured", result.Snapshots[0].Status);
        Assert.Equal("not_configured", result.Snapshots[0].Source);
    }

    [Fact]
    public async Task ParsesIsAvailableAndBalanceInfos()
    {
        var envKey = $"DEEPSEEK_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envKey, "test-api-key");

        try
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                is_available = true,
                balance_infos = new[]
                {
                    new { currency = "USD", total_balance = "12.50", granted_balance = "0.00", topped_up_balance = "12.50" }
                }
            });

            var httpClient = new HttpClient(new FakeHttpHandler(HttpStatusCode.OK, responseJson))
            {
                BaseAddress = new Uri("https://api.deepseek.com")
            };

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

            var adapter = new DeepSeekUsageAdapter(factory);
            var account = Account(envKey);
            var result = await adapter.ProbeAsync(account, CancellationToken.None);

            Assert.Single(result.Snapshots);
            var snap = result.Snapshots[0];
            Assert.Equal("usd", snap.Unit);
            Assert.Equal(12.50m, snap.RemainingAmount);
            Assert.NotEqual("not_configured", snap.Status);
            Assert.NotEqual("error", snap.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public async Task ParsesIsAvailable_False_AsExhausted()
    {
        var envKey = $"DEEPSEEK_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envKey, "test-api-key");

        try
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                is_available = false,
                balance_infos = new[]
                {
                    new { currency = "USD", total_balance = "0.00", granted_balance = "0.00", topped_up_balance = "0.00" }
                }
            });

            var httpClient = new HttpClient(new FakeHttpHandler(HttpStatusCode.OK, responseJson))
            {
                BaseAddress = new Uri("https://api.deepseek.com")
            };

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

            var adapter = new DeepSeekUsageAdapter(factory);
            var account = Account(envKey);
            var result = await adapter.ProbeAsync(account, CancellationToken.None);

            Assert.NotEmpty(result.Snapshots);
            Assert.All(result.Snapshots, s => Assert.Equal("exhausted", s.Status));
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

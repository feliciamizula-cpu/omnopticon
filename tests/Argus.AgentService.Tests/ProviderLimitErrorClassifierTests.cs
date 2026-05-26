namespace Argus.AgentService.Tests;

using Argus.AgentService.ProviderUsage;
using Xunit;

public sealed class ProviderLimitErrorClassifierTests
{
    [Theory]
    [InlineData("rate_limit_error occurred")]
    [InlineData("429 Too Many Requests")]
    [InlineData("you have hit a rate limit")]
    [InlineData("too many requests sent")]
    public void RateLimitText_MapsTo_Critical(string text)
    {
        Assert.Equal("critical", ProviderLimitErrorClassifier.Classify(text));
    }

    [Fact]
    public void HttpStatus429_MapsTo_Critical()
    {
        Assert.Equal("critical", ProviderLimitErrorClassifier.Classify("some error", 429));
    }

    [Theory]
    [InlineData("insufficient balance in your account")]
    [InlineData("402 Payment Required")]
    [InlineData("quota exceeded for this period")]
    [InlineData("usage limit reached today")]
    [InlineData("weekly limit reached")]
    [InlineData("message limit hit")]
    public void QuotaExhaustedText_MapsTo_Exhausted(string text)
    {
        Assert.Equal("exhausted", ProviderLimitErrorClassifier.Classify(text));
    }

    [Fact]
    public void HttpStatus402_MapsTo_Exhausted()
    {
        Assert.Equal("exhausted", ProviderLimitErrorClassifier.Classify("payment required", 402));
    }

    [Theory]
    [InlineData("invalid api key provided")]
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    public void AuthErrorText_MapsTo_Error(string text)
    {
        Assert.Equal("error", ProviderLimitErrorClassifier.Classify(text));
    }

    [Fact]
    public void HttpStatus401_MapsTo_Error()
    {
        Assert.Equal("error", ProviderLimitErrorClassifier.Classify("unauthorized", 401));
    }

    [Fact]
    public void HttpStatus403_MapsTo_Error()
    {
        Assert.Equal("error", ProviderLimitErrorClassifier.Classify("forbidden", 403));
    }

    [Fact]
    public void UnrecognizedText_MapsTo_Unknown()
    {
        Assert.Equal("unknown", ProviderLimitErrorClassifier.Classify("some random output text"));
    }

    [Fact]
    public void CredentialError_TakesPrecedenceOver_RateLimit()
    {
        // 401 should map to error, not critical, even if rate-limit text is also present
        var result = ProviderLimitErrorClassifier.Classify("401 unauthorized rate limit", 401);
        Assert.Equal("error", result);
    }
}

using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Uygulama yalnız Gemini Flash Lite kullanır; .env'de daha pahalı bir model yazsa bile yok sayılır.</summary>
public class GeminiModelLockTests
{
    [Theory]
    [InlineData("gemini-3.5-flash-lite", "gemini-3.5-flash-lite")]
    [InlineData(" gemini-3.1-flash-lite ", "gemini-3.1-flash-lite")]
    [InlineData("gemini-3.6-flash", "gemini-3.5-flash-lite")]
    [InlineData("gemini-3.1-pro", "gemini-3.5-flash-lite")]
    [InlineData("gemini-3.8-flash", "gemini-3.5-flash-lite")]
    [InlineData("", "gemini-3.5-flash-lite")]
    [InlineData(null, "gemini-3.5-flash-lite")]
    public void YalnizFlashLite(string? configured, string expected)
        => Assert.Equal(expected, GeminiVisionClient.LiteOnly(configured));
}

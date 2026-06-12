using Otsuno.Core.Models;
using Otsuno.Core.Services;

namespace Otsuno.Core.Tests;

public class InMemoryTranslationCacheTests {
    [Fact]
    public void TryGetReturnsFalseForMissingRequest() {
        var cache = new InMemoryTranslationCache();
        var request = new TranslationRequest("Start", "en", "ja");

        var found = cache.TryGet(request, out var response);

        Assert.False(found);
        Assert.Equal("Start", response.SourceText);
        Assert.Equal(string.Empty, response.TranslatedText);
        Assert.False(response.FromCache);
    }

    [Fact]
    public void StoreThenTryGetReturnsCachedResponse() {
        var cache = new InMemoryTranslationCache();
        var request = new TranslationRequest("Start Game", "en", "ja");
        var response = new TranslationResponse("Start Game", "ゲーム開始", "en", "ja", FromCache: false);

        cache.Store(request, response);
        var found = cache.TryGet(request, out var cached);

        Assert.True(found);
        Assert.Equal("ゲーム開始", cached.TranslatedText);
        Assert.True(cached.FromCache);
    }

    [Fact]
    public void CacheKeyNormalizesRepeatedSpaces() {
        var cache = new InMemoryTranslationCache();
        var storedRequest = new TranslationRequest("Start  Game", "en", "ja");
        var lookupRequest = new TranslationRequest("Start Game", "en", "ja");
        var response = new TranslationResponse("Start  Game", "ゲーム開始", "en", "ja", FromCache: false);

        cache.Store(storedRequest, response);
        var found = cache.TryGet(lookupRequest, out var cached);

        Assert.True(found);
        Assert.Equal("ゲーム開始", cached.TranslatedText);
    }
}

using System.Net;
using System.Text;
using PokeTokenBar.Core.Companions;
using PokeTokenBar.Core.Pokedex;

namespace PokeTokenBar.Core.Tests;

public sealed class PokeApiClientTests
{
    [Theory]
    [InlineData("https://pokeapi.co/api/v2/pokemon-species/25/", 25)]
    [InlineData("https://pokeapi.co/api/v2/evolution-chain/10/", 10)]
    [InlineData("https://pokeapi.co/api/v2/pokemon-species/133", 133)]
    public void ParsesTheTrailingIdOutOfAnApiUrl(string url, int expected)
    {
        // The id is parsed rather than the URL followed: a server-supplied URL is the input an
        // SSRF guard exists for, and an integer cannot redirect anything.
        Assert.Equal(expected, PokeApiClient.TrailingId(url));
    }

    [Theory]
    [InlineData("https://evil.example.com/api/v2/pokemon-species/25/", 25)]
    [InlineData("http://127.0.0.1:8080/25/", 25)]
    public void ParsingIgnoresTheHostEntirely(string url, int expected)
    {
        // Even a hostile URL yields only a number, which is then range-checked. Nothing about
        // the host survives to influence a request.
        Assert.Equal(expected, PokeApiClient.TrailingId(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://pokeapi.co/api/v2/pokemon-species/")]
    [InlineData("not a url at all")]
    public void RejectsUrlsWithNoUsableId(string? url)
    {
        Assert.Null(PokeApiClient.TrailingId(url));
    }

    [Fact]
    public async Task ReadsTheBaseSpeciesIndex()
    {
        var body = """
        {"data":{"pokemonspecies":[
          {"id":1,"capture_rate":45,"is_legendary":false,"is_mythical":false},
          {"id":10,"capture_rate":255,"is_legendary":false,"is_mythical":false},
          {"id":150,"capture_rate":3,"is_legendary":true,"is_mythical":false}
        ]}}
        """;

        var client = new PokeApiClient(new HttpClient(new StubHandler(Json(body))));
        var index = await client.GetBaseSpeciesAsync();

        Assert.Equal(3, index.Count);
        Assert.Equal(Rarity.Rare, index[0].Rarity);
        Assert.Equal(Rarity.Common, index[1].Rarity);
        Assert.Equal(Rarity.Legendary, index[2].Rarity);
    }

    [Fact]
    public async Task DropsIndexEntriesOutsideTheSpriteRange()
    {
        var body = """
        {"data":{"pokemonspecies":[
          {"id":1,"capture_rate":45},
          {"id":0,"capture_rate":45},
          {"id":9999,"capture_rate":45}
        ]}}
        """;

        var index = await new PokeApiClient(new HttpClient(new StubHandler(Json(body)))).GetBaseSpeciesAsync();

        Assert.Single(index);
        Assert.Equal(1, index[0].Id);
    }

    [Fact]
    public async Task WalksABranchingChainIntoSeparatePaths()
    {
        // Eevee's shape: one root, several leaves.
        var body = """
        {"chain":{"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/133/"},"evolves_to":[
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/134/"},"evolves_to":[]},
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/135/"},"evolves_to":[]}
        ]}}
        """;

        var chain = await new PokeApiClient(new HttpClient(new StubHandler(Json(body))))
            .GetEvolutionChainAsync(67);
        var paths = chain.Paths;

        Assert.Equal(2, paths.Count);
        Assert.Equal([133, 134], paths[0]);
        Assert.Equal([133, 135], paths[1]);
    }

    [Fact]
    public async Task WalksALinearChain()
    {
        var body = """
        {"chain":{"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/4/"},"evolves_to":[
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/5/"},"evolves_to":[
            {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/6/"},"evolves_to":[]}
          ]}
        ]}}
        """;

        var chain = await new PokeApiClient(new HttpClient(new StubHandler(Json(body))))
            .GetEvolutionChainAsync(2);
        var paths = chain.Paths;

        Assert.Single(paths);
        Assert.Equal([4, 5, 6], paths[0]);
    }

    [Fact]
    public async Task RejectsANonJsonResponse()
    {
        var html = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>captive portal</html>", Encoding.UTF8, "text/html"),
        };

        var index = await new PokeApiClient(new HttpClient(new StubHandler(html))).GetBaseSpeciesAsync();

        Assert.Empty(index);
    }

    [Fact]
    public async Task RejectsAResponseOverTheCap()
    {
        var huge = Json("{\"data\":{\"pokemonspecies\":[]}}");
        huge.Content = new ByteArrayContent(new byte[PokeApiClient.MaxResponseBytes + 1024]);
        huge.Content.Headers.ContentType = new("application/json");

        Assert.Empty(await new PokeApiClient(new HttpClient(new StubHandler(huge))).GetBaseSpeciesAsync());
    }

    [Fact]
    public async Task DegradesWhenOffline()
    {
        var client = new PokeApiClient(new HttpClient(new ThrowingHandler()));

        Assert.Empty(await client.GetBaseSpeciesAsync());
        Assert.Empty((await client.GetEvolutionChainAsync(1)).Paths);
        Assert.Null(await client.GetChainIdAsync(1));
    }

    [Fact]
    public async Task NeverRequestsAnOutOfRangeSpecies()
    {
        var handler = new StubHandler(Json("{}"));
        var client = new PokeApiClient(new HttpClient(handler));

        Assert.Null(await client.GetChainIdAsync(99_999));
        Assert.Null(await client.GetChainIdAsync(0));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OnlyTalksToThePinnedHosts()
    {
        var handler = new StubHandler(() => Json("{\"data\":{\"pokemonspecies\":[]}}"));
        var client = new PokeApiClient(new HttpClient(handler));

        await client.GetBaseSpeciesAsync();
        await client.GetChainIdAsync(25);

        Assert.All(handler.Hosts, host =>
            Assert.True(
                host is PokeApiClient.GraphQlHost or PokeApiClient.RestHost,
                $"unexpected host {host}"));
        Assert.All(handler.Schemes, scheme => Assert.Equal("https", scheme));
    }


    [Fact]
    public async Task TruncatesAtASpeciesOutsideTheSpriteRangeRatherThanDiscardingTheLine()
    {
        // Chain 110: Teddiursa -> Ursaring -> Ursaluna. Ursaluna is 901, beyond the animated
        // sprite range, and dropping the branch made Teddiursa look single-form.
        var body = """
        {"chain":{"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/216/"},"evolves_to":[
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/217/"},"evolves_to":[
            {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/901/"},"evolves_to":[]}
          ]}
        ]}}
        """;

        var chain = await new PokeApiClient(new HttpClient(new StubHandler(Json(body))))
            .GetEvolutionChainAsync(110);
        var paths = chain.Paths;

        Assert.Single(paths);
        Assert.Equal([216, 217], paths[0]);
    }

    [Fact]
    public async Task YieldsNothingWhenTheRootItselfIsOutOfRange()
    {
        var body = """
        {"chain":{"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/906/"},"evolves_to":[]}}
        """;

        var chain = await new PokeApiClient(new HttpClient(new StubHandler(Json(body))))
            .GetEvolutionChainAsync(1);

        Assert.Empty(chain.Paths);
    }

    [Fact]
    public async Task KeepsOnlyTheInRangePartOfABranchingChain()
    {
        // Eevee: some branches are in range, others (Sylveon, 700) are not.
        var body = """
        {"chain":{"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/133/"},"evolves_to":[
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/134/"},"evolves_to":[]},
          {"species":{"url":"https://pokeapi.co/api/v2/pokemon-species/700/"},"evolves_to":[]}
        ]}}
        """;

        var chain = await new PokeApiClient(new HttpClient(new StubHandler(Json(body))))
            .GetEvolutionChainAsync(67);
        var paths = chain.Paths;

        Assert.Equal(2, paths.Count);
        Assert.Equal([133, 134], paths[0]);
        // The out-of-range branch degrades to the root alone rather than vanishing.
        Assert.Equal([133], paths[1]);
    }

    private static HttpResponseMessage Json(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    /// <summary>
    /// Builds a fresh response per call. The client disposes each response, so a handler that
    /// hands out one instance fails on the second request with ObjectDisposedException.
    /// </summary>
    private sealed class StubHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        public StubHandler(HttpResponseMessage single)
            : this(() => single)
        {
        }

        public int Calls { get; private set; }

        public List<string> Hosts { get; } = [];

        public List<string> Schemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Hosts.Add(request.RequestUri!.Host);
            Schemes.Add(request.RequestUri!.Scheme);
            return Task.FromResult(factory());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}

public sealed class SpeciesLibraryTests
{
    [Fact]
    public async Task FallsBackToBuiltInLinesWithNoNetwork()
    {
        // A companion must exist even with no network and no cache.
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        var line = await library.DrawAsync(12345);

        Assert.NotEmpty(line.SpeciesPath);
        Assert.Contains(line, EvolutionLines.All);
    }

    [Fact]
    public async Task DrawsFromACachedIndexWhoseEntriesHaveNoName()
    {
        // A cached base-index written by a build before BaseSpecies carried a name has no name
        // field, and a source-generated deserialiser does not run the `= string.Empty`
        // initialiser, so Name comes back null. Seeding names from that index must skip the
        // null rather than dereferencing it — otherwise every draw throws and no egg can be
        // taken. The offline handler proves the draw survives on the cache alone.
        using var directory = new TempPokedexDirectory();
        var nameless = """
            {"fetchedAt":"2026-09-10T10:01:00+00:00","entries":[
              {"id":1,"captureRate":45,"isLegendary":false,"isMythical":false},
              {"id":4,"captureRate":45,"isLegendary":false,"isMythical":false},
              {"id":7,"captureRate":45,"isLegendary":false,"isMythical":false},
              {"id":10,"captureRate":255,"isLegendary":false,"isMythical":false}
            ]}
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory.Path, "base-index.json"), nameless);
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        var line = await library.DrawAsync(553524739);

        Assert.NotEmpty(line.SpeciesPath);
    }

    [Fact]
    public async Task TheSameSeedDrawsTheSameLine()
    {
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        var first = await library.DrawAsync(999);
        var second = await library.DrawAsync(999);

        Assert.Equal(first.SpeciesPath, second.SpeciesPath);
    }

    [Fact]
    public async Task DoesNotCacheATruncatedIndex()
    {
        // Caching a short index would narrow the pool permanently.
        using var directory = new TempPokedexDirectory();
        var body = """{"data":{"pokemonspecies":[{"id":1,"capture_rate":45}]}}""";
        var handler = new SingleResponseHandler(body);
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(handler)), directory.Path);

        await library.DrawAsync(1);

        Assert.False(library.HasCachedIndex);
    }

    [Fact]
    public async Task ReconstructsTheFormsRaisedToReachAFinalForm()
    {
        // A completed line is recorded by its last form alone, so a Pokédex rebuilt from it
        // would show Venusaur with nothing before it. The built-in table answers this offline.
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        Assert.Equal([1, 2, 3], await library.LineageOfAsync(3));
        Assert.Equal([1, 2], await library.LineageOfAsync(2));
        Assert.Equal([1], await library.LineageOfAsync(1));
    }

    [Fact]
    public async Task ASpeciesWithNoEarlierFormIsItsOwnWholeLineage()
    {
        // A real answer, not a failure: Mewtwo has nothing before it.
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        Assert.Equal([150], await library.LineageOfAsync(150));
    }

    [Fact]
    public async Task AnUnresolvableLineageIsEmptyRatherThanTheSpeciesAlone()
    {
        // The caller retries an empty answer and accepts a single-element one, so returning
        // [id] on failure would make a failed lookup indistinguishable from a single-form line
        // and stop the retry that would have fixed it.
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new OfflineHandler())), directory.Path);

        Assert.Empty(await library.LineageOfAsync(700));
    }

    [Fact]
    public async Task RejectsAnImplausibleSpeciesIdWithoutAskingAnyone()
    {
        using var directory = new TempPokedexDirectory();
        var library = new SpeciesLibrary(new PokeApiClient(new HttpClient(new ThrowIfCalledHandler())), directory.Path);

        Assert.Empty(await library.LineageOfAsync(0));
        Assert.Empty(await library.LineageOfAsync(-5));
    }

    [Fact]
    public async Task CachesAResolvedLineageSoTheSecondLookupIsOffline()
    {
        using var directory = new TempPokedexDirectory();
        var chain = """
            {"chain":{"species":{"name":"gastly","url":"https://pokeapi.co/api/v2/pokemon-species/92/"},
            "evolves_to":[{"species":{"name":"haunter","url":"https://pokeapi.co/api/v2/pokemon-species/93/"},
            "evolves_to":[{"species":{"name":"gengar","url":"https://pokeapi.co/api/v2/pokemon-species/94/"},
            "evolves_to":[]}]}]}}
            """;
        var online = new SpeciesLibrary(
            new PokeApiClient(new HttpClient(new SpeciesThenChainHandler(chain))),
            directory.Path);

        Assert.Equal([92, 93, 94], await online.LineageOfAsync(94));

        var offline = new SpeciesLibrary(
            new PokeApiClient(new HttpClient(new OfflineHandler())),
            directory.Path);
        Assert.Equal([92, 93, 94], await offline.LineageOfAsync(94));
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class ThrowIfCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"no request should have been made, got {request.RequestUri}");
    }

    /// <summary>Answers the species lookup with a chain id, then the chain itself.</summary>
    private sealed class SpeciesThenChainHandler(string chain) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path.Contains("evolution-chain", StringComparison.Ordinal)
                ? chain
                : """{"evolution_chain":{"url":"https://pokeapi.co/api/v2/evolution-chain/33/"}}""";

            // A fresh response per call: the product disposes each one it reads.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TempPokedexDirectory : IDisposable
    {
        public TempPokedexDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ptb-pokedex-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

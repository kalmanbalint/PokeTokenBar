using System.Net;
using System.Text;
using System.Text.Json;

namespace PokeTokenBar.Core.Pokedex;

/// <summary>
/// Reads species data from PokéAPI.
/// </summary>
/// <remarks>
/// Both hosts are pinned and every response is size-capped and content-type checked. Species
/// identifiers are parsed out of the URLs the API returns rather than those URLs being
/// followed — a server-supplied URL is exactly the input an SSRF guard exists for, and an
/// integer cannot redirect anything.
/// </remarks>
public sealed class PokeApiClient(HttpClient? client = null)
{
    public const string GraphQlHost = "graphql.pokeapi.co";
    public const string RestHost = "pokeapi.co";

    /// <summary>The base index is a few hundred small records; this is generous for it.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Animated sprites exist only through generation V, and a companion with no artwork is a
    /// worse outcome than a smaller pool.
    /// </summary>
    public const int MaxSpeciesId = 649;

    private static readonly Uri GraphQlEndpoint = new("https://graphql.pokeapi.co/v1beta2");

    private readonly HttpClient _client = client ?? CreateClient();

    /// <summary>
    /// Every species with no pre-evolution, with the fields rarity is derived from. One request
    /// rather than several hundred.
    /// </summary>
    public async ValueTask<IReadOnlyList<BaseSpecies>> GetBaseSpeciesAsync(
        CancellationToken cancellationToken = default)
    {
        const string query =
            "{ pokemonspecies(where: {evolves_from_species_id: {_is_null: true}, id: {_lte: "
            + "649}}, order_by: {id: asc}) { id name capture_rate is_legendary is_mythical } }";

        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = query },
            PokedexJsonContext.Default.DictionaryStringString);

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        var json = await ReadJsonAsync(request, GraphQlHost, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        using var document = json;
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("pokemonspecies", out var species)
            || species.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<BaseSpecies>(species.GetArrayLength());
        foreach (var entry in species.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id) || !id.TryGetInt32(out var speciesId))
            {
                continue;
            }

            if (speciesId is < 1 or > MaxSpeciesId)
            {
                continue;
            }

            results.Add(new BaseSpecies
            {
                Id = speciesId,
                Name = ReadString(entry, "name"),
                CaptureRate = ReadInt(entry, "capture_rate", 255),
                IsLegendary = ReadBool(entry, "is_legendary"),
                IsMythical = ReadBool(entry, "is_mythical"),
            });
        }

        return results;
    }

    /// <summary>
    /// Every root-to-leaf path of the chain a species belongs to, as species ids. Branching
    /// chains yield more than one path.
    /// </summary>
    public async ValueTask<EvolutionChainResult> GetEvolutionChainAsync(
        int chainId,
        CancellationToken cancellationToken = default)
    {
        if (chainId < 1)
        {
            return EvolutionChainResult.Empty;
        }

        var url = new Uri($"https://{RestHost}/api/v2/evolution-chain/{chainId}/");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var json = await ReadJsonAsync(request, RestHost, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return EvolutionChainResult.Empty;
        }

        using var document = json;
        if (!document.RootElement.TryGetProperty("chain", out var chain))
        {
            return EvolutionChainResult.Empty;
        }

        var paths = new List<int[]>();
        var names = new Dictionary<int, string>();
        Walk(chain, [], paths, names);
        return new EvolutionChainResult { Paths = paths, Names = names };
    }

    /// <summary>
    /// Name of a single species. Used to fill gaps for species never walked as part of a
    /// chain — a collected mid-line form, for instance.
    /// </summary>
    public async ValueTask<string?> GetSpeciesNameAsync(int speciesId, CancellationToken cancellationToken = default)
    {
        if (speciesId is < 1 or > MaxSpeciesId)
        {
            return null;
        }

        var url = new Uri($"https://{RestHost}/api/v2/pokemon-species/{speciesId}/");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var json = await ReadJsonAsync(request, RestHost, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var document = json;
        var name = ReadString(document.RootElement, "name");
        return name.Length > 0 ? name : null;
    }

    /// <summary>Chain id for a species, needed because a species does not name its own chain.</summary>
    public async ValueTask<int?> GetChainIdAsync(int speciesId, CancellationToken cancellationToken = default)
    {
        if (speciesId is < 1 or > MaxSpeciesId)
        {
            return null;
        }

        var url = new Uri($"https://{RestHost}/api/v2/pokemon-species/{speciesId}/");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var json = await ReadJsonAsync(request, RestHost, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var document = json;
        if (!document.RootElement.TryGetProperty("evolution_chain", out var chain)
            || !chain.TryGetProperty("url", out var chainUrl)
            || chainUrl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return TrailingId(chainUrl.GetString());
    }

    /// <summary>
    /// Last path segment of a PokéAPI URL as an integer. Parsing the id rather than following
    /// the URL is what keeps a server-supplied string from choosing what gets fetched.
    /// </summary>
    internal static int? TrailingId(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(segments[i], out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static void Walk(
        JsonElement node,
        List<int> prefix,
        List<int[]> paths,
        Dictionary<int, string> names)
    {
        var id = InRangeSpeciesId(node);
        if (id is null)
        {
            return;
        }

        if (node.TryGetProperty("species", out var speciesNode))
        {
            var name = ReadString(speciesNode, "name");
            if (name.Length > 0)
            {
                names[id.Value] = name;
            }
        }

        var path = new List<int>(prefix) { id.Value };

        // An evolution outside the sprite range is dropped, and the line ends here only when no
        // evolution is left. Teddiursa -> Ursaring -> Ursaluna (901) keeps [216, 217] rather than
        // losing the line; Meowth -> Persian | Perrserker (863) keeps [52, 53] alone, because
        // also emitting [52] would offer Meowth as a single-form branch of its own.
        var children = node.TryGetProperty("evolves_to", out var next) && next.ValueKind == JsonValueKind.Array
            ? next.EnumerateArray().Where(static child => InRangeSpeciesId(child) is not null).ToArray()
            : [];

        // The depth cap guards against a cyclic or absurdly deep chain from malformed data.
        if (children.Length == 0 || path.Count >= 8)
        {
            paths.Add([.. path]);
            return;
        }

        foreach (var child in children)
        {
            Walk(child, path, paths, names);
        }
    }

    private static int? InRangeSpeciesId(JsonElement node)
    {
        var id = node.TryGetProperty("species", out var species)
                 && species.TryGetProperty("url", out var url)
                 && url.ValueKind == JsonValueKind.String
            ? TrailingId(url.GetString())
            : null;

        return id is >= 1 and <= MaxSpeciesId ? id : null;
    }

    private async ValueTask<JsonDocument?> ReadJsonAsync(
        HttpRequestMessage request,
        string expectedHost,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null
            || !request.RequestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !request.RequestUri.Host.Equals(expectedHost, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return null;
            }

            var bytes = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : JsonDocument.Parse(bytes);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<byte[]?> ReadCappedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            // Enforced while reading as well as against the declared length, because
            // Content-Length is a claim rather than a guarantee.
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement parent, string name, int fallback) =>
        parent.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt32(out var value)
            ? value
            : fallback;

    private static bool ReadBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PokeTokenBar/1.0");
        return client;
    }
}

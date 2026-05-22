// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveVoiceTest;

/// <summary>
/// Fetches SharePoint list items using app-only client-credentials authentication
/// against the Microsoft Graph REST API.
/// </summary>
internal sealed class SharePointService
{
    private readonly SharePointOptions _options;
    private readonly HttpClient _http;

    // Token cache
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // Resolved site GUID cache — path-based IDs cannot be chained with /lists/
    private string? _resolvedSiteId;

    // Resolved list GUID cache — display names in URL paths are unreliable
    private string? _resolvedListId;

    public SharePointService(SharePointOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Returns all items from the configured SharePoint list as a JSON array string.
    /// </summary>
    public async Task<string> GetListItemsAsync(CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);

        // Resolve the path-based SiteId to its canonical GUID form once.
        // Graph does not support chaining path-based site resolution with /lists/ inline.
        var siteId = await ResolveSiteIdAsync(token, ct).ConfigureAwait(false);

        // Resolve the list display name to its GUID — display names in URL paths are unreliable.
        var listId = await ResolveListIdAsync(token, siteId, ct).ConfigureAwait(false);

        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}" +
                  $"/lists/{listId}/items?expand=fields&$top=200";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Extract the "value" array and return it as a compact JSON string
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("value", out var valueElement))
        {
            // Build a simplified list of field objects to keep the response concise
            var items = new List<Dictionary<string, object?>>();
            foreach (var item in valueElement.EnumerateArray())
            {
                if (item.TryGetProperty("fields", out var fields))
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var field in fields.EnumerateObject())
                    {
                        // Skip internal SharePoint metadata fields
                        if (field.Name.StartsWith('@') || field.Name.StartsWith("_"))
                            continue;

                        dict[field.Name] = field.Value.ValueKind switch
                        {
                            JsonValueKind.String => field.Value.GetString(),
                            JsonValueKind.Number => field.Value.TryGetInt64(out var l) ? l : field.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => field.Value.ToString()
                        };
                    }
                    items.Add(dict);
                }
            }
            return JsonSerializer.Serialize(items);
        }

        return "[]";
    }

    /// <summary>
    /// Resolves a path-based SiteId (e.g. "hostname:/sites/path:") to its canonical
    /// GUID-based id (e.g. "hostname,siteGuid,webGuid") via GET /sites/{siteId}.
    /// The result is cached for the lifetime of this instance.
    /// </summary>
    private async Task<string> ResolveSiteIdAsync(string token, CancellationToken ct)
    {
        if (_resolvedSiteId is not null)
            return _resolvedSiteId;

        var url = $"https://graph.microsoft.com/v1.0/sites/{_options.SiteId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        _resolvedSiteId = doc.RootElement.GetProperty("id").GetString()!;
        Console.WriteLine($"Resolved SharePoint site ID: {_resolvedSiteId}");
        return _resolvedSiteId;
    }

    /// <summary>
    /// Resolves the configured list display name to its canonical GUID by fetching
    /// GET /sites/{siteId}/lists and matching on displayName (case-insensitive).
    /// The result is cached for the lifetime of this instance.
    /// </summary>
    private async Task<string> ResolveListIdAsync(string token, string siteId, CancellationToken ct)
    {
        if (_resolvedListId is not null)
            return _resolvedListId;

        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists?$select=id,displayName,name";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var displayName = list.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            var name = list.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (string.Equals(displayName, _options.ListId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, _options.ListId, StringComparison.OrdinalIgnoreCase))
            {
                _resolvedListId = list.GetProperty("id").GetString()!;
                Console.WriteLine($"Resolved SharePoint list '{_options.ListId}' to ID: {_resolvedListId}");
                return _resolvedListId;
            }
        }

        throw new InvalidOperationException(
            $"SharePoint list '{_options.ListId}' was not found in site '{siteId}'. " +
            $"Available lists: {string.Join(", ", doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(l => l.TryGetProperty("displayName", out var d) ? d.GetString() : "?"))}");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _cachedToken;

        var tokenUrl = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

        var body = new FormUrlEncodedContent(
        [
            new("client_id", _options.ClientId),
            new("client_secret", _options.ClientSecret),
            new("scope", "https://graph.microsoft.com/.default"),
            new("grant_type", "client_credentials")
        ]);

        using var response = await _http.PostAsync(tokenUrl, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _cachedToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

        return _cachedToken;
    }
}

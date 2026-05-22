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

        // Graph endpoint: GET /sites/{siteId}/lists/{listId}/items?expand=fields
        var url = $"https://graph.microsoft.com/v1.0/sites/{_options.SiteId}" +
                  $"/lists/{_options.ListId}/items?expand=fields&$top=200";

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

using RecordingBot.Services.ServiceSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecordingBot.Services.Media
{
    /// <summary>Describes a single column from a SharePoint list.</summary>
    public sealed record ListColumn(
        string DisplayName,
        string InternalName,
        string Type,
        bool Required,
        string Description);

    /// <summary>
    /// Fetches and mutates SharePoint list items using app-only client-credentials
    /// authentication against the Microsoft Graph REST API.
    /// </summary>
    public sealed class SharePointService
    {
        private readonly SharePointSettings _options;
        private readonly HttpClient _http;

        // Token cache
        private string _cachedToken;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

        // Resolved site GUID cache
        private string _resolvedSiteId;

        // Resolved list GUID cache
        private string _resolvedListId;

        // Schema cache — fetched once at startup
        private IReadOnlyList<ListColumn> _schema;

        public SharePointService(SharePointSettings options, HttpClient http = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _http = http ?? new HttpClient();
        }

        /// <summary>
        /// Returns the parsed column schema, fetching it once and caching the result.
        /// </summary>
        public async Task<IReadOnlyList<ListColumn>> GetSchemaAsync(CancellationToken ct = default)
        {
            if (_schema is not null)
                return _schema;

            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            var siteId = await ResolveSiteIdAsync(token, ct).ConfigureAwait(false);
            var listId = await ResolveListIdAsync(token, siteId, ct).ConfigureAwait(false);

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/columns";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var columns = new List<ListColumn>();
            foreach (var col in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var hidden = col.TryGetProperty("hidden", out var h) && h.GetBoolean();
                var readOnly = col.TryGetProperty("readOnly", out var ro) && ro.GetBoolean();
                if (hidden || readOnly)
                    continue;

                var internalName = col.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                if (internalName is "Title" or "id" or "ID")
                    continue;

                columns.Add(new ListColumn(
                    DisplayName: col.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? internalName : internalName,
                    InternalName: internalName,
                    Type: ResolveColumnType(col),
                    Required: col.TryGetProperty("required", out var req) && req.GetBoolean(),
                    Description: col.TryGetProperty("description", out var desc) ? desc.GetString() : null));
            }

            _schema = columns;
            return _schema;
        }

        /// <summary>
        /// Returns all items from the configured SharePoint list as a JSON array string.
        /// </summary>
        public async Task<string> GetListItemsAsync(CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            var siteId = await ResolveSiteIdAsync(token, ct).ConfigureAwait(false);
            var listId = await ResolveListIdAsync(token, siteId, ct).ConfigureAwait(false);

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}" +
                      $"/lists/{listId}/items?expand=fields&$top=200";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var valueElement))
            {
                var items = new List<Dictionary<string, object>>();
                foreach (var item in valueElement.EnumerateArray())
                {
                    if (item.TryGetProperty("fields", out var fields))
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var field in fields.EnumerateObject())
                        {
                            if (field.Name.StartsWith("@") || field.Name.StartsWith("_"))
                                continue;

                            dict[field.Name] = field.Value.ValueKind switch
                            {
                                JsonValueKind.String => (object)field.Value.GetString(),
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
        /// Creates a new item in the SharePoint list using the provided field values.
        /// <paramref name="fields"/> keys must be the internal column names.
        /// Returns a JSON string indicating success and the created item's ID.
        /// </summary>
        public async Task<string> CreateListItemAsync(
            Dictionary<string, JsonElement> fields, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            var siteId = await ResolveSiteIdAsync(token, ct).ConfigureAwait(false);
            var listId = await ResolveListIdAsync(token, siteId, ct).ConfigureAwait(false);

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items";

            var body = JsonSerializer.Serialize(new { fields });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var createdId = doc.RootElement.TryGetProperty("id", out var idProp)
                ? idProp.GetString()
                : "unknown";

            return JsonSerializer.Serialize(new { success = true, id = createdId });
        }

        private static string ResolveColumnType(JsonElement col)
        {
            string[] knownTypes =
            [
                "text", "number", "boolean", "choice", "dateTime", "lookup",
                "person", "hyperlink", "currency", "calculated", "geolocation",
                "term", "thumbnail", "contentApprovalStatus"
            ];

            foreach (var t in knownTypes)
            {
                if (col.TryGetProperty(t, out _))
                    return t;
            }
            return "unknown";
        }

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

            _resolvedSiteId = doc.RootElement.GetProperty("id").GetString();
            return _resolvedSiteId;
        }

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
                    _resolvedListId = list.GetProperty("id").GetString();
                    return _resolvedListId;
                }
            }

            throw new InvalidOperationException(
                $"SharePoint list '{_options.ListId}' was not found in site '{siteId}'.");
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _cachedToken;

            var tokenUrl = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _options.ClientId),
                new KeyValuePair<string, string>("client_secret", _options.ClientSecret),
                new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default"),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            using var response = await _http.PostAsync(tokenUrl, body, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _cachedToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

            return _cachedToken;
        }
    }
}

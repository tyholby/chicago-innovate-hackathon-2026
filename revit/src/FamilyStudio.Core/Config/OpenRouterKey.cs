using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FamilyStudio.Core.Json;

namespace FamilyStudio.Core.Config;

public sealed record OpenRouterKeyInfo(bool Valid, string Summary);

/// <summary>
/// Checks an OpenRouter API key without spending anything, by reading the key's own metadata.
/// Family Studio does not route any model calls through OpenRouter yet; this only proves the
/// key in the .env file is usable for features that will.
/// </summary>
public static class OpenRouterKey
{
    private static readonly Uri[] Endpoints =
    {
        new("https://openrouter.ai/api/v1/key"),
        new("https://openrouter.ai/api/v1/auth/key")
    };

    public static async Task<OpenRouterKeyInfo> CheckAsync(string apiKey, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        foreach (var endpoint in Endpoints)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, "the key was not accepted.");
            if (!response.IsSuccessStatusCode)
                return new(false, $"OpenRouter answered {(int)response.StatusCode}.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var data = document.RootElement.Opt("data") ?? document.RootElement;
            var label = data.Str("label");
            var usage = data.Opt("usage") is { ValueKind: JsonValueKind.Number } u ? u.GetDouble() : (double?)null;
            var limit = data.Opt("limit") is { ValueKind: JsonValueKind.Number } l ? l.GetDouble() : (double?)null;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(label)) parts.Add($"Label: {label}.");
            if (usage is double used) parts.Add(string.Format(CultureInfo.InvariantCulture, "Used ${0:0.00}", used) +
                (limit is double cap ? string.Format(CultureInfo.InvariantCulture, " of ${0:0.00}.", cap) : "."));
            return new(true, parts.Count > 0 ? string.Join(" ", parts) : "Key accepted.");
        }
        return new(false, "OpenRouter's key endpoint was not found.");
    }
}

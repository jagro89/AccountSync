using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AccountSync.Models;
using Microsoft.Extensions.Options;

namespace AccountSync.Services;

public sealed class KeycloakService(HttpClient httpClient, IOptions<KeycloakOptions> options) : IKeycloakService
{
    private readonly KeycloakOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1);
    private string? _accessToken;
    private DateTimeOffset _refreshTokenAt;

    private string UsersPath => $"admin/realms/{Uri.EscapeDataString(_options.Realm)}/users";

    public async IAsyncEnumerable<KeycloakUser> GetUsersAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = 0;

        while (true)
        {
            var path = string.Create(CultureInfo.InvariantCulture,
                $"{UsersPath}?first={first}&max={_options.PageSize}&briefRepresentation=false");
            KeycloakUser[] users;
            using (var response = await SendAdminAsync(HttpMethod.Get, path, null, cancellationToken))
            {
                users = await response.Content.ReadFromJsonAsync<KeycloakUser[]>(cancellationToken)
                    ?? throw new InvalidDataException("Keycloak did not return a user list.");
            }

            if (users.Length == 0)
                yield break;

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return user;
            }

            first += users.Length;
        }
    }

    public async Task<KeycloakUser> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using var response = await SendAdminAsync(
            HttpMethod.Get, $"{UsersPath}/{Uri.EscapeDataString(userId)}", null, cancellationToken);

        return await response.Content.ReadFromJsonAsync<KeycloakUser>(cancellationToken)
            ?? throw new InvalidDataException("Keycloak did not return the requested user.");
    }

    public async Task<bool> UpdateUserAttributesAsync(
        KeycloakUser user,
        IReadOnlyDictionary<string, string[]> attributes,
        CancellationToken cancellationToken = default)
    {
        if (!HasChanges(user.Attributes, attributes))
            return false;

        var currentUser = await GetUserAsync(user.Id, cancellationToken);
        if (!HasChanges(currentUser.Attributes, attributes))
            return false;

        var merged = currentUser.Attributes is null
            ? new Dictionary<string, string[]>(StringComparer.Ordinal)
            : new Dictionary<string, string[]>(currentUser.Attributes, StringComparer.Ordinal);

        foreach (var (key, values) in attributes)
            merged[key] = values;

        // Keycloak replaces the profile attributes, including the standard profile fields.
        var update = new
        {
            currentUser.Username,
            currentUser.Email,
            currentUser.FirstName,
            currentUser.LastName,
            Attributes = merged
        };

        using var response = await SendAdminAsync(HttpMethod.Put,
            $"{UsersPath}/{Uri.EscapeDataString(user.Id)}", update, cancellationToken);
        return true;
    }

    private static bool HasChanges(
        IReadOnlyDictionary<string, string[]>? current,
        IReadOnlyDictionary<string, string[]> desired)
    {
        foreach (var (key, values) in desired)
        {
            if (current is null || !current.TryGetValue(key, out var existing) || existing is null
                || !existing.SequenceEqual(values, StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private async Task<HttpResponseMessage> SendAdminAsync(
        HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;

        var statusCode = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(
            $"Keycloak Admin API rejected {method} {path} ({(int)statusCode}).", null, statusCode);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_accessToken is not null && now < _refreshTokenAt)
                return _accessToken;

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret
            });
            var path = $"realms/{Uri.EscapeDataString(_options.Realm)}/protocol/openid-connect/token";
            using var response = await httpClient.PostAsync(path, form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
                throw new InvalidDataException("Keycloak returned an invalid access token response.");

            var refreshMargin = Math.Min(30, token.ExpiresIn / 2.0);
            _refreshTokenAt = now.AddSeconds(token.ExpiresIn - refreshMargin);
            _accessToken = token.AccessToken;
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

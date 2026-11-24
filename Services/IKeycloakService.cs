using AccountSync.Models;

namespace AccountSync.Services;

public interface IKeycloakService
{
    IAsyncEnumerable<KeycloakUser> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<KeycloakUser> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    // Returns true when an update was sent to Keycloak.
    Task<bool> UpdateUserAttributesAsync(
        KeycloakUser user,
        IReadOnlyDictionary<string, string[]> attributes,
        CancellationToken cancellationToken = default);
}

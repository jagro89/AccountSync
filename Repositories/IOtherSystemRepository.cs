using AccountSync.Models;

namespace AccountSync.Repositories;

public interface IOtherSystemRepository
{
    // Return null when there is no unambiguous match in the source system.
    Task<OtherSystemAccount?> FindAccountAsync(
        string keycloakUserId,
        string? username,
        string? email,
        CancellationToken cancellationToken);
}

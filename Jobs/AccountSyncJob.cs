using AccountSync.Repositories;
using AccountSync.Services;
using Quartz;

namespace AccountSync.Jobs;

[DisallowConcurrentExecution]
public sealed class AccountSyncJob(
    IKeycloakService keycloak,
    IOtherSystemRepository otherSystem,
    ILogger<AccountSyncJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(AccountSyncJob));

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var updated = 0;
        var unchanged = 0;
        var unmatched = 0;
        var failed = 0;

        await foreach (var user in keycloak.GetUsersAsync(cancellationToken))
        {
            try
            {
                var account = await otherSystem.FindAccountAsync(
                    user.Id, user.Username, user.Email, cancellationToken);

                if (account is null)
                {
                    unmatched++;
                    continue;
                }

                var attributes = new Dictionary<string, string[]>();
                if (!string.IsNullOrWhiteSpace(account.ExternalId))
                    attributes["externalSystemId"] = [account.ExternalId];
                if (!string.IsNullOrWhiteSpace(account.JobTitle))
                    attributes["jobTitle"] = [account.JobTitle];

                if (await keycloak.UpdateUserAttributesAsync(user, attributes, cancellationToken))
                    updated++;
                else
                    unchanged++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(ex, "Synchronization failed for Keycloak user {UserId}.", user.Id);
            }
        }

        logger.LogInformation(
            "Account synchronization finished. Updated {Updated}, unchanged {Unchanged}, unmatched {Unmatched}, failed {Failed}.",
            updated, unchanged, unmatched, failed);

        if (failed > 0)
            throw new JobExecutionException($"Account synchronization failed for {failed} user(s).");
    }
}

using AccountSync.Jobs;
using AccountSync.Services;
using Microsoft.Extensions.Options;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<KeycloakOptions>()
    .BindConfiguration(KeycloakOptions.SectionName)
    .Validate(options =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo),
        "Keycloak:BaseUrl must be an absolute HTTP(S) server URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Realm)
        && !string.IsNullOrWhiteSpace(options.ClientId)
        && !string.IsNullOrWhiteSpace(options.ClientSecret),
        "Keycloak:Realm, Keycloak:ClientId and Keycloak:ClientSecret are required.")
    .Validate(options => options.PageSize > 0, "Keycloak:PageSize must be positive.");

builder.Services.AddHttpClient<IKeycloakService, KeycloakService>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

var syncCron = builder.Configuration["AccountSync:CronExpression"];

// Register the source system's IOtherSystemRepository implementation before scheduling the job.
builder.Services.AddQuartz(quartz =>
{
    quartz.AddJob<AccountSyncJob>(job => job.WithIdentity(AccountSyncJob.Key).StoreDurably());

    if (!string.IsNullOrWhiteSpace(syncCron))
    {
        quartz.AddTrigger(trigger => trigger
            .WithIdentity("AccountSyncTrigger")
            .ForJob(AccountSyncJob.Key)
            .WithCronSchedule(syncCron, schedule => schedule
                .InTimeZone(TimeZoneInfo.Utc)
                .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)));
    }
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

var app = builder.Build();

app.Run();

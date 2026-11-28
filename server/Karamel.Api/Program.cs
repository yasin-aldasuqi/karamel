using Karamel.Api.Data;
using Karamel.Api.Devices;
using Karamel.Api.Health;
using Karamel.Api.Hosting;
using Karamel.Api.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// "dotnet run -- generate-vapid-keys" prints a new key pair and exits.
if (args is ["generate-vapid-keys"])
{
    var (publicKey, privateKey) = VapidKeys.Generate();
    Console.WriteLine($"Push__VapidPublicKey={publicKey}");
    Console.WriteLine($"Push__VapidPrivateKey={privateKey}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Fail at startup, with a clear message, instead of on the first request.
var connectionString = DatabaseUrl.ConnectionString(builder.Configuration)
    ?? throw new InvalidOperationException(
        "Missing the database: set ConnectionStrings__Karamel (an Npgsql connection string) " +
        "or DATABASE_URL (postgres://user:password@host:port/database).");

// "migrate" creates or updates the tables and exits. Hosts run it before a new version goes
// live (Fly.io's release_command), so a failed migration stops the deploy instead of
// leaving a server running against the wrong schema.
if (args is ["migrate"])
{
    var options = new DbContextOptionsBuilder<KaramelDb>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
    await using var db = new KaramelDb(options.Options);
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    await db.Database.MigrateAsync();
    Console.WriteLine(pending.Count == 0
        ? "The database is up to date."
        : $"Applied {pending.Count} migration(s): {string.Join(", ", pending)}");
    return;
}

builder.Services.AddDbContext<KaramelDb>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddOptions<PushOptions>()
    .Bind(builder.Configuration.GetSection("Push"))
    .Validate(push => push.AllowedEndpointHosts.Count > 0, "Push:AllowedEndpointHosts must not be empty.")
    .Validate(push => VapidKeys.Problem(push.VapidPublicKey, push.VapidPrivateKey) is null,
        "Push:VapidPublicKey and Push:VapidPrivateKey must be a matching P-256 key pair (see server/README.md).")
    .Validate(push => Uri.TryCreate(push.VapidSubject, UriKind.Absolute, out var subject)
            && (subject.Scheme == Uri.UriSchemeHttps || subject.Scheme == Uri.UriSchemeMailto),
        "Push:VapidSubject must be an https URL or a mailto: address.")
    .ValidateOnStart();

// The client that talks to push services. It never follows redirects: a redirect could point
// the server at an internal address, which is the SSRF the address allowlist prevents.
builder.Services.AddHttpClient(PushDispatcher.HttpClientName)
    .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });
builder.Services.AddScoped<PushDispatcher>();
if (builder.Configuration.GetValue("Push:SenderEnabled", defaultValue: true))
    builder.Services.AddHostedService<PushSenderService>();

var rateLimits = builder.Configuration.GetSection("RateLimits").Get<RateLimitOptions>() ?? new RateLimitOptions();
builder.Services.AddKaramelRateLimits(rateLimits);

// Every request body here is tiny. Bigger ones are refused before they are read.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    // Missing or null required fields fail as 400 Bad Request instead of becoming nulls.
    json.SerializerOptions.RespectNullableAnnotations = true;
    json.SerializerOptions.RespectRequiredConstructorParameters = true;
    // {"endpoint": "a", "endpoint": "b"} is ambiguous, so it is refused.
    json.SerializerOptions.AllowDuplicateProperties = false;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

// Everything lives under /api: the website forwards /api/* here, so the browser
// only ever talks to the website's own origin.
// Health probes from the hosting platform must always get through, even during a flood.
app.MapHealthChecks("/api/health").DisableRateLimiting();
app.MapDeviceEndpoints();

// The website needs the server's public key to subscribe a phone. It is not a secret.
app.MapGet("/api/push/public-key", (IOptions<PushOptions> push) => new { publicKey = push.Value.VapidPublicKey });

app.Run();

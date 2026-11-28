using Npgsql;

namespace Karamel.Api.Hosting;

/// <summary>
/// Hosts such as Fly.io give the database as a URL (postgres://user:password@host:port/db),
/// but Npgsql takes "Host=...;Username=..." connection strings. This converts the URL.
/// </summary>
public static class DatabaseUrl
{
    /// <summary>
    /// Fly's smallest Postgres plan allows 20 connections; Npgsql's default pool allows 100.
    /// The app and a deploy's migration step together must stay under the server's limit.
    /// </summary>
    public const int MaxPoolSize = 10;

    /// <summary>
    /// The connection string from configuration: ConnectionStrings:Karamel if set, else the
    /// DATABASE_URL environment variable converted. Null when neither is set.
    /// </summary>
    public static string? ConnectionString(IConfiguration configuration)
    {
        var direct = configuration.GetConnectionString("Karamel");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var url = configuration["DATABASE_URL"];
        return string.IsNullOrWhiteSpace(url) ? null : FromUrl(url);
    }

    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("DATABASE_URL must look like postgres://user:password@host:port/database.");

        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            // Encrypted unless the URL says otherwise (Fly's databases use TLS by default).
            SslMode = SslMode.Require,
            MaxPoolSize = MaxPoolSize,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = pair.Split('=', 2) is [var k, var v] ? (k, Uri.UnescapeDataString(v)) : (pair, "");
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                builder.SslMode = Enum.Parse<SslMode>(value.Replace("-", ""), ignoreCase: true);
        }
        return builder.ConnectionString;
    }
}

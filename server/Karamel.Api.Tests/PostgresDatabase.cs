using Karamel.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karamel.Api.Tests;

/// <summary>
/// A fresh, real PostgreSQL database for one test class, built with the app's
/// own migrations and dropped afterwards. Tests run against PostgreSQL, not an
/// in-memory fake, so constraints, indexes, and locking behave as in production.
///
/// The server comes from KARAMEL_TEST_POSTGRES (default: localhost:55432, user
/// "karamel" with the CREATEDB right). CI starts one as a service container.
/// </summary>
public class PostgresDatabase : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("KARAMEL_TEST_POSTGRES")
        ?? "Host=localhost;Port=55432;Username=karamel";

    private readonly string _name = $"karamel_test_{Guid.NewGuid():N}";

    public string ConnectionString => new NpgsqlConnectionStringBuilder(Server) { Database = _name }.ConnectionString;

    public KaramelDb CreateContext() => new(new DbContextOptionsBuilder<KaramelDb>()
        .UseNpgsql(ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options);

    /// <summary>Whether the tables are created up front; false gives an empty database.</summary>
    protected virtual bool Migrated => true;

    public async Task InitializeAsync()
    {
        await RunOnServerAsync($"CREATE DATABASE {_name}");
        if (!Migrated) return;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await RunOnServerAsync($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
    }

    private static async Task RunOnServerAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(Server) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>A fresh database with no tables at all.</summary>
public sealed class EmptyPostgresDatabase : PostgresDatabase
{
    protected override bool Migrated => false;
}

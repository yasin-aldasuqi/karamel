using System.Diagnostics;
using Karamel.Api.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Karamel.Api.Tests;

public class DatabaseUrlTests
{
    [Fact]
    public void A_Fly_style_url_becomes_an_Npgsql_connection_string()
    {
        var parsed = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.FromUrl("postgresql://fly-user:p%40ss%3Aw0rd@direct.abc123.flympg.net/fly-db"));

        Assert.Equal("direct.abc123.flympg.net", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("fly-db", parsed.Database);
        Assert.Equal("fly-user", parsed.Username);
        Assert.Equal("p@ss:w0rd", parsed.Password); // URL-escaped characters are decoded.
        Assert.Equal(SslMode.Require, parsed.SslMode); // Encrypted by default.
        Assert.Equal(DatabaseUrl.MaxPoolSize, parsed.MaxPoolSize); // Under Fly's 20-connection limit.
    }

    [Theory]
    [InlineData("postgres://u:p@db.example:6543/app?sslmode=disable", 6543, SslMode.Disable)]
    [InlineData("postgres://u:p@db.example/app?sslmode=verify-full", 5432, SslMode.VerifyFull)]
    public void The_port_and_sslmode_in_the_url_are_used(string url, int port, SslMode ssl)
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.FromUrl(url));
        Assert.Equal(port, parsed.Port);
        Assert.Equal(ssl, parsed.SslMode);
    }

    [Theory]
    [InlineData("mysql://u:p@db.example/app")]
    [InlineData("Host=db.example;Username=u")]
    [InlineData("not a url")]
    public void Anything_else_is_refused_with_a_clear_message(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseUrl.FromUrl(url));
        Assert.Contains("DATABASE_URL must look like", error.Message);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void An_explicit_connection_string_wins_over_DATABASE_URL()
    {
        Assert.Equal("Host=explicit", DatabaseUrl.ConnectionString(Config(
            ("ConnectionStrings:Karamel", "Host=explicit"), ("DATABASE_URL", "postgres://u:p@from-url/app"))));
        Assert.Contains("Host=from-url", DatabaseUrl.ConnectionString(Config(("DATABASE_URL", "postgres://u:p@from-url/app"))));
        Assert.Null(DatabaseUrl.ConnectionString(Config()));
    }
}

/// <summary>Runs the real compiled server as a separate process, like a host's release command.</summary>
public class MigrateCommandTests(EmptyPostgresDatabase database) : IClassFixture<EmptyPostgresDatabase>
{
    private static async Task<(int ExitCode, string Output)> RunServerAsync(string databaseUrl, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DATABASE_URL"] = databaseUrl;
        start.Environment.Remove("ConnectionStrings__Karamel");
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task Migrate_creates_the_tables_from_a_DATABASE_URL_and_then_reports_up_to_date()
    {
        var parsed = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        // With the password when the server needs one (CI's does; a local trust setup does not).
        var login = parsed.Password is { Length: > 0 } password
            ? $"{Uri.EscapeDataString(parsed.Username!)}:{Uri.EscapeDataString(password)}"
            : Uri.EscapeDataString(parsed.Username!);
        var url = $"postgres://{login}@{parsed.Host}:{parsed.Port}/{parsed.Database}?sslmode=disable";

        var first = await RunServerAsync(url, "migrate");
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains("Applied 1 migration(s)", first.Output);

        await using (var db = database.CreateContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await db.Devices.CountAsync());
        }

        var second = await RunServerAsync(url, "migrate");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("The database is up to date.", second.Output);
    }

    [Fact]
    public async Task Migrate_fails_loudly_when_the_database_cannot_be_reached()
    {
        var result = await RunServerAsync("postgres://karamel@localhost:1/nothing?sslmode=disable", "migrate");
        Assert.NotEqual(0, result.ExitCode); // A host stops the deploy on a non-zero exit.
    }
}

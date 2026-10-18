using DotNet.Testcontainers.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace RestToLinq.Server.IntegrationTests.Shop;

public enum TestProvider
{
    Sqlite,
    Postgres,
}

/// <summary>A fresh, isolated database for one server, and whatever must be disposed with it.</summary>
public sealed record DatabaseLease(Action<DbContextOptionsBuilder> Configure, IAsyncDisposable? Owned);

/// <summary>The provider a test class runs on.</summary>
public abstract class ShopDatabase
{
    public abstract TestProvider Provider { get; }

    /// <summary>Gets why this provider can't run here, or <see langword="null"/>.</summary>
    public virtual string? SkipReason => null;

    public abstract Task<DatabaseLease> CreateAsync();

    /// <summary>Skips the current test when the provider isn't available.</summary>
    public void SkipIfUnavailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
}

public sealed class SqliteDatabase : ShopDatabase
{
    public override TestProvider Provider => TestProvider.Sqlite;

    public override async Task<DatabaseLease> CreateAsync()
    {
        // An in-memory SQLite database lives as long as its connection.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return new DatabaseLease(options => options.UseSqlite(connection), connection);
    }
}

public sealed class PostgresDatabase(PostgresContainer container) : ShopDatabase
{
    public override TestProvider Provider => TestProvider.Postgres;

    public override string? SkipReason => container.Unavailable;

    public override async Task<DatabaseLease> CreateAsync()
    {
        SkipIfUnavailable();
        var connectionString = await container.CreateDatabaseAsync();
        return new DatabaseLease(options => options.UseNpgsql(connectionString), null);
    }
}

/// <summary>
/// One PostgreSQL container per test assembly; each server gets its own database in it. Without Docker the
/// PostgreSQL tests skip, unless RESTTOLINQ_REQUIRE_POSTGRES=1 (as in CI) makes that a failure.
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var required = Environment.GetEnvironmentVariable("RESTTOLINQ_REQUIRE_POSTGRES") == "1";
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException) when (!required)
        {
            Unavailable = "Docker isn't available, so the PostgreSQL tests can't run. Set RESTTOLINQ_REQUIRE_POSTGRES=1 to fail instead.";
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"shop_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

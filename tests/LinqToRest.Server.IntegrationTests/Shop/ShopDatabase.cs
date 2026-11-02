using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace LinqToRest.Server.IntegrationTests.Shop;

public enum TestProvider
{
    Sqlite,
    Postgres,
    SqlServer,
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
        return new DatabaseLease(options => options.UseNpgsql(connectionString), new PoolRelease(connectionString));
    }

    /// <summary>
    /// Closes the database's pooled connections once its server is done. Every test database has its own pool, so
    /// keeping them open would exhaust PostgreSQL's connection limit over a test run.
    /// </summary>
    private sealed class PoolRelease(string connectionString) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class SqlServerDatabase(SqlServerContainer container) : ShopDatabase
{
    public override TestProvider Provider => TestProvider.SqlServer;

    public override string? SkipReason => container.Unavailable;

    public override async Task<DatabaseLease> CreateAsync()
    {
        SkipIfUnavailable();
        var connectionString = await container.CreateDatabaseAsync();
        return new DatabaseLease(options => options.UseSqlServer(connectionString), new PoolRelease(connectionString));
    }

    /// <summary>Closes the database's pooled connections once its server is done, like PostgreSQL's.</summary>
    private sealed class PoolRelease(string connectionString) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            using var connection = new SqlConnection(connectionString);
            SqlConnection.ClearPool(connection);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Starts a database container for a test assembly. Without Docker its tests skip, unless
/// LINQTOREST_REQUIRE_DATABASES=1 (as in CI) makes that a failure.
/// </summary>
public static class DatabaseContainers
{
    public static async Task<string?> StartAsync(string name, Func<CancellationToken, Task> start)
    {
        try
        {
            await start(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DockerUnavailableException) when (Environment.GetEnvironmentVariable("LINQTOREST_REQUIRE_DATABASES") != "1")
        {
            return $"Docker isn't available, so the {name} tests can't run. Set LINQTOREST_REQUIRE_DATABASES=1 to fail instead.";
        }
    }
}

/// <summary>One PostgreSQL container per test assembly; each server gets its own database in it.</summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync() =>
        Unavailable = await DatabaseContainers.StartAsync("PostgreSQL", cancellationToken =>
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            return _container.StartAsync(cancellationToken);
        });

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

/// <summary>
/// One SQL Server container per test assembly; each server gets its own database in it, on the default
/// (case-insensitive) collation.
/// </summary>
public sealed class SqlServerContainer : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync() =>
        Unavailable = await DatabaseContainers.StartAsync("SQL Server", cancellationToken =>
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            return _container.StartAsync(cancellationToken);
        });

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"shop_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{name}]", connection);
        await command.ExecuteNonQueryAsync();
        return new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

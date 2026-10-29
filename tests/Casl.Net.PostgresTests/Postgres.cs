using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casl.Tests;

/// <summary>
/// A test that needs a PostgreSQL server: it runs when <c>CASL_POSTGRES</c> holds a connection string, such as
/// <c>Host=localhost;Username=postgres;Password=postgres</c>, and is skipped otherwise.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (Postgres.ConnectionString is null)
        {
            Skip = "Set CASL_POSTGRES to a PostgreSQL connection string to run this test.";
        }
    }
}

internal static class Postgres
{
    public static string? ConnectionString { get; } = Environment.GetEnvironmentVariable("CASL_POSTGRES") is { Length: > 0 } s ? s : null;

    /// <summary>Options for a database of its own, created empty: each test class gets a fresh one.</summary>
    public static DbContextOptions<TestDb> Fresh(string database)
    {
        NpgsqlConnectionStringBuilder builder = new(ConnectionString) { Database = database };
        DbContextOptions<TestDb> options = new DbContextOptionsBuilder<TestDb>().UseNpgsql(builder.ConnectionString).Options;
        using TestDb db = new(options);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
        return options;
    }
}

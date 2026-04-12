using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Robust.Cdn.Config;

namespace Robust.Cdn;

public abstract class BaseScopedDatabase : IDisposable
{
    private readonly DbContext _context;

    protected BaseScopedDatabase(DbContext context)
    {
        _context = context;
    }

    public DbContext DbContext => _context;

    public NpgsqlConnection Connection
    {
        get
        {
            var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                _context.Database.OpenConnection();
            }

            return connection;
        }
    }

#pragma warning disable CA1816
    public void Dispose()
    {
        _context.Dispose();
    }
#pragma warning restore CA1816
}

/// <summary>
/// Database service for CDN functionality.
/// </summary>
public sealed class Database(CdnDbContext context) : BaseScopedDatabase(context)
{
    public CdnDbContext Context => (CdnDbContext)DbContext;
}

/// <summary>
/// Database service for server manifest functionality.
/// </summary>
public sealed class ManifestDatabase(ManifestDbContext context, IOptions<ManifestOptions> options) : BaseScopedDatabase(context)
{
    public ManifestDbContext Context => (ManifestDbContext)DbContext;

    public void EnsureForksCreated()
    {
        var db = (ManifestDbContext)DbContext;
        foreach (var forkName in options.Value.Forks.Keys)
        {
            if (!db.Forks.Any(f => f.Name == forkName))
            {
                db.Forks.Add(new ManifestFork { Name = forkName });
            }
        }

        db.SaveChanges();
    }
}

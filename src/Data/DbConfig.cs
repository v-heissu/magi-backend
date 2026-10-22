using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Magi.Api.Data;

public static class DbConfig
{
    // Pinned instead of ServerVersion.AutoDetect, which would open a connection at startup
    // and crash the app if MySQL is not reachable yet.
    public static readonly MySqlServerVersion ServerVersion = new(new Version(8, 0, 36));
}

/// <summary>
/// Used only by `dotnet ef migrations add`: builds the context without the runtime environment
/// variables. It never connects, so the placeholder connection string is never used.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MagiDbContext>
{
    public MagiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MagiDbContext>()
            .UseMySql("Server=localhost;Database=magi_design;User=design;Password=design;", DbConfig.ServerVersion)
            .Options;
        return new MagiDbContext(options);
    }
}

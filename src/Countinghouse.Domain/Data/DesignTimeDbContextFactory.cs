using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Countinghouse.Domain.Data;

/// <summary>
/// How <c>dotnet ef</c> builds a context at design time.
/// </summary>
/// <remarks>
/// <para>
/// A WPF app has no host for the <c>dotnet ef</c> tooling to discover a configured
/// <c>DbContext</c> from, so without this every <c>migrations add</c> fails with
/// "Unable to create a DbContext of type CountinghouseContext".
/// </para>
/// <para>
/// It points at the real database under <see cref="AppPaths.DbPath"/> deliberately. Migrations are
/// scaffolded by diffing the model against the snapshot, not by reading the file, so the path only
/// matters for <c>database update</c> and <c>dotnet ef</c> commands that genuinely should act on the
/// live database — the same one <c>Migrate()</c> opens on startup.
/// </para>
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CountinghouseContext>
{
    public CountinghouseContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CountinghouseContext>()
            .UseSqlite($"Data Source={AppPaths.DbPath}")
            .Options;

        return new CountinghouseContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StackPivot.Control.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StackPivotDbContext>
{
    public StackPivotDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StackPivotDbContext>()
            .UseSqlite("Data Source=stackpivot.design.db")
            .Options;
        return new StackPivotDbContext(options);
    }
}

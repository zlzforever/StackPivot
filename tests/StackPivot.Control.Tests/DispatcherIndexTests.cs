using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Infrastructure.Persistence;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class DispatcherIndexTests
{
    [Fact]
    public async Task ServiceOperationHistoryHasTheDispatcherStatusAndEventTimeIndex()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "stackpivot-index-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var db = new StackPivotDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.OpenConnectionAsync();

            var indexNames = new List<string>();
            await using (var indexList = db.Database.GetDbConnection().CreateCommand())
            {
                indexList.CommandText = "PRAGMA index_list('service_operation_history')";
                await using var reader = await indexList.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    indexNames.Add(reader.GetString(1));
                }
            }

            var dispatcherIndex = false;
            foreach (var indexName in indexNames)
            {
                await using var indexInfo = db.Database.GetDbConnection().CreateCommand();
                indexInfo.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''", StringComparison.Ordinal)}')";
                await using var reader = await indexInfo.ExecuteReaderAsync();
                var columns = new List<string>();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(2));
                }

                if (columns.SequenceEqual(["task_status", "last_event_at"], StringComparer.Ordinal))
                {
                    dispatcherIndex = true;
                    break;
                }
            }

            Assert.True(dispatcherIndex);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}

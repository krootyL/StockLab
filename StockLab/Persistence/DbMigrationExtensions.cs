using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StockLab.Persistence;

/// <summary>
/// EnsureCreated 기반 기존 데이터베이스의 데이터를 보존하면서 EF Core Migrations 체계로 안전하게 전환/적용한다.
/// </summary>
public static class DbMigrationExtensions
{
    public static async Task MigratePreservingBaselineAsync(
        this AppDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        var historyRepo = db.GetService<IHistoryRepository>();
        var historyExists = await TableExistsAsync(db, "__EFMigrationsHistory", ct);
        var hasAppliedMigrations = historyExists && (await db.Database.GetAppliedMigrationsAsync(ct)).Any();

        if (!hasAppliedMigrations && await TableExistsAsync(db, "users", ct))
        {
            var initialMigrationId = db.Database.GetMigrations().FirstOrDefault();
            if (!string.IsNullOrEmpty(initialMigrationId))
            {
                var productVersion = typeof(DbContext).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion?.Split('+')[0] ?? "10.0.4";

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync(historyRepo.GetCreateIfNotExistsScript(), ct);

                var insertSql = historyRepo.GetInsertScript(new HistoryRow(initialMigrationId, productVersion));
                await db.Database.ExecuteSqlRawAsync(insertSql, ct);
                await tx.CommitAsync(ct);

                historyExists = true;
                logger.LogInformation(
                    "[PostgreSQL 마이그레이션 기준점 등록] 기존 스키마 보존을 위해 {MigrationId}를 적용 완료로 기록했습니다.",
                    initialMigrationId);
            }
        }

        var pending = historyExists
            ? (await db.Database.GetPendingMigrationsAsync(ct)).ToList()
            : db.Database.GetMigrations().ToList();
        await db.Database.MigrateAsync(ct);

        if (pending.Count > 0)
        {
            logger.LogInformation(
                "[PostgreSQL 마이그레이션 완료] 신규 마이그레이션 {Count}건 적용됨: {Migrations}",
                pending.Count,
                string.Join(", ", pending));
        }
        else
        {
            logger.LogInformation("[PostgreSQL 연결 성공] 데이터베이스 스키마가 최신 상태입니다.");
        }
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = current_schema()
                      AND table_name = @tableName
                );
                """;
            var param = cmd.CreateParameter();
            param.ParameterName = "@tableName";
            param.Value = tableName;
            cmd.Parameters.Add(param);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        finally
        {
            if (shouldClose)
            {
                await conn.CloseAsync();
            }
        }
    }
}


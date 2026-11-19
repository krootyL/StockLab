using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StockLab.Persistence;

/// <summary>
/// dotnet ef CLI 명령 실행 시 런타임 호스트(Program.cs) 및 외부 시크릿 없이도
/// 마이그레이션 스캐폴딩을 생성할 수 있도록 제공하는 디자인 타임 팩토리.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=stocklab_design;Username=postgres;Password=postgres");
        return new AppDbContext(optionsBuilder.Options);
    }
}


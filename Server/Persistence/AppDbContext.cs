using Microsoft.EntityFrameworkCore;
using StockLab.Persistence.Entities;

namespace StockLab.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<UserWallet> UserWallets => Set<UserWallet>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Stocks> Stocks => Set<Stocks>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Uid);
            entity.Property(e => e.Uid).ValueGeneratedNever();
            entity.Property(e => e.LoginId).HasMaxLength(50);
            entity.Property(e => e.PasswordHash).HasMaxLength(200);
            entity.Property(e => e.Nickname).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.LoginId)
                .IsUnique()
                .HasFilter("\"LoginId\" IS NOT NULL");
        });

        modelBuilder.Entity<Strategy>(entity =>
        {
            entity.ToTable("strategies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.Uid);

            entity.HasOne(e => e.User)
                .WithMany(u => u.Strategies)
                .HasForeignKey(e => e.Uid)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.ToTable("wallets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Currency).HasMaxLength(16).IsRequired();
            entity.Property(e => e.AvailableCash).HasPrecision(18, 4);
            entity.Property(e => e.PendingCash).HasPrecision(18, 4);
            entity.Property(e => e.RealizedPnl).HasPrecision(18, 4).HasDefaultValue(0m);
            entity.Property(e => e.Version).IsRowVersion();

            entity.HasIndex(e => new { e.StrategyId, e.Currency }).IsUnique();

            entity.HasOne(e => e.Strategy)
                .WithMany(s => s.Wallets)
                .HasForeignKey(e => e.StrategyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Stocks>(entity =>
        {
            entity.ToTable("stocks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StockCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Quantity).HasColumnName("Volume");
            entity.Property(e => e.ReservedQuantity).HasColumnName("ReservedVolume");
            entity.Property(e => e.AverageBuyPrice).HasPrecision(18, 4);
            entity.Property(e => e.Version).IsRowVersion();

            entity.HasIndex(e => new { e.StrategyId, e.StockCode }).IsUnique();

            entity.HasOne(e => e.Strategy)
                .WithMany(s => s.Holdings)
                .HasForeignKey(e => e.StrategyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.StockCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.OrderPrice).HasPrecision(18, 4);
            entity.Property(e => e.Quantity).HasColumnName("Volume");
            entity.Property(e => e.FilledQuantity).HasColumnName("FilledVolume");
            entity.Property(e => e.TotalAmount).HasPrecision(18, 4);

            entity.HasIndex(e => new { e.Status, e.StockCode });
            entity.HasIndex(e => new { e.StrategyId, e.OrderedAtUtc });

            entity.HasOne(e => e.Strategy)
                .WithMany(s => s.Orders)
                .HasForeignKey(e => e.StrategyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany(u => u.Orders)
                .HasForeignKey(e => e.Uid)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Trade>(entity =>
        {
            entity.ToTable("trades");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StockCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ExecutedPrice).HasPrecision(18, 4);
            entity.Property(e => e.ExecutedQuantity).HasColumnName("ExecutedVolume");
            entity.Property(e => e.TotalAmount).HasPrecision(18, 4);

            entity.HasIndex(e => e.OrderId);
            entity.HasIndex(e => new { e.StrategyId, e.ExecutedAtUtc });

            entity.HasOne(e => e.Order)
                .WithMany(o => o.Trades)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Strategy)
                .WithMany(s => s.Trades)
                .HasForeignKey(e => e.StrategyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WatchlistItem>(entity =>
        {
            entity.ToTable("watchlist_items");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StockCode).HasMaxLength(20).IsRequired();

            entity.HasIndex(e => new { e.Uid, e.StockCode }).IsUnique();
            entity.HasIndex(e => new { e.Uid, e.CreatedAtUtc });

            entity.HasOne(e => e.User)
                .WithMany(u => u.WatchlistItems)
                .HasForeignKey(e => e.Uid)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserWallet>(entity =>
        {
            entity.ToTable("user_wallets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Currency).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Balance).HasPrecision(18, 4).HasDefaultValue(0m);
            entity.Property(e => e.Version).IsRowVersion();

            entity.HasIndex(e => new { e.Uid, e.Currency }).IsUnique();

            entity.HasOne(e => e.User)
                .WithMany(u => u.AccountWallets)
                .HasForeignKey(e => e.Uid)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

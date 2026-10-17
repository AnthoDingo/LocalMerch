using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LocalMerch.Core;

public class LocalMerchDb(DbContextOptions<LocalMerchDb> options) : DbContext(options)
{
    /// <summary>Levé après l'enregistrement d'un produit ou d'une touche : la caisse se recharge.</summary>
    public static event Action? CatalogChanged;

    public DbSet<Product> Products => Set<Product>();
    public DbSet<PosKey> PosKeys => Set<PosKey>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<PaymentTerminal> PaymentTerminals => Set<PaymentTerminal>();

    public static LocalMerchDb Open(string connectionString) =>
        new(new DbContextOptionsBuilder<LocalMerchDb>().UseSqlite(connectionString).Options);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>().HasIndex(p => p.Barcode);
        b.Entity<Product>().HasOne<Supplier>().WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<PosKey>().HasIndex(k => new { k.Page, k.Row, k.Column }).IsUnique();
        b.Entity<PosKey>().HasOne<Product>().WithMany().HasForeignKey(k => k.ProductId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Quote>().HasOne<Supplier>().WithMany().HasForeignKey(q => q.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Quote>().OwnsMany(q => q.Lines, l =>
        {
            l.ToTable("QuoteLines");
            l.HasKey(x => x.Id);
            l.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PurchaseOrder>().HasOne<Supplier>().WithMany().HasForeignKey(o => o.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PurchaseOrder>().HasOne<Quote>().WithMany().HasForeignKey(o => o.QuoteId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PurchaseOrder>().OwnsMany(o => o.Lines, l =>
        {
            l.ToTable("PurchaseOrderLines");
            l.HasKey(x => x.Id);
            l.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockMovement>().HasIndex(m => m.ProductId);
        b.Entity<StockMovement>().HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StockMovement>().HasOne<Sale>().WithMany().HasForeignKey(m => m.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<StockMovement>().HasOne<PurchaseOrder>().WithMany().HasForeignKey(m => m.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Sale>().HasOne<Sale>().WithMany().HasForeignKey(s => s.RefundOfSaleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Sale>().OwnsMany(s => s.Lines, l =>
        {
            l.ToTable("SaleLines");
            l.HasKey(x => x.Id);
            l.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // SQLite perd le DateTimeKind : tout est stocké en UTC, on le relit en UTC.
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var catalog = CatalogTouched();
        var n = base.SaveChanges(acceptAllChangesOnSuccess);
        if (catalog) CatalogChanged?.Invoke();
        return n;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var catalog = CatalogTouched();
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (catalog) CatalogChanged?.Invoke();
        return n;
    }

    bool CatalogTouched() => ChangeTracker.Entries().Any(e =>
        e.Entity is Product or PosKey && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

/// <summary>Utilisé par « dotnet ef migrations ».</summary>
public class DesignTimeDbFactory : IDesignTimeDbContextFactory<LocalMerchDb>
{
    public LocalMerchDb CreateDbContext(string[] args) => LocalMerchDb.Open("Data Source=design.db");
}

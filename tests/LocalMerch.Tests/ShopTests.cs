using System.Net;
using LocalMerch.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.Tests;

public sealed class ShopTests : IDisposable
{
    readonly SqliteConnection connection = new("Data Source=:memory:");
    readonly LocalMerchDb db;
    readonly Shop shop;
    readonly Product coffee, tea;
    readonly Supplier supplier;

    public ShopTests()
    {
        connection.Open();
        db = new LocalMerchDb(new DbContextOptionsBuilder<LocalMerchDb>().UseSqlite(connection).Options);
        db.Database.Migrate();
        shop = new Shop(db);

        supplier = new Supplier { Name = "Torréfaction Martin" };
        coffee = new Product { Name = "Café", Price = 1.50m, VatRate = 10m };
        tea = new Product { Name = "Thé", Price = 2.00m, VatRate = 5.5m };
        db.AddRange(supplier, coffee, tea);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public void Ticket_groups_consecutive_items_and_voids_last()
    {
        var t = new Ticket();
        t.Add(coffee);
        t.Add(coffee);
        t.Add(tea);
        Assert.Equal(2, t.Lines.Count);
        Assert.Equal(5.00m, t.Total);

        t.VoidLast();
        t.VoidLast();
        Assert.Equal(1.50m, t.Total);

        t.Add(tea);
        t.RemoveLine(0); // la ligne de café, entière
        t.RemoveLine(5); // hors limites : ignoré
        Assert.Equal("Thé", Assert.Single(t.Lines).Label);
    }

    [Fact]
    public void Vat_is_extracted_from_price_including_vat()
    {
        Assert.Equal(0.20m, Money.VatOf(1.20m, 20m));
        Assert.Equal(0.33m, Money.VatOf(2.00m, 20m));
        Assert.Equal(0.10m, Money.VatOf(2.00m, 5.5m));
    }

    [Fact]
    public void Sale_records_lines_and_decreases_stock()
    {
        shop.RecordMovement(coffee.Id, MovementType.In, 10, "Inventaire");
        var t = new Ticket();
        t.Add(coffee);
        t.Add(coffee);
        t.Add(tea);

        var sale = shop.CompleteSale(t, PaymentMethod.Card);

        Assert.Equal(5.00m, sale.Total);
        Assert.Equal(8, shop.StockOf(coffee.Id));
        Assert.Equal(-1, shop.StockOf(tea.Id));
        Assert.Equal(0.27m, sale.Vat[10m]); // 3.00 TTC à 10 %
    }

    [Fact]
    public void Free_amount_is_sold_without_stock_movement()
    {
        var t = new Ticket();
        t.AddFree("Bon de commande 42", 123.456m, 20m);
        t.Add(coffee);

        var sale = shop.CompleteSale(t, PaymentMethod.Cheque);

        Assert.Equal(124.96m, sale.Total); // 123,46 + 1,50
        Assert.Null(sale.Lines[0].ProductId);
        Assert.Equal(-1, shop.StockOf(coffee.Id));
        Assert.Single(db.StockMovements);

        shop.RefundSale(sale.Id);
        Assert.Equal(0, shop.StockOf(coffee.Id));
        Assert.Throws<ShopException>(() => new Ticket().AddFree("x", 0m, 20m));
    }

    [Fact]
    public void Backup_restores_data_and_rejects_foreign_files()
    {
        var dir = Directory.CreateTempSubdirectory("localmerch-test").FullName;
        try
        {
            var live = Path.Combine(dir, "live.db");
            var backup = Path.Combine(dir, "backup.db");
            using (var source = LocalMerchDb.Open($"Data Source={live};Pooling=False"))
            {
                source.Database.Migrate();
                source.Products.Add(new Product { Name = "Avant", Price = 1m });
                source.SaveChanges();
            }
            Backup.Save(live, backup);
            using (var db2 = LocalMerchDb.Open($"Data Source={live};Pooling=False"))
            {
                db2.Products.Single().Name = "Après";
                db2.SaveChanges();
                Backup.Validate(backup, db2.Database.GetMigrations());
            }

            Backup.Restore(backup, live);
            using (var db3 = LocalMerchDb.Open($"Data Source={live};Pooling=False"))
                Assert.Equal("Avant", db3.Products.Single().Name);

            var garbage = Path.Combine(dir, "garbage.db");
            File.WriteAllText(garbage, "ceci n'est pas une base");
            Assert.Throws<ShopException>(() => Backup.Validate(garbage, []));
            Assert.Throws<ShopException>(() => Backup.Validate(backup, ["UneAutreMigration"]));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Secrets_are_salted_and_verified()
    {
        var a = Secret.Hash("motdepasse");
        Assert.NotEqual(a, Secret.Hash("motdepasse"));
        Assert.True(Secret.Verify("motdepasse", a));
        Assert.False(Secret.Verify("motdepassE", a));
        Assert.False(Secret.Verify("motdepasse", null));
        Assert.False(Secret.Verify("motdepasse", "n'importe quoi"));
    }

    [Fact]
    public void Empty_sale_is_refused()
    {
        Assert.Throws<ShopException>(() => shop.CompleteSale(new Ticket(), PaymentMethod.Cash));
    }

    [Fact]
    public void Refund_restores_stock_once()
    {
        var t = new Ticket();
        t.Add(coffee);
        var sale = shop.CompleteSale(t, PaymentMethod.Cash);

        var refund = shop.RefundSale(sale.Id);

        Assert.Equal(-1.50m, refund.Total);
        Assert.Equal(0, shop.StockOf(coffee.Id));
        Assert.Throws<ShopException>(() => shop.RefundSale(sale.Id));
        Assert.Throws<ShopException>(() => shop.RefundSale(refund.Id));
    }

    [Fact]
    public void Manual_movements_apply_sign_by_type()
    {
        shop.RecordMovement(tea.Id, MovementType.In, 5, null);
        shop.RecordMovement(tea.Id, MovementType.Out, 2, null);
        shop.RecordMovement(tea.Id, MovementType.Adjustment, -1, "Casse");

        Assert.Equal(2, shop.StockOf(tea.Id));
        Assert.Throws<ShopException>(() => shop.RecordMovement(tea.Id, MovementType.Out, -3, null));
        Assert.Throws<ShopException>(() => shop.RecordMovement(tea.Id, MovementType.Sale, 1, null));
        Assert.Equal(2, shop.StockLevels()[tea.Id]);
    }

    [Fact]
    public void Quote_converts_to_order_which_is_received_into_stock_once()
    {
        var quote = new Quote
        {
            SupplierId = supplier.Id,
            Reference = "D-42",
            Lines = [new() { ProductId = coffee.Id, Quantity = 12, UnitPrice = 0.40m }],
        };
        db.Add(quote);
        db.SaveChanges();

        var order = shop.ConvertQuote(quote.Id);
        Assert.Equal(QuoteStatus.Accepted, quote.Status);
        Assert.Equal(quote.Id, order.QuoteId);
        Assert.Equal(4.80m, order.Total);
        Assert.Throws<ShopException>(() => shop.ConvertQuote(quote.Id));

        shop.ReceiveOrder(order.Id);
        Assert.Equal(12, shop.StockOf(coffee.Id));
        Assert.Equal(OrderStatus.Received, order.Status);
        Assert.Throws<ShopException>(() => shop.ReceiveOrder(order.Id));
        Assert.Equal(12, shop.StockOf(coffee.Id));
    }

    [Fact]
    public void Dates_come_back_as_utc()
    {
        db.ChangeTracker.Clear();
        shop.RecordMovement(coffee.Id, MovementType.In, 1, null);
        db.ChangeTracker.Clear();
        Assert.Equal(DateTimeKind.Utc, db.StockMovements.Single().At.Kind);
    }

    [Fact]
    public void Enabled_terminal_requires_host_and_port()
    {
        static bool Valid(PaymentTerminal t) =>
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(t, new(t), null, validateAllProperties: true);

        Assert.True(Valid(new PaymentTerminal()));
        Assert.False(Valid(new PaymentTerminal { Enabled = true }));
        Assert.False(Valid(new PaymentTerminal { Host = "192.168.1.50", Port = 70000 }));
        Assert.False(Valid(new PaymentTerminal { PosNumber = "1" }));
        Assert.True(Valid(new PaymentTerminal { Enabled = true, Host = "192.168.1.50", Port = 8888 }));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("::ffff:192.168.0.3", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Local_network_detection(string ip, bool expected)
    {
        Assert.Equal(expected, LocalNetwork.Contains(IPAddress.Parse(ip)));
    }
}

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

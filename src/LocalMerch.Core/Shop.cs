using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.Core;

/// <summary>Opérations métier qui touchent plusieurs tables (stock, ventes, commandes).</summary>
public class Shop(LocalMerchDb db)
{
    public int StockOf(int productId) =>
        db.StockMovements.Where(m => m.ProductId == productId).Sum(m => m.Quantity);

    public Dictionary<int, int> StockLevels() =>
        db.StockMovements.GroupBy(m => m.ProductId)
            .Select(g => new { g.Key, Quantity = g.Sum(m => m.Quantity) })
            .ToDictionary(x => x.Key, x => x.Quantity);

    /// <summary>Mouvement manuel. Entrée/sortie : quantité positive. Ajustement : quantité signée.</summary>
    public StockMovement RecordMovement(int productId, MovementType type, int quantity, string? reason)
    {
        if (type is not (MovementType.In or MovementType.Out or MovementType.Adjustment))
            throw new ShopException("Seuls les entrées, sorties et ajustements sont saisissables.");
        if (quantity == 0 || (type != MovementType.Adjustment && quantity < 0))
            throw new ShopException("Quantité invalide.");
        if (!db.Products.Any(p => p.Id == productId))
            throw new ShopException("Produit inconnu.");

        var m = new StockMovement
        {
            ProductId = productId,
            Type = type,
            Quantity = type == MovementType.Out ? -quantity : quantity,
            Reason = reason,
        };
        db.StockMovements.Add(m);
        db.SaveChanges();
        return m;
    }

    public Sale CompleteSale(Ticket ticket, PaymentMethod payment)
    {
        if (ticket.Lines.Count == 0) throw new ShopException("Ticket vide.");
        var sale = new Sale
        {
            PaymentMethod = payment,
            Lines = ticket.Lines.Select(l => new SaleLine
            {
                ProductId = l.ProductId, Label = l.Label, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate,
            }).ToList(),
        };
        return Save(sale, MovementType.Sale);
    }

    public Sale RefundSale(int saleId)
    {
        var original = db.Sales.Find(saleId) ?? throw new ShopException("Vente inconnue.");
        if (original.RefundOfSaleId is not null) throw new ShopException("Un remboursement ne peut pas être remboursé.");
        if (db.Sales.Any(s => s.RefundOfSaleId == saleId)) throw new ShopException("Vente déjà remboursée.");

        var refund = new Sale
        {
            PaymentMethod = original.PaymentMethod,
            RefundOfSaleId = saleId,
            Lines = original.Lines.Select(l => new SaleLine
            {
                ProductId = l.ProductId, Label = l.Label, Quantity = -l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate,
            }).ToList(),
        };
        return Save(refund, MovementType.Refund);
    }

    Sale Save(Sale sale, MovementType type)
    {
        using var tx = db.Database.BeginTransaction();
        db.Sales.Add(sale);
        db.SaveChanges();
        db.StockMovements.AddRange(sale.Lines.Select(l => new StockMovement
        {
            ProductId = l.ProductId, Quantity = -l.Quantity, Type = type, SaleId = sale.Id, At = sale.At,
        }));
        db.SaveChanges();
        tx.Commit();
        return sale;
    }

    public PurchaseOrder ConvertQuote(int quoteId)
    {
        var quote = db.Quotes.Find(quoteId) ?? throw new ShopException("Devis inconnu.");
        if (quote.Status != QuoteStatus.Pending) throw new ShopException("Seul un devis en attente peut être converti.");

        quote.Status = QuoteStatus.Accepted;
        var order = new PurchaseOrder
        {
            SupplierId = quote.SupplierId,
            QuoteId = quote.Id,
            Notes = quote.Reference is null ? null : $"Devis {quote.Reference}",
            Lines = quote.Lines.Select(l => new DocumentLine
            {
                ProductId = l.ProductId, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
            }).ToList(),
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>Réception d'une commande : entre toutes ses lignes en stock.</summary>
    public PurchaseOrder ReceiveOrder(int orderId)
    {
        var order = db.PurchaseOrders.Find(orderId) ?? throw new ShopException("Commande inconnue.");
        if (order.Status is not (OrderStatus.Draft or OrderStatus.Sent))
            throw new ShopException("Commande déjà réceptionnée ou annulée.");

        order.Status = OrderStatus.Received;
        order.ReceivedAt = DateTime.UtcNow;
        db.StockMovements.AddRange(order.Lines.Select(l => new StockMovement
        {
            ProductId = l.ProductId, Quantity = l.Quantity, Type = MovementType.Receipt,
            PurchaseOrderId = order.Id, At = order.ReceivedAt.Value,
        }));
        db.SaveChanges();
        return order;
    }
}

/// <summary>Ticket en cours à la caisse (pas encore enregistré).</summary>
public class Ticket
{
    public List<TicketLine> Lines { get; } = [];
    public decimal Total => Lines.Sum(l => l.Amount);

    public void Add(Product p)
    {
        var last = Lines.LastOrDefault();
        if (last?.ProductId == p.Id) last.Quantity++;
        else Lines.Add(new TicketLine(p.Id, p.Name, p.Price, p.VatRate));
    }

    public void VoidLast()
    {
        if (Lines.Count == 0) return;
        if (--Lines[^1].Quantity == 0) Lines.RemoveAt(Lines.Count - 1);
    }

    public void Clear() => Lines.Clear();
}

public class TicketLine(int productId, string label, decimal unitPrice, decimal vatRate)
{
    public int ProductId { get; } = productId;
    public string Label { get; } = label;
    public decimal UnitPrice { get; } = unitPrice;
    public decimal VatRate { get; } = vatRate;
    public int Quantity { get; set; } = 1;
    public decimal Amount => Money.Round(UnitPrice * Quantity);
}

public static class LocalNetwork
{
    /// <summary>Loopback, plages privées IPv4 (RFC 1918), lien local, ULA IPv6.</summary>
    public static bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
}

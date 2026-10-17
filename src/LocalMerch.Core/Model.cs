using System.ComponentModel.DataAnnotations;

namespace LocalMerch.Core;

public interface IEntity
{
    int Id { get; set; }
}

public interface IHasLines
{
    List<DocumentLine> Lines { get; set; }
}

public class Product : IEntity
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(50)] public string? Barcode { get; set; }
    /// <summary>Prix de vente TTC.</summary>
    [Range(0, 1_000_000)] public decimal Price { get; set; }
    [Range(0, 100)] public decimal VatRate { get; set; } = 20m;
    public int? SupplierId { get; set; }
    public bool Active { get; set; } = true;
}

public enum KeyAction { Product, VoidLast, ClearTicket, PayCash, PayCard, GoToPage }

public class PosKey : IEntity, IValidatableObject
{
    public int Id { get; set; }
    [Range(0, 99)] public int Page { get; set; }
    [Range(0, 49)] public int Row { get; set; }
    [Range(0, 49)] public int Column { get; set; }
    [Required, MaxLength(40)] public string Label { get; set; } = "";
    [RegularExpression("^#[0-9a-fA-F]{6}$")] public string Color { get; set; } = "#3b82f6";
    public KeyAction Action { get; set; }
    public int? ProductId { get; set; }
    [Range(0, 99)] public int? TargetPage { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Action == KeyAction.Product && ProductId is null)
            yield return new("Une touche produit doit avoir un produit.", [nameof(ProductId)]);
        if (Action == KeyAction.GoToPage && TargetPage is null)
            yield return new("Une touche de page doit avoir une page cible.", [nameof(TargetPage)]);
    }
}

public class Supplier : IEntity
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? ContactName { get; set; }
    [EmailAddress, MaxLength(100)] public string? Email { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(20)] public string? PostalCode { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(100)] public string? Country { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
}

/// <summary>Ligne de devis ou de commande fournisseur.</summary>
public class DocumentLine
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    [Range(1, 1_000_000)] public int Quantity { get; set; } = 1;
    [Range(0, 1_000_000)] public decimal UnitPrice { get; set; }
    public decimal Amount => Money.Round(UnitPrice * Quantity);
}

public enum QuoteStatus { Pending, Accepted, Rejected }

/// <summary>Devis fournisseur.</summary>
public class Quote : IEntity, IHasLines
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    [MaxLength(50)] public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateOnly? ValidUntil { get; set; }
    public QuoteStatus Status { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    public List<DocumentLine> Lines { get; set; } = [];
    public decimal Total => Lines.Sum(l => l.Amount);
}

public enum OrderStatus { Draft, Sent, Received, Cancelled }

/// <summary>Commande fournisseur.</summary>
public class PurchaseOrder : IEntity, IHasLines
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int? QuoteId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReceivedAt { get; set; }
    public OrderStatus Status { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    public List<DocumentLine> Lines { get; set; } = [];
    public decimal Total => Lines.Sum(l => l.Amount);
}

public enum MovementType { In, Out, Adjustment, Sale, Refund, Receipt }

/// <summary>Mouvement de stock. Quantité signée : positive = entrée, négative = sortie.</summary>
public class StockMovement
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public MovementType Type { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string? Reason { get; set; }
    public int? SaleId { get; set; }
    public int? PurchaseOrderId { get; set; }
}

public enum PaymentMethod { Cash, Card }

public class SaleLine
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Label { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public decimal Amount => Money.Round(UnitPrice * Quantity);
}

/// <summary>Vente validée, immuable. Un remboursement est une nouvelle vente aux quantités négatives.</summary>
public class Sale
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public PaymentMethod PaymentMethod { get; set; }
    public int? RefundOfSaleId { get; set; }
    public List<SaleLine> Lines { get; set; } = [];
    public decimal Total => Lines.Sum(l => l.Amount);

    /// <summary>TVA incluse, par taux.</summary>
    public Dictionary<decimal, decimal> Vat =>
        Lines.GroupBy(l => l.VatRate).ToDictionary(g => g.Key, g => Money.VatOf(g.Sum(l => l.Amount), g.Key));
}

public enum TerminalProtocol { CaisseAp, Concert }

/// <summary>Configuration du TPE réseau. Une seule ligne (Id = 1).</summary>
public class PaymentTerminal : IValidatableObject
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public TerminalProtocol Protocol { get; set; }
    [MaxLength(255)] public string? Host { get; set; }
    [Range(1, 65535)] public int? Port { get; set; }
    /// <summary>Numéro de caisse transmis au TPE.</summary>
    [Required, RegularExpression("^[0-9]{2}$")] public string PosNumber { get; set; } = "01";
    /// <summary>Temps laissé au client pour payer.</summary>
    [Range(10, 300)] public int TimeoutSeconds { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Enabled && (string.IsNullOrWhiteSpace(Host) || Port is null))
            yield return new("Un TPE activé doit avoir une adresse et un port.", [nameof(Host)]);
    }
}

public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Part de TVA contenue dans un montant TTC.</summary>
    public static decimal VatOf(decimal amountInclVat, decimal ratePercent) =>
        Round(amountInclVat - amountInclVat / (1 + ratePercent / 100m));
}

public class ShopException(string message) : Exception(message);

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

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

// Nouvelles valeurs uniquement en fin de liste : stockées en base sous forme d'entier.
public enum KeyAction { Product, VoidLast, ClearTicket, PayCash, PayCard, GoToPage, PayCheque, FreeAmount, VoidSelected }

public class PosKey : IEntity, IValidatableObject
{
    public int Id { get; set; }
    [Range(0, 99)] public int Page { get; set; }
    /// <summary>Affichée sur toutes les pages ; une touche propre à la page, à la même place, la masque.</summary>
    public bool AllPages { get; set; }
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

public enum PaymentMethod { Cash, Card, Cheque }

public class SaleLine
{
    public int Id { get; set; }
    /// <summary>Null pour un montant libre (hors catalogue, sans mouvement de stock).</summary>
    public int? ProductId { get; set; }
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

/// <summary>Rôles de l'administration web. Admin a tous les droits ; Standard n'a que ses droits par page.</summary>
public enum Role { Standard, Admin }

/// <summary>Pages métier soumises à des droits. En minuscules : identifiant de l'écran dans l'admin web.</summary>
public enum Page { Keys, Sales, Products, Stock, Suppliers, Quotes, Orders }

/// <summary>Niveaux croissants : Write inclut Read.</summary>
public enum Access { None, Read, Write }

public class PagePermission
{
    public Page Page { get; set; }
    public Access Access { get; set; }
}

/// <summary>Compte de l'administration web.</summary>
public class User
{
    public int Id { get; set; }
    [Required, MaxLength(50), RegularExpression("^[a-z0-9._-]+$", ErrorMessage = "Identifiant : minuscules, chiffres, . _ - uniquement.")]
    public string Login { get; set; } = "";
    [Required, MaxLength(100)] public string DisplayName { get; set; } = "";
    public Role Role { get; set; }
    public bool Active { get; set; } = true;
    [JsonIgnore] public string? PasswordHash { get; set; }
    public bool HasPassword => PasswordHash is not null;
    /// <summary>Droits par page d'un utilisateur Standard (ignorés pour un administrateur).</summary>
    public List<PagePermission> Permissions { get; set; } = [];

    public Access AccessTo(Page page) => Role == Role.Admin
        ? Access.Write
        : Permissions.Where(p => p.Page == page).Select(p => p.Access).DefaultIfEmpty(Access.None).Max();
}

/// <summary>Hachage PBKDF2 des mots de passe. Format : itérations.sel.hash (base64).</summary>
public static class Secret
{
    const int Iterations = 100_000;

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string secret, string? stored)
    {
        if (stored?.Split('.') is not [var iterations, var salt, var hash]) return false;
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, Convert.FromBase64String(salt), int.Parse(iterations),
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Personnalisation (titre, logo). Une seule ligne (Id = 1).</summary>
public class ShopSettings
{
    public int Id { get; set; } = 1;
    [Required, MaxLength(100)] public string Title { get; set; } = "LocalMerch";
    [JsonIgnore] public byte[]? Logo { get; set; }
    [JsonIgnore] public string? LogoContentType { get; set; }
    public bool HasLogo => Logo is not null;
    /// <summary>Code demandé pour quitter la caisse. Jamais renvoyé par /api/branding (public).</summary>
    [JsonIgnore, MaxLength(20), RegularExpression("^[0-9]*$", ErrorMessage = "Mot de passe de sortie : chiffres uniquement.")]
    public string? ExitPassword { get; set; }
    /// <summary>Installe seul les mises à jour (au démarrage ou la nuit).</summary>
    [JsonIgnore] public bool AutoUpdate { get; set; }
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LocalMerch.Core;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.App;

/// <summary>Écran de caisse (Avalonia, construit en code, sans XAML).</summary>
public class PosApp : Application
{
    static IServiceProvider services = null!;
    static PosOptions options = null!;

    public static void Run(string[] args, IServiceProvider sp, PosOptions opts)
    {
        services = sp;
        options = opts;
        AppBuilder.Configure<PosApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PosWindow(services, options);
        base.OnFrameworkInitializationCompleted();
    }
}

public class PosWindow : Window
{
    readonly IServiceProvider services;
    readonly PosOptions options;
    readonly Ticket ticket = new();
    readonly Grid keysGrid = new();
    readonly ListBox ticketList = new() { FontSize = 20 };
    readonly TextBlock total = new() { FontSize = 44, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock status = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap };
    readonly TextBox barcode = new() { PlaceholderText = "Code-barres", FontSize = 18 };
    List<PosKey> keys = [];
    Dictionary<int, Product> products = [];
    int page;

    public PosWindow(IServiceProvider services, PosOptions options)
    {
        this.services = services;
        this.options = options;
        Title = "LocalMerch — Caisse";
        Width = 1280;
        Height = 800;
        if (options.FullScreen) WindowState = WindowState.FullScreen;

        var right = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(barcode, Dock.Top);
        var footer = new StackPanel { Spacing = 4, Children = { total, status } };
        DockPanel.SetDock(footer, Dock.Bottom);
        right.Children.Add(barcode);
        right.Children.Add(footer);
        right.Children.Add(ticketList);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
        keysGrid.Margin = new Thickness(8);
        root.Children.Add(keysGrid);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        barcode.AddHandler(KeyDownEvent, OnBarcodeKey, RoutingStrategies.Tunnel);
        LocalMerchDb.CatalogChanged += OnCatalogChanged;
        Closed += (_, _) => LocalMerchDb.CatalogChanged -= OnCatalogChanged;

        Reload();
        RefreshTicket();
    }

    // Levé depuis le thread du serveur web quand l'admin modifie produits ou touches.
    void OnCatalogChanged() => Dispatcher.UIThread.Post(Reload);

    void Reload()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalMerchDb>();
        keys = db.PosKeys.AsNoTracking().ToList();
        products = db.Products.AsNoTracking().Where(p => p.Active).ToDictionary(p => p.Id);
        RenderKeys();
    }

    void RenderKeys()
    {
        keysGrid.Children.Clear();
        keysGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", options.Rows)));
        keysGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", options.Columns)));

        foreach (var key in keys.Where(k => k.Page == page && k.Row < options.Rows && k.Column < options.Columns))
        {
            var button = new Button
            {
                Content = new TextBlock { Text = key.Label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Background = Brush.Parse(key.Color),
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => Press(key);
            Grid.SetRow(button, key.Row);
            Grid.SetColumn(button, key.Column);
            keysGrid.Children.Add(button);
        }
    }

    void Press(PosKey key)
    {
        switch (key.Action)
        {
            case KeyAction.Product:
                if (key.ProductId is { } id && products.TryGetValue(id, out var p)) ticket.Add(p);
                else status.Text = "Produit indisponible.";
                break;
            case KeyAction.VoidLast: ticket.VoidLast(); break;
            case KeyAction.ClearTicket: ticket.Clear(); break;
            case KeyAction.PayCash: Pay(PaymentMethod.Cash); break;
            case KeyAction.PayCard: Pay(PaymentMethod.Card); break;
            case KeyAction.GoToPage:
                page = key.TargetPage ?? 0;
                RenderKeys();
                break;
        }
        RefreshTicket();
    }

    void OnBarcodeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var code = barcode.Text?.Trim();
        barcode.Text = "";
        var product = products.Values.FirstOrDefault(p => p.Barcode == code);
        if (product is null)
        {
            status.Text = $"Code-barres inconnu : {code}";
            return;
        }
        ticket.Add(product);
        RefreshTicket();
    }

    void Pay(PaymentMethod method)
    {
        if (ticket.Lines.Count == 0) return;
        try
        {
            using var scope = services.CreateScope();
            var sale = scope.ServiceProvider.GetRequiredService<Shop>().CompleteSale(ticket, method);
            status.Text = $"Vente n°{sale.Id} — {sale.Total:C} ({(method == PaymentMethod.Cash ? "espèces" : "carte")})";
            ticket.Clear();
        }
        catch (Exception ex)
        {
            // Le ticket est conservé : rien n'est perdu, on peut réessayer.
            status.Text = $"Erreur, vente non enregistrée : {ex.Message}";
        }
    }

    void RefreshTicket()
    {
        ticketList.ItemsSource = ticket.Lines.Select(l => $"{l.Quantity} × {l.Label}    {l.Amount:C}").ToList();
        total.Text = ticket.Total.ToString("C");
    }
}

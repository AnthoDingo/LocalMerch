using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

/// <summary>
/// Conçue pour un écran 4:3 de 1024×768. Un Viewbox la met à l'échelle de façon uniforme sur
/// toute autre résolution 4:3 (800×600, 1280×1024…).
/// </summary>
public class PosWindow : Window
{
    const int DesignWidth = 1024, DesignHeight = 768;

    readonly IServiceProvider services;
    readonly PosOptions options;
    readonly Ticket ticket = new();

    // Écran de vente
    readonly Grid keysGrid = new() { Margin = new Thickness(4) };
    readonly ListBox ticketList = new() { FontSize = 18 };
    readonly TextBlock total = new() { FontSize = 40, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock status = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    readonly TextBox barcode = new() { PlaceholderText = "Code-barres", FontSize = 16 };
    readonly TextBlock headerTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Image headerLogo = new() { Height = 36, Margin = new Thickness(0, 0, 8, 0) };

    // Montant libre
    // Page « Montant libre », ouverte uniquement par une touche FreeAmount.
    readonly Control saleScreen;
    readonly DockPanel freePage = new() { IsVisible = false };
    readonly TextBox freeReference = new() { PlaceholderText = "N° du bon de commande (facultatif)", FontSize = 18, Width = 380 };
    readonly TextBox freeAmount = new() { FontSize = 36, Width = 380, IsReadOnly = true, PlaceholderText = "0,00 €", HorizontalContentAlignment = HorizontalAlignment.Right };
    readonly TextBlock freeError = new() { FontSize = 16, Foreground = Brushes.OrangeRed, HorizontalAlignment = HorizontalAlignment.Center };

    List<PosKey> keys = [];
    Dictionary<int, Product> products = [];
    ShopSettings settings = new();
    int page;

    public PosWindow(IServiceProvider services, PosOptions options)
    {
        this.services = services;
        this.options = options;
        Width = DesignWidth;
        Height = DesignHeight;
        // Appliqué une fois la fenêtre affichée : sous X11, certains gestionnaires de fenêtres
        // ignorent le plein écran demandé avant l'affichage.
        if (options.FullScreen) Opened += (_, _) => WindowState = WindowState.FullScreen;

        saleScreen = SaleScreen();
        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = new Panel
            {
                Width = DesignWidth,
                Height = DesignHeight,
                Children = { saleScreen, freePage },
            },
        };
        BuildFreePage();

        barcode.AddHandler(KeyDownEvent, OnBarcodeKey, RoutingStrategies.Tunnel);
        LocalMerchDb.CatalogChanged += OnCatalogChanged;
        Closed += (_, _) => LocalMerchDb.CatalogChanged -= OnCatalogChanged;

        Reload();
        RefreshTicket();
    }

    Control SaleScreen()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(headerLogo, Dock.Left);
        header.Children.Add(headerLogo);
        header.Children.Add(headerTitle);

        var top = new StackPanel { Spacing = 6, Children = { header, barcode } };
        var footer = new StackPanel { Spacing = 2, Children = { total, status } };
        var right = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        right.Children.Add(top);
        right.Children.Add(footer);
        right.Children.Add(ticketList);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
        root.Children.Add(keysGrid);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        return root;
    }

    /// <summary>Pavé numérique tactile : 3 colonnes.</summary>
    static Grid Keypad(Action<string> press, params string[] labels)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), HorizontalAlignment = HorizontalAlignment.Center, Width = 300 };
        for (var i = 0; i < labels.Length; i++)
        {
            if (i % 3 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = labels[i];
            var b = new Button
            {
                Content = label, FontSize = 26, Height = 64, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) => press(label);
            Grid.SetRow(b, i / 3);
            Grid.SetColumn(b, i % 3);
            grid.Children.Add(b);
        }
        return grid;
    }

    // ---------- Montant libre ----------

    void BuildFreePage()
    {
        var keypad = Keypad(k =>
        {
            var text = freeAmount.Text ?? "";
            var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (k == "←") text = text.Length > 0 ? text[..^1] : "";
            else if (k == ",") { if (!text.Contains(separator)) text = (text.Length == 0 ? "0" : text) + separator; }
            else if (!(text.Contains(separator) && text.Length - text.IndexOf(separator) > 2) && text.Length < 9) text += k;
            freeAmount.Text = text;
            freeError.Text = "";
        }, "7", "8", "9", "4", "5", "6", "1", "2", "3", ",", "0", "←");

        var pay = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
        foreach (var (label, method, color) in new[]
                 {
                     ("Carte bancaire", PaymentMethod.Card, "#7c3aed"),
                     ("Espèces", PaymentMethod.Cash, "#16a34a"),
                     ("Chèque", PaymentMethod.Cheque, "#0369a1"),
                 })
        {
            var b = new Button
            {
                Content = label, FontSize = 20, Height = 64, Width = 170, Foreground = Brushes.White,
                Background = Brush.Parse(color), HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            b.Click += (_, _) => PayFreeAmount(method);
            pay.Children.Add(b);
        }
        var back = new Button { Content = "← Retour à la caisse", FontSize = 18, Padding = new Thickness(16, 10) };
        back.Click += (_, _) => ShowSaleScreen();
        var header = new Grid { Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var title = new TextBlock
        {
            Text = "Montant libre", FontSize = 28, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(back);
        // Titre centré sur toute la largeur, pas seulement à droite du bouton.
        Grid.SetColumnSpan(title, 2);
        header.Children.Insert(0, title);
        DockPanel.SetDock(header, Dock.Top);

        freePage.Children.Add(header);
        freePage.Children.Add(new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { freeReference, freeAmount, keypad, freeError, pay },
        });
    }

    void ShowSaleScreen()
    {
        freePage.IsVisible = false;
        saleScreen.IsVisible = true;
        barcode.Focus();
    }

    void OpenFreeAmount()
    {
        freeReference.Text = "";
        freeAmount.Text = "";
        freeError.Text = "";
        saleScreen.IsVisible = false;
        freePage.IsVisible = true;
    }

    void PayFreeAmount(PaymentMethod method)
    {
        if (!decimal.TryParse(freeAmount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0)
        {
            freeError.Text = "Saisissez un montant.";
            return;
        }
        // Ticket à part : le ticket en cours à la caisse n'est ni payé ni modifié.
        var reference = freeReference.Text?.Trim();
        var freeTicket = new Ticket();
        freeTicket.AddFree(string.IsNullOrEmpty(reference) ? "Montant libre" : $"Bon de commande {reference}",
            amount, options.FreeAmountVatRate);
        if (Pay(freeTicket, method)) ShowSaleScreen();
        else freeError.Text = status.Text;
    }

    // ---------- Vente ----------

    // Levé depuis le thread du serveur web quand l'admin modifie produits, touches ou réglages.
    void OnCatalogChanged() => Dispatcher.UIThread.Post(Reload);

    void Reload()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalMerchDb>();
        keys = db.PosKeys.AsNoTracking().ToList();
        products = db.Products.AsNoTracking().Where(p => p.Active).ToDictionary(p => p.Id);
        settings = db.Settings.AsNoTracking().FirstOrDefault() ?? new ShopSettings();

        Title = headerTitle.Text = settings.Title;
        headerLogo.Source = settings.Logo is { } bytes ? new Bitmap(new MemoryStream(bytes)) : null;
        headerLogo.IsVisible = headerLogo.Source is not null;
        RenderKeys();
    }

    void RenderKeys()
    {
        keysGrid.Children.Clear();
        keysGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", options.Rows)));
        keysGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", options.Columns)));

        var visible = keys.Where(k => (k.Page == page || k.AllPages) && k.Row < options.Rows && k.Column < options.Columns)
            .OrderBy(k => k.Page != page) // à la même case, la touche propre à la page passe devant la touche globale
            .DistinctBy(k => (k.Row, k.Column));
        foreach (var key in visible)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = key.Label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Background = Brush.Parse(key.Color),
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(3),
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
            case KeyAction.VoidSelected:
                if (ticketList.SelectedIndex < 0) status.Text = "Touchez d'abord une ligne du ticket.";
                else ticket.RemoveLine(ticketList.SelectedIndex);
                break;
            case KeyAction.ClearTicket: ticket.Clear(); break;
            case KeyAction.PayCash: Pay(ticket, PaymentMethod.Cash); break;
            case KeyAction.PayCard: Pay(ticket, PaymentMethod.Card); break;
            case KeyAction.PayCheque: Pay(ticket, PaymentMethod.Cheque); break;
            case KeyAction.FreeAmount: OpenFreeAmount(); break;
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

    bool Pay(Ticket ticket, PaymentMethod method)
    {
        if (ticket.Lines.Count == 0) return false;
        try
        {
            using var scope = services.CreateScope();
            var sale = scope.ServiceProvider.GetRequiredService<Shop>().CompleteSale(ticket, method);
            var label = method switch { PaymentMethod.Cash => "espèces", PaymentMethod.Card => "carte", _ => "chèque" };
            status.Text = $"Vente n°{sale.Id} — {sale.Total:C} ({label})";
            ticket.Clear();
            return true;
        }
        catch (Exception ex)
        {
            // Le ticket est conservé : rien n'est perdu, on peut réessayer.
            status.Text = $"Erreur, vente non enregistrée : {ex.Message}";
            return false;
        }
    }

    void RefreshTicket()
    {
        ticketList.ItemsSource = ticket.Lines.Select(l => $"{l.Quantity} × {l.Label}    {l.Amount:C}").ToList();
        total.Text = ticket.Total.ToString("C");
    }
}

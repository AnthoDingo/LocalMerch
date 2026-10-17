# LocalMerch

Application de caisse de magasin programmable, en C#, destinée à tourner sur Linux.

## Objectif

- **Caisse (front)** : écran tactile de vente. Les touches (produits, raccourcis, fonctions) ne sont pas codées en dur : leur disposition et leur action viennent de la configuration.
- **Moteur web embarqué** : l'application héberge son propre serveur HTTP (Kestrel). Une interface d'administration accessible depuis un navigateur du réseau local permet de gérer :
  - la configuration des touches de la caisse (disposition, libellé, couleur, produit/action associé) ;
  - les fournisseurs (coordonnées, adresses) ;
  - les commandes fournisseurs ;
  - les devis ;
  - les entrées et sorties de stock.

Un seul processus : la caisse et le serveur web partagent la même base et le même code métier.

## Stack

- .NET 10 (LTS), C#, nullable activé.
- Interface caisse : Avalonia UI (multiplateforme, fonctionne sous Linux X11/Wayland).
- Serveur web : ASP.NET Core minimal APIs hébergé dans le même processus que la caisse. Front d'admin en HTML/JS servi en fichiers statiques (`wwwroot`), sans framework JS tant que ce n'est pas nécessaire.
- Base de données : SQLite (fichier local) via EF Core, migrations EF.
- Tests : xUnit.

> Stack validée. Si un choix change, mettre à jour cette section.

## Structure

```
src/
  LocalMerch.Core/      # domaine + accès données — aucune dépendance UI
    Model.cs            # entités, Money (arrondis, TVA)
    LocalMerchDb.cs     # DbContext EF Core (SQLite), événement CatalogChanged
    Shop.cs             # opérations métier (stock, ventes, devis, commandes), Ticket, LocalNetwork
    Backup.cs           # sauvegarde / restauration de la base (API de sauvegarde SQLite)
    Migrations/
  LocalMerch.App/       # point d'entrée : Kestrel (admin web) + Avalonia (caisse)
    Program.cs          # démarrage, auth, filtre réseau local
    AdminApi.cs         # API JSON /api/* (CRUD générique + actions métier)
    PosApp.cs           # écran de caisse, construit en code (pas de XAML), conçu en 1024×768 (4:3)
    wwwroot/index.html  # page d'admin, HTML/JS sans framework
tests/
  LocalMerch.Tests/     # xUnit, SQLite en mémoire
deploy/
  localmerch.service    # service systemd utilisateur
```

- Le serveur web tourne en arrière-plan, et la caisse Avalonia sur le thread principal.
- Quand un produit ou une touche est enregistré, `LocalMerchDb.CatalogChanged` est levé, et la caisse se recharge.
- Les règles qui touchent plusieurs tables passent par `Shop`, jamais directement par le CRUD de l'API.

Ne pas créer de projet supplémentaire sans besoin réel.

## Règles métier

- **Montants** : toujours `decimal`, jamais `double`/`float`. Arrondi explicite (`MidpointRounding.AwayFromZero`) au centime.
- **Stock** : le stock est un journal de mouvements (entrée, sortie, vente, ajustement, réception de commande). La quantité courante se calcule depuis les mouvements ; on ne modifie jamais une quantité directement.
- **Ventes** : une vente validée est immuable. Une correction = une opération d'annulation/avoir, pas une modification.
- **Devis** : il s'agit de devis *fournisseurs*.
- **Devis → commande** : un devis peut être converti en commande ; la commande garde la référence du devis.
- **Réception de commande** : génère les mouvements d'entrée de stock correspondants.
- **Montant libre** : une page dédiée, ouverte uniquement par une touche `FreeAmount`. Elle sert aux commandes passées sur un catalogue extérieur : on saisit le montant TTC et le n° de bon, puis on paie (CB, espèces, chèque). Le paiement porte sur un ticket séparé, sans toucher au ticket en cours. La TVA n'est pas demandée : on applique `Pos:FreeAmountVatRate` (20 % par défaut). La ligne n'a pas de produit (`ProductId` null), donc elle ne crée aucun mouvement de stock.
- **TPE réseau** : sa configuration (`PaymentTerminal`, une seule ligne, Id = 1) se fait dans l'onglet TPE de l'admin. Le protocole de paiement (Caisse-AP / Concert) n'est **pas encore implémenté** : le bouton « Payer par carte » enregistre la vente sans interroger le TPE.
- Horodatage en UTC en base, affichage en heure locale.

## Sécurité

- **Rôles** : ils ne concernent que l'admin web, la caisse n'a pas de connexion.
  - `Manager` (Responsable) : tout, sauf les onglets Utilisateurs, TPE et Configuration.
  - `Admin` : tout.
- Les politiques sont définies dans `Policies` (AdminApi.cs). Le groupe `/api` exige `Manager`, et le sous-groupe `admin` exige `Admin`.
- À chaque requête, la session est revalidée en base (compte actif, même rôle, même login). Un compte désactivé est donc déconnecté immédiatement.
- Les mots de passe sont hachés en PBKDF2 (`Secret`). Ils ne sont jamais renvoyés par l'API.
- Le dernier administrateur actif ne peut être ni supprimé, ni désactivé, ni rétrogradé.
- Une restauration copie d'abord la base actuelle dans `<données>/backups/`. Elle refuse les fichiers corrompus, ceux qui ne viennent pas de LocalMerch et ceux créés par une version plus récente.

- L'admin web modifie la caisse : accès protégé (authentification) et écoute limitée au réseau local par défaut.
- Valider toutes les entrées reçues par l'API web.
- Pas de secrets dans le dépôt.

## Linux / déploiement

- Cible : Linux x64 (éventuellement ARM64 pour un mini-PC/Raspberry), **écran 4:3**. La caisse est dessinée en 1024×768 dans un `Viewbox` : ne pas dépasser cette surface. Grille par défaut : 6 lignes × 5 colonnes.
- Publication : `dotnet publish -c Release -r linux-x64 --self-contained`.
- Lancement en service `systemd` ; la caisse démarre en plein écran.
- Chemins : utiliser `Path.Combine` et des emplacements standards (`Environment.SpecialFolder.ApplicationData`), jamais de chemins Windows codés en dur.
- Le développement peut se faire sous Windows ; vérifier régulièrement le comportement sous Linux.

## Commandes

```bash
dotnet build
dotnet test
dotnet run --project src/LocalMerch.App              # caisse + admin sur http://localhost:5080
dotnet run --project src/LocalMerch.App -- --no-ui   # admin web seul (sans écran)
dotnet ef migrations add <Nom> --project src/LocalMerch.Core --startup-project src/LocalMerch.App
dotnet publish src/LocalMerch.App -c Release -r linux-x64 --self-contained -o publish
```

## Configuration (`appsettings.json`, surchargeable par variables d'environnement `Section__Clé`)

- `Urls` : adresse d'écoute (par défaut `http://0.0.0.0:5080`).
- `Admin:Password` : mot de passe du compte `admin`, créé quand aucun administrateur actif n'existe. S'il est vide, un mot de passe est généré dans `<données>/admin-password.txt`.
- `Admin:AllowRemote` : `false` par défaut (seuls le loopback et les réseaux privés sont acceptés).
- `Pos:Rows`, `Pos:Columns`, `Pos:FullScreen` : grille des touches et plein écran.
- `Pos:FreeAmountVatRate` : taux de TVA des montants libres (20 par défaut).
- `Culture` : `fr-FR` (format monétaire).
- Le dossier de données vaut `LOCALMERCH_DATA`, sinon `ApplicationData/LocalMerch` (`~/.config/LocalMerch` sous Linux). Il contient la base `localmerch.db`, qui est migrée automatiquement au démarrage.
- Sous Linux, ICU (`libicu`) doit être installé pour la culture fr-FR.

## Conventions

- Code et identifiants en anglais, textes affichés à l'utilisateur en français.
- Garder le code simple : pas d'interface à une seule implémentation, pas de couche d'abstraction « pour plus tard ».
- Logique métier (calculs de prix, TVA, stock) couverte par des tests.

# LocalMerch

Caisse de magasin programmable pour Linux, avec son interface d'administration web intégrée.

- **Caisse tactile** (Avalonia, 1024×768, écran 4:3) : les touches (produits, raccourcis, fonctions) se configurent depuis l'admin, rien n'est codé en dur.
- **Admin web** sur le réseau local : touches, produits, ventes, stock, fournisseurs, devis et commandes fournisseurs, utilisateurs et droits, TPE, sauvegarde et restauration, mises à jour.
- **Un seul processus**, une base SQLite locale. Pas de cloud, pas d'abonnement.

## Fonctionnalités

- Stock tenu en journal de mouvements (entrées, sorties, ventes, ajustements, réceptions).
- Ventes immuables : une correction passe par une annulation.
- Devis fournisseurs convertibles en commandes, réception de commande → entrée en stock.
- Montant libre pour les commandes passées sur catalogue extérieur.
- Comptes `Admin` / `Standard` avec droits par page (aucun, lecture, écriture).
- Mises à jour depuis les releases GitHub, avec sauvegarde de la base avant installation et vérification SHA-256.

> Le paiement par TPE (Caisse-AP / Concert) n'est pas encore implémenté : « Payer par carte » enregistre la vente sans interroger le terminal.

## Installation (Linux)

Prérequis : une session graphique (X11 ou Wayland) et `libicu`.

```bash
tar -xzf LocalMerch-vX.Y.Z-linux-x64.tar.gz -C ~
mkdir -p ~/.config/systemd/user
cp ~/localmerch/localmerch.service ~/.config/systemd/user/
systemctl --user enable --now localmerch
```

Prenez l'archive `linux-x64` ou `linux-arm64` sur la page [Releases](https://github.com/AnthoDingo/LocalMerch/releases).

L'admin est alors accessible sur `http://<ip-de-la-caisse>:5080`. Au premier lancement, le compte est `admin` / `admin` (ou `Admin:Password` s'il est défini), et le mot de passe doit être changé à la première connexion.

## Configuration

Dans `appsettings.json`, ou par variables d'environnement `Section__Clé` :

| Clé | Rôle | Défaut |
|---|---|---|
| `Urls` | Adresse d'écoute | `http://0.0.0.0:5080` |
| `Admin:Password` | Mot de passe initial du compte `admin` | `admin` |
| `Admin:AllowRemote` | Accepter les accès hors réseau local | `false` |
| `Pos:Rows`, `Pos:Columns` | Grille des touches | 6 × 5 |
| `Pos:FullScreen` | Caisse en plein écran | `true` |
| `Pos:FreeAmountVatRate` | TVA des montants libres (%) | `20` |
| `Updates:Repository` | Dépôt GitHub des mises à jour (vide = désactivé) | `AnthoDingo/LocalMerch` |
| `Culture` | Format monétaire | `fr-FR` |

Les données (base `localmerch.db`, sauvegardes) sont dans `LOCALMERCH_DATA`, sinon `~/.config/LocalMerch`.

## Développement

.NET 10 requis. Le développement fonctionne aussi sous Windows.

```bash
dotnet build
dotnet test
dotnet run --project src/LocalMerch.App              # caisse + admin sur http://localhost:5080
dotnet run --project src/LocalMerch.App -- --no-ui   # admin web seul
```

Une release est publiée en poussant un tag `vX.Y.Z`.

## Licence

[GNU AGPL v3 ou ultérieure](LICENSE.md). L'admin web étant accessible par le réseau, le code source doit être proposé à ses utilisateurs.

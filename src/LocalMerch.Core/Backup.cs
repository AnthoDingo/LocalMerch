using Microsoft.Data.Sqlite;

namespace LocalMerch.Core;

/// <summary>
/// Sauvegarde et restauration de la base complète (données + configuration : touches, utilisateurs, TPE, logo…).
/// Passe par l'API de sauvegarde SQLite : copie cohérente même pendant que la caisse tourne.
/// </summary>
public static class Backup
{
    public static void Save(string databasePath, string destinationPath)
    {
        using var source = Open(databasePath, readOnly: true);
        using var destination = Open(destinationPath);
        source.BackupDatabase(destination);
    }

    /// <summary>Refuse un fichier corrompu, étranger à LocalMerch ou créé par une version plus récente.</summary>
    public static void Validate(string path, IEnumerable<string> knownMigrations)
    {
        try
        {
            using var c = Open(path, readOnly: true);
            if (Scalar(c, "PRAGMA integrity_check") as string != "ok")
                throw new ShopException("Fichier de sauvegarde corrompu.");
            if (Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'") is 0L)
                throw new ShopException("Ce fichier n'est pas une sauvegarde LocalMerch.");

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            using var reader = cmd.ExecuteReader();
            var known = knownMigrations.ToHashSet();
            while (reader.Read())
                if (!known.Contains(reader.GetString(0)))
                    throw new ShopException("Sauvegarde créée par une version plus récente de LocalMerch : mettez l'application à jour.");
        }
        catch (SqliteException)
        {
            throw new ShopException("Ce fichier n'est pas une sauvegarde LocalMerch valide.");
        }
    }

    /// <summary>Remplace le contenu de la base par celui de la sauvegarde (à valider avant).</summary>
    public static void Restore(string backupPath, string databasePath)
    {
        using (var source = Open(backupPath, readOnly: true))
        using (var destination = Open(databasePath))
            source.BackupDatabase(destination);
        SqliteConnection.ClearAllPools();
    }

    static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static SqliteConnection Open(string path, bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // sinon le fichier reste ouvert et ne peut pas être supprimé
        }.ToString());
        c.Open();
        return c;
    }
}

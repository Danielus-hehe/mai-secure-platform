using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace MAI.DataAccessLayer.Maintenance
{
    /// <summary>
    /// Ce poate face rolul aplicației după provizionare. Verificat în bază, nu
    /// presupus: <c>has_table_privilege</c> ține cont și de rolurile moștenite
    /// și de drepturile date lui PUBLIC.
    /// </summary>
    public sealed record AppRoleReport(
        string RoleName,
        bool Created,
        bool CanReadUsers,
        bool CanInsertAudit,
        bool CanUpdateAudit,
        bool CanDeleteAudit,
        bool CanWriteMigrationHistory,
        IReadOnlyList<string> RowSecurityDisabled,
        IReadOnlyList<string> RowSecurityWithPolicies)
    {
        /// <summary>Jurnalul e append-only și istoricul migrărilor e doar citibil.</summary>
        public bool IsLeastPrivilege =>
            CanReadUsers && CanInsertAudit && !CanUpdateAudit && !CanDeleteAudit && !CanWriteMigrationHistory;
    }

    /// <summary>
    /// Rolul PostgreSQL cu care rulează API-ul, separat de proprietarul schemei.
    ///
    /// Înainte, API-ul se conecta cu proprietarul bazei (în Docker chiar
    /// superutilizator). O vulnerabilitate în aplicație (o injecție SQL, un
    /// endpoint scăpat de sub autorizare) avea deci aceleași drepturi ca
    /// administratorul bazei: putea șterge jurnalul de audit, rescrie istoricul
    /// migrărilor, crea sau distruge tabele.
    ///
    /// Rolul aplicației primește doar ce folosește efectiv codul:
    ///   - SELECT, INSERT, UPDATE, DELETE pe tabelele aplicației;
    ///   - pe AuditLogs doar SELECT și INSERT: jurnalul devine append-only la
    ///     nivelul bazei, nu doar prin disciplina codului. Nici măcar un API
    ///     compromis nu poate acoperi urmele;
    ///   - pe __EFMigrationsHistory doar SELECT: schema o schimbă doar db:migrate,
    ///     rulat de proprietar;
    ///   - niciun drept DDL (CREATE pe schema public).
    ///
    /// Idempotent: rulat la fiecare db:migrate, deci și după restaurarea unui
    /// backup (care se face cu --no-privileges și pierde drepturile), și după o
    /// migrare care adaugă tabele (drepturile implicite le acoperă oricum).
    ///
    /// Trebuie rulat de proprietarul schemei, cu drept de CREATE ROLE.
    /// </summary>
    public static class DatabaseRoleProvisioner
    {
        /// <summary>
        /// Nume de rol acceptat: identificator PostgreSQL simplu, fără ghilimele.
        /// Oricum ajunge în SQL doar prin format('%I'), dar un nume ciudat
        /// („SGDM App”) e aproape sigur o greșeală de configurare.
        /// </summary>
        public static readonly Regex RoleNamePattern = new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

        /// <summary>Lungimea minimă a parolei rolului. Nu o tastează nimeni: stă în .env.</summary>
        public const int MinPasswordLength = 16;

        public static async Task<AppRoleReport> EnsureAppRoleAsync(
            AppDbContext db, string roleName, string password, CancellationToken ct = default)
        {
            if (!RoleNamePattern.IsMatch(roleName))
                throw new InvalidOperationException(
                    $"Numele rolului aplicației „{roleName}” nu e valid: litere mici, cifre și '_', maxim 63 de caractere.");

            if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
                throw new InvalidOperationException(
                    $"Parola rolului aplicației trebuie să aibă cel puțin {MinPasswordLength} caractere.");

            var connection = db.Database.GetDbConnection();
            var opened     = connection.State != System.Data.ConnectionState.Open;
            if (opened) await connection.OpenAsync(ct);

            try
            {
                var owner    = (string)(await ScalarAsync(connection, null, "SELECT current_user", ct))!;
                var database = (string)(await ScalarAsync(connection, null, "SELECT current_database()", ct))!;

                // Protecția cea mai importantă din clasă: dacă rolul aplicației ar
                // fi chiar proprietarul, ALTER ROLE de mai jos i-ar lua acestuia
                // drepturile de superutilizator, iar REVOKE-urile l-ar lăsa fără
                // acces la propriul jurnal.
                if (string.Equals(owner, roleName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Rolul aplicației („{roleName}”) nu poate fi proprietarul schemei. " +
                        "POSTGRES_APP_USER trebuie să difere de POSTGRES_USER.");

                await using var tx = await connection.BeginTransactionAsync(ct);

                var exists = (bool)(await ScalarAsync(connection, tx,
                    "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @p0)", ct, roleName))!;

                // Parola ajunge în SQL doar prin format('%L') calculat de server,
                // deci ghilimelele din ea nu pot sparge instrucțiunea. Instrucțiunea
                // nu se scrie în niciun log al aplicației. Atribute explicite, ca un
                // rol creat manual cu drepturi prea mari să fie adus la minim.
                const string attributes =
                    "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS";

                await ExecuteFormattedAsync(connection, tx,
                    exists
                        ? $"ALTER ROLE %I WITH {attributes} PASSWORD %L"
                        : $"CREATE ROLE %I WITH {attributes} PASSWORD %L",
                    ct, roleName, password);

                await ExecuteFormattedAsync(connection, tx, "GRANT CONNECT ON DATABASE %I TO %I", ct, database, roleName);
                await ExecuteFormattedAsync(connection, tx, "GRANT USAGE ON SCHEMA public TO %I", ct, roleName);

                // Fără DDL pentru nimeni în afara proprietarului. Implicit în
                // PostgreSQL 15+, dar o bază mutată de pe o versiune mai veche
                // (sau de pe Supabase) poate avea încă CREATE dat lui PUBLIC.
                await ExecuteAsync(connection, tx, "REVOKE CREATE ON SCHEMA public FROM PUBLIC", ct);

                await ExecuteFormattedAsync(connection, tx,
                    "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I", ct, roleName);
                await ExecuteFormattedAsync(connection, tx,
                    "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I", ct, roleName);

                // Tabelele create de migrările viitoare primesc aceleași drepturi
                // automat, fără să depindă de rularea din nou a acestei metode.
                await ExecuteFormattedAsync(connection, tx,
                    "ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public " +
                    "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I", ct, owner, roleName);
                await ExecuteFormattedAsync(connection, tx,
                    "ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public " +
                    "GRANT USAGE, SELECT ON SEQUENCES TO %I", ct, owner, roleName);

                // Row Level Security rămas de pe Supabase. Acolo RLS se activează
                // pe tabele (recomandarea Supabase pentru API-ul lor public), iar
                // politicile țin de rolurile Supabase (anon, authenticated).
                // Restaurarea sare politicile - rolurile nu există aici - dar
                // lasă RLS activat, adică „nimic permis” pentru orice rol care nu
                // e proprietar. Proprietarul trece peste RLS, deci până acum nu
                // se vedea; rolul aplicației nu trece, iar primul INSERT în
                // AuditLogs pica cu „new row violates row-level security policy”.
                //
                // Se dezactivează DOAR pe tabelele fără nicio politică: acolo RLS
                // nu protejează nimic, doar blochează. Un tabel cu politici e o
                // decizie a cuiva; nu îl atingem, iar comanda îl raportează.
                var disabled = new List<string>();
                foreach (var table in await ListAsync(connection, tx, RowSecurityWithoutPoliciesSql, ct))
                {
                    await ExecuteFormattedAsync(connection, tx,
                        "ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY", ct, table);
                    disabled.Add(table);
                }

                var withPolicies = await ListAsync(connection, tx, RowSecurityWithPoliciesSql, ct);

                // Excepțiile, după GRANT-ul general, ca să nu fie anulate de el.
                await ExecuteFormattedAsync(connection, tx,
                    "REVOKE UPDATE, DELETE, TRUNCATE ON \"AuditLogs\" FROM %I", ct, roleName);
                await ExecuteFormattedAsync(connection, tx,
                    "REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON \"__EFMigrationsHistory\" FROM %I", ct, roleName);

                await tx.CommitAsync(ct);

                return new AppRoleReport(
                    RoleName:                 roleName,
                    Created:                  !exists,
                    CanReadUsers:             await HasPrivilegeAsync(connection, roleName, "\"Users\"", "SELECT", ct),
                    CanInsertAudit:           await HasPrivilegeAsync(connection, roleName, "\"AuditLogs\"", "INSERT", ct),
                    CanUpdateAudit:           await HasPrivilegeAsync(connection, roleName, "\"AuditLogs\"", "UPDATE", ct),
                    CanDeleteAudit:           await HasPrivilegeAsync(connection, roleName, "\"AuditLogs\"", "DELETE", ct)
                                              || await HasPrivilegeAsync(connection, roleName, "\"AuditLogs\"", "TRUNCATE", ct),
                    CanWriteMigrationHistory: await HasPrivilegeAsync(connection, roleName, "\"__EFMigrationsHistory\"", "INSERT", ct),
                    RowSecurityDisabled:      disabled,
                    RowSecurityWithPolicies:  withPolicies);
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }
        }

        private const string RowSecurityWithoutPoliciesSql = """
            SELECT c.relname
              FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace
               AND c.relkind IN ('r', 'p')
               AND c.relrowsecurity
               AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid)
             ORDER BY c.relname
            """;

        private const string RowSecurityWithPoliciesSql = """
            SELECT c.relname
              FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace
               AND c.relkind IN ('r', 'p')
               AND c.relrowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid)
             ORDER BY c.relname
            """;

        private static async Task<List<string>> ListAsync(
            DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;

            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetString(0));
            return result;
        }

        private static async Task<bool> HasPrivilegeAsync(
            DbConnection connection, string role, string table, string privilege, CancellationToken ct) =>
            (bool)(await ScalarAsync(connection, null,
                "SELECT has_table_privilege(@p0, @p1, @p2)", ct, role, table, privilege))!;

        /// <summary>
        /// Construiește instrucțiunea pe server cu format(), care citează corect
        /// identificatorii (%I) și literalii (%L), apoi o execută. Numele de rol,
        /// baza și parola nu sunt niciodată lipite ca text în C#.
        /// </summary>
        private static async Task ExecuteFormattedAsync(
            DbConnection connection, DbTransaction tx, string template, CancellationToken ct, params string[] values)
        {
            var placeholders = string.Join(", ", values.Select((_, i) => $"@p{i + 1}"));
            var args = new object[values.Length + 1];
            args[0] = template;
            for (var i = 0; i < values.Length; i++) args[i + 1] = values[i];

            var sql = (string)(await ScalarAsync(connection, tx, $"SELECT format(@p0, {placeholders})", ct, args))!;
            await ExecuteAsync(connection, tx, sql, ct);
        }

        private static async Task ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        private static async Task<object?> ScalarAsync(
            DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct, params object[] values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;

            for (var i = 0; i < values.Length; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"p{i}";
                parameter.Value         = values[i];
                command.Parameters.Add(parameter);
            }

            return await command.ExecuteScalarAsync(ct);
        }
    }
}

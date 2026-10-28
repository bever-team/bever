using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Bever.Sqlite;

public sealed class BeverSqliteDatabase
{
    private const string SchemaTable = "__bever_schema";
    private const string MigrationsTable = "__bever_migrations";
    private const string IdentitySchemaTable = "__bever_identity_schema";
    private const string IdentitySchemaVersion = "1";
    private static readonly (string Name, string Sql)[] IdentityTables =
    [
        (IdentitySchemaTable, $"CREATE TABLE {Quote(IdentitySchemaTable)} (id INTEGER PRIMARY KEY CHECK (id = 1), schema_version TEXT NOT NULL, schema_hash TEXT NOT NULL, applied_on TEXT NOT NULL)"),
        ("__bever_identities", "CREATE TABLE \"__bever_identities\" (\"Id\" TEXT PRIMARY KEY, \"Email\" TEXT NOT NULL, \"NormalizedEmail\" TEXT NOT NULL UNIQUE, \"Label\" TEXT NOT NULL, \"DisabledOn\" TEXT NULL, \"DeletedOn\" TEXT NULL, \"SecurityStamp\" TEXT NOT NULL, \"SessionVersion\" INTEGER NOT NULL, \"CreatedOn\" TEXT NOT NULL, \"ModifiedOn\" TEXT NOT NULL)"),
        ("__bever_identity_credentials", "CREATE TABLE \"__bever_identity_credentials\" (\"IdentityId\" TEXT PRIMARY KEY REFERENCES \"__bever_identities\" (\"Id\") ON DELETE CASCADE, \"PasswordHash\" TEXT NOT NULL, \"UpdatedOn\" TEXT NOT NULL)"),
        ("__bever_identity_administrators", "CREATE TABLE \"__bever_identity_administrators\" (\"IdentityId\" TEXT PRIMARY KEY REFERENCES \"__bever_identities\" (\"Id\") ON DELETE CASCADE, \"AssignedOn\" TEXT NOT NULL)"),
        ("__bever_identity_grants", "CREATE TABLE \"__bever_identity_grants\" (\"Id\" TEXT PRIMARY KEY, \"IdentityId\" TEXT NOT NULL REFERENCES \"__bever_identities\" (\"Id\") ON DELETE CASCADE, \"Entity\" TEXT NOT NULL, \"Action\" TEXT NOT NULL, \"RecordId\" TEXT NULL, \"CreatedOn\" TEXT NOT NULL)"),
        ("__bever_identity_roles", "CREATE TABLE \"__bever_identity_roles\" (\"IdentityId\" TEXT NOT NULL REFERENCES \"__bever_identities\" (\"Id\") ON DELETE CASCADE, \"Role\" TEXT NOT NULL COLLATE NOCASE, \"AssignedOn\" TEXT NOT NULL, PRIMARY KEY (\"IdentityId\", \"Role\"))"),
        ("__bever_identity_audit", "CREATE TABLE \"__bever_identity_audit\" (\"Id\" TEXT PRIMARY KEY, \"OccurredOn\" TEXT NOT NULL, \"Operation\" TEXT NOT NULL, \"IdentityId\" TEXT NULL, \"MetadataJson\" TEXT NOT NULL)")
    ];
    private static readonly (string Name, string Sql)[] IdentityIndexes =
    [
        ("IX___bever_identity_grants_identity", "CREATE INDEX \"IX___bever_identity_grants_identity\" ON \"__bever_identity_grants\" (\"IdentityId\")")
    ];
    private static readonly HashSet<string> KnownIdentityArtifactNames = IdentityTables.Select(table => table.Name).Concat(IdentityIndexes.Select(index => index.Name)).ToHashSet(StringComparer.Ordinal);
    private static readonly string IdentitySchemaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', IdentityTables.Select(table => table.Sql).Concat(IdentityIndexes.Select(index => index.Sql)))))).ToLowerInvariant();
    private static readonly PasswordHashSubject UnknownIdentitySubject = new(string.Empty, string.Empty);
    private static readonly Lazy<string> UnknownIdentityHash = new(() => new PasswordHasher<PasswordHashSubject>().HashPassword(UnknownIdentitySubject, Guid.NewGuid().ToString("N")));
    private readonly PasswordHasher<PasswordHashSubject> passwordHasher = new();
    private readonly string connectionString;
    private readonly string fullPath;
    private readonly bool createIfMissing;
    private readonly PersistenceSchema schema;
    private readonly IReadOnlyDictionary<string, PersistenceEntity> entities;

    public BeverSqliteDatabase(string databasePath, PersistenceSchema schema, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.schema = schema;
        this.createIfMissing = createIfMissing;
        entities = schema.Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);

        fullPath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string SchemaHash => SchemaJson.Hash(schema);

    public async Task<string?> GetCurrentSchemaHashAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpenExistingDatabase())
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await TableExistsAsync(connection, SchemaTable, cancellationToken))
        {
            return null;
        }

        var (hash, _) = await ReadStoredSchemaAsync(connection, cancellationToken);
        return hash;
    }

    public async Task<BeverSqliteSchemaStatus> GetSchemaStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpenExistingDatabase())
        {
            return BeverSqliteSchemaStatus.Uninitialized;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await TableExistsAsync(connection, SchemaTable, cancellationToken))
        {
            return await HasApplicationTablesAsync(connection, cancellationToken)
                ? BeverSqliteSchemaStatus.SchemaDrift
                : BeverSqliteSchemaStatus.Uninitialized;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT schema_hash FROM {Quote(SchemaTable)} WHERE id = 1";
        var storedHash = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(storedHash, SchemaHash, StringComparison.Ordinal))
        {
            return BeverSqliteSchemaStatus.SchemaMismatch;
        }
        if (!await TableExistsAsync(connection, MigrationsTable, cancellationToken))
        {
            return BeverSqliteSchemaStatus.SchemaDrift;
        }

        foreach (var entity in schema.Entities)
        {
            if (!await HasExpectedColumnsAsync(connection, entity, cancellationToken))
            {
                return BeverSqliteSchemaStatus.SchemaDrift;
            }
        }

        return BeverSqliteSchemaStatus.Current;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await TableExistsAsync(connection, SchemaTable, cancellationToken))
        {
            var status = await GetSchemaStatusAsync(cancellationToken);
            if (status != BeverSqliteSchemaStatus.Current)
            {
                throw new InvalidOperationException($"The SQLite database cannot be initialized because its schema status is '{status}'.");
            }

            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entity in schema.Entities)
        {
            await ExecuteAsync(connection, transaction, BuildCreateTableSql(entity), cancellationToken);
            foreach (var field in entity.Fields.Where(field => field.Kind == "Relationship"))
            {
                await ExecuteAsync(connection, transaction, BuildIndexSql(entity, field), cancellationToken);
            }
        }

        await ExecuteAsync(connection, transaction,
            $"CREATE TABLE {Quote(SchemaTable)} (id INTEGER PRIMARY KEY CHECK (id = 1), schema_hash TEXT NOT NULL, schema_json TEXT NOT NULL)",
            cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"CREATE TABLE {Quote(MigrationsTable)} (id TEXT PRIMARY KEY, name TEXT NOT NULL, from_schema_hash TEXT NOT NULL, to_schema_hash TEXT NOT NULL, applied_on TEXT NOT NULL)",
            cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"INSERT INTO {Quote(SchemaTable)} (id, schema_hash, schema_json) VALUES (1, $hash, $schema)";
            command.Parameters.AddWithValue("$hash", SchemaHash);
            command.Parameters.AddWithValue("$schema", SchemaJson.Serialize(schema));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BeverSqliteIdentitySchemaStatus> GetIdentitySchemaStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOpenExistingDatabase())
        {
            return BeverSqliteIdentitySchemaStatus.Uninitialized;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await TableExistsAsync(connection, IdentitySchemaTable, cancellationToken))
        {
            return await HasPrivateIdentityArtifactsAsync(connection, cancellationToken)
                ? BeverSqliteIdentitySchemaStatus.SchemaDrift
                : BeverSqliteIdentitySchemaStatus.Uninitialized;
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT schema_version, schema_hash FROM {Quote(IdentitySchemaTable)} WHERE id = 1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return BeverSqliteIdentitySchemaStatus.SchemaDrift;
            }
            if (reader.GetString(0) != IdentitySchemaVersion || reader.GetString(1) != IdentitySchemaHash)
            {
                return BeverSqliteIdentitySchemaStatus.SchemaMismatch;
            }
        }

        foreach (var (name, _) in IdentityTables)
        {
            if (!await TableExistsAsync(connection, name, cancellationToken))
            {
                return BeverSqliteIdentitySchemaStatus.SchemaDrift;
            }
        }
        foreach (var (name, sql) in IdentityTables)
        {
            if (!string.Equals(await ReadSqliteMasterSqlAsync(connection, "table", name, cancellationToken), sql, StringComparison.Ordinal))
            {
                return BeverSqliteIdentitySchemaStatus.SchemaDrift;
            }
        }
        foreach (var (name, sql) in IdentityIndexes)
        {
            if (!string.Equals(await ReadSqliteMasterSqlAsync(connection, "index", name, cancellationToken), sql, StringComparison.Ordinal))
            {
                return BeverSqliteIdentitySchemaStatus.SchemaDrift;
            }
        }
        if (await HasUnexpectedPrivateIdentityArtifactsAsync(connection, cancellationToken))
        {
            return BeverSqliteIdentitySchemaStatus.SchemaDrift;
        }

        return BeverSqliteIdentitySchemaStatus.Current;
    }

    public async Task ApplyIdentitySchemaAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetIdentitySchemaStatusAsync(cancellationToken);
        if (status == BeverSqliteIdentitySchemaStatus.Current)
        {
            return;
        }
        if (status != BeverSqliteIdentitySchemaStatus.Uninitialized)
        {
            throw new InvalidOperationException($"The SQLite identity schema cannot be applied because its status is '{status}'.");
        }

        await EnsureIdentitySchemaCreatedAsync(cancellationToken);
    }

    public async Task EnsureIdentitySchemaCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await TableExistsAsync(connection, IdentitySchemaTable, cancellationToken))
        {
            var status = await GetIdentitySchemaStatusAsync(cancellationToken);
            if (status != BeverSqliteIdentitySchemaStatus.Current)
            {
                throw new InvalidOperationException($"The SQLite identity schema cannot be initialized because its status is '{status}'.");
            }
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (_, sql) in IdentityTables)
        {
            await ExecuteAsync(connection, transaction, sql, cancellationToken);
        }
        foreach (var (_, sql) in IdentityIndexes)
        {
            await ExecuteAsync(connection, transaction, sql, cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"INSERT INTO {Quote(IdentitySchemaTable)} (id, schema_version, schema_hash, applied_on) VALUES (1, $version, $hash, $appliedOn)";
            command.Parameters.AddWithValue("$version", IdentitySchemaVersion);
            command.Parameters.AddWithValue("$hash", IdentitySchemaHash);
            command.Parameters.AddWithValue("$appliedOn", Now());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BeverSqliteIdentityRecord> CreateIdentityAsync(string email, string passwordHash, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var id = "idn_" + Guid.NewGuid().ToString("N");
        var now = Now();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteIdentityInsertAsync(connection, transaction, id, email.Trim(), normalizedEmail, passwordHash, now, cancellationToken);
        await WriteIdentityAuditAsync(connection, transaction, "identity.create", id, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await FindIdentityByIdAsync(id, cancellationToken))!;
    }

    public async Task<BeverSqliteIdentityRecord> RegisterIdentityAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var id = "idn_" + Guid.NewGuid().ToString("N");
        var subject = new PasswordHashSubject(id, normalizedEmail);
        var passwordHash = passwordHasher.HashPassword(subject, password);
        var now = Now();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteIdentityInsertAsync(connection, transaction, id, email.Trim(), normalizedEmail, passwordHash, now, cancellationToken);
        await WriteIdentityAuditAsync(connection, transaction, "identity.register", id, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await FindIdentityByIdAsync(id, cancellationToken))!;
    }

    public async Task<BeverSqliteIdentityRecord> RegisterBootstrapAdministratorAsync(string email, string password, string operation, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var id = "idn_" + Guid.NewGuid().ToString("N");
        var subject = new PasswordHashSubject(id, normalizedEmail);
        var passwordHash = passwordHasher.HashPassword(subject, password);
        var now = Now();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdentityBootstrapWriteLockAsync(connection, transaction, cancellationToken);
        if (await AdministratorExistsAsync(connection, transaction, cancellationToken))
        {
            throw new InvalidOperationException("Bootstrap refused because an Administrator already exists.");
        }

        await ExecuteIdentityInsertAsync(connection, transaction, id, email.Trim(), normalizedEmail, passwordHash, now, cancellationToken);
        await ExecuteAdministratorInsertAsync(connection, transaction, id, now, cancellationToken);
        await WriteIdentityAuditAsync(connection, transaction, operation, id, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await FindIdentityByIdAsync(id, cancellationToken))!;
    }

    public async Task<bool> VerifyIdentityPasswordAsync(string identityId, string password, CancellationToken cancellationToken)
    {
        var identity = await FindIdentityByIdAsync(identityId, cancellationToken);
        if (identity is null)
        {
            // Same hashing cost as a real verify so unknown emails are not distinguishable by timing.
            passwordHasher.VerifyHashedPassword(UnknownIdentitySubject, UnknownIdentityHash.Value, password);
            return false;
        }

        var result = passwordHasher.VerifyHashedPassword(new PasswordHashSubject(identity.Id, identity.NormalizedEmail), identity.PasswordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    public Task<BeverSqliteIdentityRecord?> FindIdentityByEmailAsync(string email, CancellationToken cancellationToken) =>
        FindIdentityAsync("i.\"NormalizedEmail\" = $value", NormalizeEmail(email), cancellationToken);

    public Task<BeverSqliteIdentityRecord?> FindIdentityByIdAsync(string id, CancellationToken cancellationToken) =>
        FindIdentityAsync("i.\"Id\" = $value", id, cancellationToken);

    public async Task<IReadOnlyList<BeverSqliteIdentityRecord>> ListActiveIdentitiesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT i.\"Id\", i.\"Email\", i.\"NormalizedEmail\", i.\"Label\", c.\"PasswordHash\", i.\"SecurityStamp\", i.\"SessionVersion\" FROM \"__bever_identities\" i JOIN \"__bever_identity_credentials\" c ON c.\"IdentityId\" = i.\"Id\" WHERE i.\"DisabledOn\" IS NULL AND i.\"DeletedOn\" IS NULL";
        var records = new List<BeverSqliteIdentityRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadIdentity(reader));
        }
        return records;
    }

    public async Task AssignAdministratorAsync(string identityId, string operation, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAdministratorInsertAsync(connection, transaction, identityId, Now(), cancellationToken);
        await WriteIdentityAuditAsync(connection, transaction, operation, identityId, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsAdministratorAsync(string identityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM \"__bever_identity_administrators\" WHERE \"IdentityId\" = $id";
        command.Parameters.AddWithValue("$id", identityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<string> AddDirectGrantAsync(string identityId, string entity, string action, string? recordId, string operation, CancellationToken cancellationToken)
    {
        var grantId = "grn_" + Guid.NewGuid().ToString("N");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO \"__bever_identity_grants\" (\"Id\", \"IdentityId\", \"Entity\", \"Action\", \"RecordId\", \"CreatedOn\") VALUES ($id, $identityId, $entity, $action, $recordId, $createdOn)";
            command.Parameters.AddWithValue("$id", grantId);
            command.Parameters.AddWithValue("$identityId", identityId);
            command.Parameters.AddWithValue("$entity", entity);
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$recordId", recordId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$createdOn", Now());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteIdentityAuditAsync(connection, transaction, operation, identityId, JsonSerializer.Serialize(new { grantId, entity, action, recordId }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return grantId;
    }

    public async Task<IReadOnlyList<BeverSqliteDirectGrant>> ListDirectGrantsAsync(string identityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"IdentityId\", \"Entity\", \"Action\", \"RecordId\" FROM \"__bever_identity_grants\" WHERE \"IdentityId\" = $identityId ORDER BY \"CreatedOn\", \"Id\"";
        command.Parameters.AddWithValue("$identityId", identityId);
        var grants = new List<BeverSqliteDirectGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            grants.Add(new BeverSqliteDirectGrant(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return grants;
    }

    public async Task<IReadOnlyList<string>> ListRolesAsync(string identityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ListRolesAsync(connection, null, identityId, cancellationToken);
    }

    // Replaces the account's roles, auditing each added and revoked role. actorId is the Administrator
    // who made the change over HTTP, or null for owner-run CLI commands.
    public async Task SetRolesAsync(string identityId, IReadOnlyList<string> roles, string? actorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ListRolesAsync(connection, transaction, identityId, cancellationToken);
        foreach (var role in current.Where(role => !roles.Contains(role, StringComparer.OrdinalIgnoreCase)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM \"__bever_identity_roles\" WHERE \"IdentityId\" = $id AND \"Role\" = $role";
            command.Parameters.AddWithValue("$id", identityId);
            command.Parameters.AddWithValue("$role", role);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await WriteIdentityAuditAsync(connection, transaction, "identity.role.revoke", identityId, JsonSerializer.Serialize(new { role, by = actorId }), cancellationToken);
        }

        foreach (var role in roles.Where(role => !current.Contains(role, StringComparer.OrdinalIgnoreCase)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO \"__bever_identity_roles\" (\"IdentityId\", \"Role\", \"AssignedOn\") VALUES ($id, $role, $now)";
            command.Parameters.AddWithValue("$id", identityId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$now", Now());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await WriteIdentityAuditAsync(connection, transaction, "identity.role.add", identityId, JsonSerializer.Serialize(new { role, by = actorId }), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ListRolesAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string identityId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = "SELECT \"Role\" FROM \"__bever_identity_roles\" WHERE \"IdentityId\" = $id ORDER BY \"Role\"";
        command.Parameters.AddWithValue("$id", identityId);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString(0));
        }
        return roles;
    }

    public async Task<IReadOnlyList<BeverSqliteIdentityAuditEntry>> ListIdentityAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Operation\", \"IdentityId\", \"MetadataJson\" FROM \"__bever_identity_audit\" ORDER BY \"OccurredOn\", \"Id\"";
        var entries = new List<BeverSqliteIdentityAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new BeverSqliteIdentityAuditEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }
        return entries;
    }

    public async Task ApplyMigrationAsync(MigrationArtifact migration, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await TableExistsAsync(connection, SchemaTable, cancellationToken))
        {
            throw new MigrationException("Cannot apply a migration to an uninitialized SQLite database.");
        }

        var (storedHash, storedSchema) = await ReadStoredSchemaAsync(connection, cancellationToken);
        if (!string.Equals(storedHash, migration.FromSchemaHash, StringComparison.Ordinal))
        {
            throw new MigrationException($"Migration '{migration.Id}' expects schema hash '{migration.FromSchemaHash}', but the database has '{storedHash}'.");
        }
        foreach (var entity in storedSchema.Entities)
        {
            if (!await HasExpectedColumnsAsync(connection, entity, cancellationToken))
            {
                throw new MigrationException($"Cannot apply migration '{migration.Id}' because the live SQLite schema has drifted from its recorded state.");
            }
        }
        if (await MigrationExistsAsync(connection, migration.Id, cancellationToken))
        {
            throw new MigrationException($"Migration '{migration.Id}' is already applied.");
        }

        var targetSchema = ApplyOperations(storedSchema, migration.Operations);
        var targetHash = SchemaJson.Hash(targetSchema);
        if (!string.Equals(targetHash, migration.ToSchemaHash, StringComparison.Ordinal))
        {
            throw new MigrationException($"Migration '{migration.Id}' operations produce schema hash '{targetHash}', not the recorded hash '{migration.ToSchemaHash}'.");
        }

        await SetForeignKeysAsync(connection, enabled: false, cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var workingSchema = Clone(storedSchema);
            foreach (var operation in migration.Operations)
            {
                await ApplyOperationAsync(connection, transaction, workingSchema, operation, cancellationToken);
            }

            await using (var check = connection.CreateCommand())
            {
                check.Transaction = (SqliteTransaction)transaction;
                check.CommandText = "PRAGMA foreign_key_check";
                if (await check.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    throw new MigrationException($"Migration '{migration.Id}' would leave invalid SQLite foreign keys.");
                }
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = $"UPDATE {Quote(SchemaTable)} SET schema_hash = $hash, schema_json = $schema WHERE id = 1";
                update.Parameters.AddWithValue("$hash", targetHash);
                update.Parameters.AddWithValue("$schema", SchemaJson.Serialize(targetSchema));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = (SqliteTransaction)transaction;
                history.CommandText = $"INSERT INTO {Quote(MigrationsTable)} (id, name, from_schema_hash, to_schema_hash, applied_on) VALUES ($id, $name, $from, $to, $appliedOn)";
                history.Parameters.AddWithValue("$id", migration.Id);
                history.Parameters.AddWithValue("$name", migration.Name);
                history.Parameters.AddWithValue("$from", migration.FromSchemaHash);
                history.Parameters.AddWithValue("$to", migration.ToSchemaHash);
                history.Parameters.AddWithValue("$appliedOn", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await history.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await SetForeignKeysAsync(connection, enabled: true, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<JsonObject>> ListAsync(string entityName, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ColumnList(entity)} FROM {Quote(entity.Name)} WHERE {Quote("DeletedOn")} IS NULL";

        var records = new List<JsonObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader, entity));
        }

        return records;
    }

    /// <summary>Reads only the named columns (plus Id) of active records, optionally limited to the given IDs.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListColumnsAsync(string entityName, IReadOnlyList<string> fieldNames, IReadOnlyList<string>? ids, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        var projection = new PersistenceEntity
        {
            Name = entity.Name,
            Fields = entity.Fields.Where(field => field.Name == "Id" || fieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase)).ToList()
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = $"{Quote("DeletedOn")} IS NULL";
        if (ids is not null)
        {
            var parameters = ids.Select((_, index) => $"$id{index}").ToList();
            where += parameters.Count == 0 ? " AND 0" : $" AND {Quote("Id")} IN ({string.Join(", ", parameters)})";
            for (var index = 0; index < ids.Count; index++)
            {
                command.Parameters.AddWithValue(parameters[index], ids[index]);
            }
        }

        command.CommandText = $"SELECT {ColumnList(projection)} FROM {Quote(entity.Name)} WHERE {where}";
        var records = new List<JsonObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader, projection));
        }

        return records;
    }

    public async Task<JsonObject?> GetAsync(string entityName, string id, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ColumnList(entity)} FROM {Quote(entity.Name)} WHERE {Quote("Id")} = $id AND {Quote("DeletedOn")} IS NULL";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader, entity) : null;
    }

    public async Task<JsonObject> CreateAsync(string entityName, JsonObject values, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var storedValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = id,
            ["CreatedOn"] = now,
            ["CreatedBy"] = null,
            ["ModifiedOn"] = now,
            ["ModifiedBy"] = null,
            ["DeletedOn"] = null,
            ["DeletedBy"] = null
        };

        foreach (var field in entity.Fields.Where(field => !field.System || field.Name is "CreatedBy" or "ModifiedBy"))
        {
            values.TryGetPropertyValue(JsonName(field.Name), out var value);
            storedValues[field.Name] = ToDatabaseValue(field, value);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"INSERT INTO {Quote(entity.Name)} ({string.Join(", ", entity.Fields.Select(field => Quote(field.Name)))}) VALUES ({string.Join(", ", entity.Fields.Select((_, index) => $"$p{index}"))})";
        for (var index = 0; index < entity.Fields.Count; index++)
        {
            command.Parameters.AddWithValue($"$p{index}", storedValues[entity.Fields[index].Name] ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(entityName, id, cancellationToken))!;
    }

    public async Task<JsonObject?> PatchAsync(string entityName, string id, JsonObject values, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        var fields = entity.Fields
            .Where(field => (!field.System || field.Name is "ModifiedBy" or "DeletedBy") && values.ContainsKey(JsonName(field.Name)))
            .ToList();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var assignments = fields.Select((field, index) => $"{Quote(field.Name)} = $p{index}").ToList();
        assignments.Add($"{Quote("ModifiedOn")} = $modifiedOn");
        command.CommandText = $"UPDATE {Quote(entity.Name)} SET {string.Join(", ", assignments)} WHERE {Quote("Id")} = $id AND {Quote("DeletedOn")} IS NULL";
        for (var index = 0; index < fields.Count; index++)
        {
            var value = values[JsonName(fields[index].Name)];
            command.Parameters.AddWithValue($"$p{index}", ToDatabaseValue(fields[index], value) ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("$modifiedOn", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetAsync(entityName, id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string entityName, string id, CancellationToken cancellationToken)
        => await DeleteAsync(entityName, id, actorId: null, cancellationToken);

    public async Task<bool> DeleteAsync(string entityName, string id, string? actorId, CancellationToken cancellationToken)
    {
        var entity = GetEntity(entityName);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ActiveRecordExistsAsync(connection, transaction, entity, id, cancellationToken))
        {
            return false;
        }

        var deletedOn = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await ValidateDeleteAllowedAsync(connection, transaction, entity, id, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
        await SoftDeleteAsync(connection, transaction, entity, id, deletedOn, actorId, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task ExecuteIdentityInsertAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string id, string email, string normalizedEmail, string passwordHash, string now, CancellationToken cancellationToken)
    {
        await using (var identity = connection.CreateCommand())
        {
            identity.Transaction = (SqliteTransaction)transaction;
            identity.CommandText = "INSERT INTO \"__bever_identities\" (\"Id\", \"Email\", \"NormalizedEmail\", \"Label\", \"SecurityStamp\", \"SessionVersion\", \"CreatedOn\", \"ModifiedOn\") VALUES ($id, $email, $normalizedEmail, $label, $stamp, 0, $now, $now)";
            identity.Parameters.AddWithValue("$id", id);
            identity.Parameters.AddWithValue("$email", email);
            identity.Parameters.AddWithValue("$normalizedEmail", normalizedEmail);
            identity.Parameters.AddWithValue("$label", normalizedEmail);
            identity.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
            identity.Parameters.AddWithValue("$now", now);
            await identity.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var credential = connection.CreateCommand();
        credential.Transaction = (SqliteTransaction)transaction;
        credential.CommandText = "INSERT INTO \"__bever_identity_credentials\" (\"IdentityId\", \"PasswordHash\", \"UpdatedOn\") VALUES ($id, $hash, $now)";
        credential.Parameters.AddWithValue("$id", id);
        credential.Parameters.AddWithValue("$hash", passwordHash);
        credential.Parameters.AddWithValue("$now", now);
        await credential.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAdministratorInsertAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string identityId, string assignedOn, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT OR IGNORE INTO \"__bever_identity_administrators\" (\"IdentityId\", \"AssignedOn\") VALUES ($id, $now)";
        command.Parameters.AddWithValue("$id", identityId);
        command.Parameters.AddWithValue("$now", assignedOn);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireIdentityBootstrapWriteLockAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"UPDATE {Quote(IdentitySchemaTable)} SET applied_on = applied_on WHERE id = 1";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> AdministratorExistsAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1 FROM \"__bever_identity_administrators\" LIMIT 1";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<BeverSqliteIdentityRecord?> FindIdentityAsync(string predicate, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT i.\"Id\", i.\"Email\", i.\"NormalizedEmail\", i.\"Label\", c.\"PasswordHash\", i.\"SecurityStamp\", i.\"SessionVersion\" FROM \"__bever_identities\" i JOIN \"__bever_identity_credentials\" c ON c.\"IdentityId\" = i.\"Id\" WHERE {predicate} AND i.\"DisabledOn\" IS NULL AND i.\"DeletedOn\" IS NULL";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIdentity(reader) : null;
    }

    private static BeverSqliteIdentityRecord ReadIdentity(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6));

    private static async Task WriteIdentityAuditAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string operation, string? identityId, string metadataJson, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO \"__bever_identity_audit\" (\"Id\", \"OccurredOn\", \"Operation\", \"IdentityId\", \"MetadataJson\") VALUES ($id, $occurredOn, $operation, $identityId, $metadataJson)";
        command.Parameters.AddWithValue("$id", "aud_" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$occurredOn", Now());
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$identityId", identityId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", metadataJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private PersistenceEntity GetEntity(string entityName) => entities.TryGetValue(entityName, out var entity)
        ? entity
        : throw new InvalidOperationException($"Unknown persistence entity '{entityName}'.");

    private static async Task<(string Hash, PersistenceSchema Schema)> ReadStoredSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT schema_hash, schema_json FROM {Quote(SchemaTable)} WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new MigrationException("The SQLite schema metadata row is missing.");
        }

        return (reader.GetString(0), SchemaJson.DeserializeSchema(reader.GetString(1)));
    }

    private static async Task<bool> MigrationExistsAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {Quote(MigrationsTable)} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static PersistenceSchema ApplyOperations(PersistenceSchema source, IReadOnlyList<MigrationOperation> operations)
    {
        var target = Clone(source);
        foreach (var operation in operations)
        {
            ApplyOperationToSchema(target, operation);
        }
        return target;
    }

    private static async Task ApplyOperationAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceSchema workingSchema,
        MigrationOperation operation,
        CancellationToken cancellationToken)
    {
        var existingEntity = workingSchema.Entities.FirstOrDefault(entity => entity.Name == operation.Entity);
        var previousEntity = existingEntity is null ? null : Clone(existingEntity);
        ApplyOperationToSchema(workingSchema, operation);

        switch (operation.Kind)
        {
            case "CreateEntity":
            {
                var entity = workingSchema.Entities.Single(entity => entity.Name == operation.Entity);
                await ExecuteAsync(connection, transaction, BuildCreateTableSql(entity), cancellationToken);
                await CreateIndexesAsync(connection, transaction, entity, cancellationToken);
                break;
            }
            case "DropEntity":
                await ExecuteAsync(connection, transaction, $"DROP TABLE {Quote(operation.Entity)}", cancellationToken);
                break;
            case "AddField":
            case "DropField":
            case "AlterField":
            {
                var targetEntity = workingSchema.Entities.Single(entity => entity.Name == operation.Entity);
                await RebuildTableAsync(connection, transaction, previousEntity!, targetEntity, operation, cancellationToken);
                break;
            }
        }
    }

    private static void ApplyOperationToSchema(PersistenceSchema schema, MigrationOperation operation)
    {
        switch (operation.Kind)
        {
            case "CreateEntity":
                if (operation.EntityDefinition is null || schema.Entities.Any(entity => entity.Name == operation.Entity))
                {
                    throw new MigrationException($"CreateEntity operation for '{operation.Entity}' is invalid.");
                }
                schema.Entities.Add(Clone(operation.EntityDefinition));
                break;
            case "DropEntity":
                if (schema.Entities.RemoveAll(entity => entity.Name == operation.Entity) != 1)
                {
                    throw new MigrationException($"DropEntity operation references unknown entity '{operation.Entity}'.");
                }
                break;
            case "AddField":
            {
                var entity = RequiredEntity(schema, operation.Entity);
                if (operation.Field is null || entity.Fields.Any(field => field.Name == operation.Field.Name))
                {
                    throw new MigrationException($"AddField operation for '{operation.Entity}.{operation.Field?.Name}' is invalid.");
                }
                entity.Fields.Add(Clone(operation.Field));
                break;
            }
            case "DropField":
            {
                var entity = RequiredEntity(schema, operation.Entity);
                if (operation.FieldName is null || entity.Fields.RemoveAll(field => field.Name == operation.FieldName) != 1)
                {
                    throw new MigrationException($"DropField operation for '{operation.Entity}.{operation.FieldName}' is invalid.");
                }
                break;
            }
            case "AlterField":
            {
                var entity = RequiredEntity(schema, operation.Entity);
                var index = operation.Field is null ? -1 : entity.Fields.FindIndex(field => field.Name == operation.Field.Name);
                if (index < 0)
                {
                    throw new MigrationException($"AlterField operation for '{operation.Entity}.{operation.Field?.Name}' is invalid.");
                }
                entity.Fields[index] = Clone(operation.Field!);
                break;
            }
            default:
                throw new MigrationException($"Unsupported migration operation kind '{operation.Kind}'.");
        }
    }

    private static PersistenceEntity RequiredEntity(PersistenceSchema schema, string name) =>
        schema.Entities.FirstOrDefault(entity => entity.Name == name)
        ?? throw new MigrationException($"Migration operation references unknown entity '{name}'.");

    private static async Task RebuildTableAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity previous,
        PersistenceEntity target,
        MigrationOperation operation,
        CancellationToken cancellationToken)
    {
        var temporaryName = $"__bever_temp_{target.Name}";
        await ExecuteAsync(connection, transaction, BuildCreateTableSql(target, temporaryName), cancellationToken);

        var previousFields = previous.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var expressions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        foreach (var field in target.Fields)
        {
            if (previousFields.ContainsKey(field.Name))
            {
                if (operation.Kind == "AlterField"
                    && operation.Field?.Name == field.Name
                    && !field.Nullable
                    && operation.Backfill is not null)
                {
                    var parameterName = $"$p{parameters.Count}";
                    parameters.Add((parameterName, LiteralDatabaseValue(field, operation.Backfill)));
                    expressions.Add($"COALESCE({Quote(field.Name)}, {parameterName})");
                }
                else
                {
                    expressions.Add(Quote(field.Name));
                }
            }
            else
            {
                var parameterName = $"$p{parameters.Count}";
                parameters.Add((parameterName, operation.Backfill is null ? null : LiteralDatabaseValue(field, operation.Backfill)));
                expressions.Add(parameterName);
            }
        }

        await using (var copy = connection.CreateCommand())
        {
            copy.Transaction = (SqliteTransaction)transaction;
            copy.CommandText = $"INSERT INTO {Quote(temporaryName)} ({ColumnList(target)}) SELECT {string.Join(", ", expressions)} FROM {Quote(previous.Name)}";
            foreach (var parameter in parameters)
            {
                copy.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }
            try
            {
                await copy.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception)
            {
                var parameterValues = string.Join(", ", parameters.Select(parameter => $"{parameter.Name}={parameter.Value ?? "NULL"}"));
                throw new MigrationException($"Failed to rebuild '{target.Name}' using '{copy.CommandText}' with [{parameterValues}]: {exception.Message}");
            }
        }

        await ExecuteAsync(connection, transaction, $"DROP TABLE {Quote(previous.Name)}", cancellationToken);
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {Quote(temporaryName)} RENAME TO {Quote(target.Name)}", cancellationToken);
        await CreateIndexesAsync(connection, transaction, target, cancellationToken);
    }

    private static async Task CreateIndexesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        CancellationToken cancellationToken)
    {
        foreach (var field in entity.Fields.Where(field => field.Kind == "Relationship"))
        {
            await ExecuteAsync(connection, transaction,
                BuildIndexSql(entity, field),
                cancellationToken);
        }
    }

    private static object? LiteralDatabaseValue(PersistenceField field, MigrationLiteral literal)
    {
        if (literal.Value is null)
        {
            return null;
        }

        var node = literal.Value is JsonElement element
            ? JsonNode.Parse(element.GetRawText())
            : JsonSerializer.SerializeToNode(literal.Value);
        return ToDatabaseValue(field, node);
    }

    private static PersistenceSchema Clone(PersistenceSchema value) => SchemaJson.DeserializeSchema(SchemaJson.Serialize(value));

    private static PersistenceEntity Clone(PersistenceEntity value) => new()
    {
        Name = value.Name,
        Fields = value.Fields.Select(Clone).ToList()
    };

    private static PersistenceField Clone(PersistenceField value) => new()
    {
        Name = value.Name,
        Kind = value.Kind,
        TypeName = value.TypeName,
        Nullable = value.Nullable,
        System = value.System,
        MaxLength = value.MaxLength,
        EnumValues = value.EnumValues.ToList(),
        RelationshipTarget = value.RelationshipTarget,
        RelationshipTargetKind = value.RelationshipTargetKind,
        OnDelete = value.OnDelete
    };

    private static async Task SetForeignKeysAsync(SqliteConnection connection, bool enabled, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = {(enabled ? "ON" : "OFF")}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SoftDeleteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        string id,
        string deletedOn,
        string? actorId,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add($"{entity.Name}\0{id}"))
        {
            return;
        }

        foreach (var dependentEntity in schema.Entities)
        {
            foreach (var relationship in dependentEntity.Fields.Where(field =>
                         field.Kind == "Relationship" && field.RelationshipTargetKind != "SystemIdentity" && field.RelationshipTarget == entity.Name))
            {
                var dependentIds = await GetDependentIdsAsync(
                    connection, transaction, dependentEntity, relationship, id, cancellationToken);
                if (dependentIds.Count == 0)
                {
                    continue;
                }

                switch (relationship.OnDelete ?? "Restrict")
                {
                    case "Restrict":
                        throw new BeverSqliteDeleteRestrictedException(entity.Name, id, dependentEntity.Name, relationship.Name);
                    case "SetNull":
                        await SetRelationshipNullAsync(
                            connection, transaction, dependentEntity, relationship, id, deletedOn, actorId, cancellationToken);
                        break;
                    case "Cascade":
                        foreach (var dependentId in dependentIds)
                        {
                            await SoftDeleteAsync(
                                connection, transaction, dependentEntity, dependentId, deletedOn, actorId, visited, cancellationToken);
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown relationship delete behavior '{relationship.OnDelete}'.");
                }
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = actorId is null
            ? $"UPDATE {Quote(entity.Name)} SET {Quote("DeletedOn")} = $deletedOn, {Quote("ModifiedOn")} = $deletedOn WHERE {Quote("Id")} = $id AND {Quote("DeletedOn")} IS NULL"
            : $"UPDATE {Quote(entity.Name)} SET {Quote("DeletedOn")} = $deletedOn, {Quote("DeletedBy")} = $actorId, {Quote("ModifiedOn")} = $deletedOn, {Quote("ModifiedBy")} = $actorId WHERE {Quote("Id")} = $id AND {Quote("DeletedOn")} IS NULL";
        command.Parameters.AddWithValue("$deletedOn", deletedOn);
        if (actorId is not null)
        {
            command.Parameters.AddWithValue("$actorId", actorId);
        }
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateDeleteAllowedAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        string id,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add($"{entity.Name}\0{id}"))
        {
            return;
        }

        foreach (var dependentEntity in schema.Entities)
        {
            foreach (var relationship in dependentEntity.Fields.Where(field =>
                         field.Kind == "Relationship" && field.RelationshipTargetKind != "SystemIdentity" && field.RelationshipTarget == entity.Name))
            {
                var dependentIds = await GetDependentIdsAsync(connection, transaction, dependentEntity, relationship, id, cancellationToken);
                if (dependentIds.Count == 0)
                {
                    continue;
                }

                switch (relationship.OnDelete ?? "Restrict")
                {
                    case "Restrict":
                        throw new BeverSqliteDeleteRestrictedException(entity.Name, id, dependentEntity.Name, relationship.Name);
                    case "Cascade":
                        foreach (var dependentId in dependentIds)
                        {
                            await ValidateDeleteAllowedAsync(connection, transaction, dependentEntity, dependentId, visited, cancellationToken);
                        }
                        break;
                    case "SetNull":
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown relationship delete behavior '{relationship.OnDelete}'.");
                }
            }
        }
    }

    private static async Task<bool> ActiveRecordExistsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"SELECT 1 FROM {Quote(entity.Name)} WHERE {Quote("Id")} = $id AND {Quote("DeletedOn")} IS NULL";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlyList<string>> GetDependentIdsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        PersistenceField relationship,
        string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"SELECT {Quote("Id")} FROM {Quote(entity.Name)} WHERE {Quote(relationship.Name)} = $id AND {Quote("DeletedOn")} IS NULL";
        command.Parameters.AddWithValue("$id", targetId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    private static async Task SetRelationshipNullAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PersistenceEntity entity,
        PersistenceField relationship,
        string targetId,
        string modifiedOn,
        string? actorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = actorId is null
            ? $"UPDATE {Quote(entity.Name)} SET {Quote(relationship.Name)} = NULL, {Quote("ModifiedOn")} = $modifiedOn WHERE {Quote(relationship.Name)} = $id AND {Quote("DeletedOn")} IS NULL"
            : $"UPDATE {Quote(entity.Name)} SET {Quote(relationship.Name)} = NULL, {Quote("ModifiedOn")} = $modifiedOn, {Quote("ModifiedBy")} = $actorId WHERE {Quote(relationship.Name)} = $id AND {Quote("DeletedOn")} IS NULL";
        command.Parameters.AddWithValue("$modifiedOn", modifiedOn);
        if (actorId is not null)
        {
            command.Parameters.AddWithValue("$actorId", actorId);
        }
        command.Parameters.AddWithValue("$id", targetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (createIfMissing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private bool CanOpenExistingDatabase() => File.Exists(fullPath);

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<string?> ReadSqliteMasterSqlAsync(SqliteConnection connection, string type, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = $type AND name = $name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<bool> HasApplicationTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__bever_%' LIMIT 1";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasPrivateIdentityArtifactsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name GLOB '__bever_identity*' OR name GLOB '__bever_identities*' OR name IN (" + string.Join(", ", IdentityTables.Skip(1).Select((_, index) => $"$table{index}").Concat(IdentityIndexes.Select((_, index) => $"$index{index}"))) + ") LIMIT 1";
        for (var index = 1; index < IdentityTables.Length; index++)
        {
            command.Parameters.AddWithValue($"$table{index - 1}", IdentityTables[index].Name);
        }
        for (var index = 0; index < IdentityIndexes.Length; index++)
        {
            command.Parameters.AddWithValue($"$index{index}", IdentityIndexes[index].Name);
        }
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasUnexpectedPrivateIdentityArtifactsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name GLOB '__bever_identity*' OR name GLOB '__bever_identities*'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!KnownIdentityArtifactNames.Contains(reader.GetString(0)))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> HasExpectedColumnsAsync(SqliteConnection connection, PersistenceEntity entity, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, entity.Name, cancellationToken))
        {
            return false;
        }

        var actual = new Dictionary<string, (string Type, bool NotNull, bool PrimaryKey)>(StringComparer.Ordinal);
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Quote(entity.Name)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actual[reader.GetString(1)] = (reader.GetString(2), reader.GetInt64(3) == 1, reader.GetInt64(5) == 1);
            }
        }

        if (actual.Count != entity.Fields.Count || !entity.Fields.All(field =>
            actual.TryGetValue(field.Name, out var column)
            && string.Equals(column.Type, SqliteType(field), StringComparison.OrdinalIgnoreCase)
            && column.NotNull == !field.Nullable
            && column.PrimaryKey == (field.Name == "Id")))
        {
            return false;
        }

        var actualRelationships = new HashSet<(string Field, string Target, string OnDelete)>();
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list({Quote(entity.Name)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actualRelationships.Add((reader.GetString(3), reader.GetString(2), reader.GetString(6)));
            }
        }

        var expectedRelationships = entity.Fields
            .Where(field => field.Kind == "Relationship" && field.RelationshipTargetKind != "SystemIdentity")
            .Select(field => (field.Name, field.RelationshipTarget!, SqliteDeleteBehavior(field.OnDelete)))
            .ToHashSet();
        if (!actualRelationships.SetEquals(expectedRelationships))
        {
            return false;
        }

        string tableSql;
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", entity.Name);
            tableSql = await command.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
        }
        if (entity.Fields.SelectMany(ExpectedCheckFragments).Any(fragment => !tableSql.Contains(fragment, StringComparison.Ordinal)))
        {
            return false;
        }

        var actualIndexes = new Dictionary<string, string>(StringComparer.Ordinal);
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = $table AND name NOT LIKE 'sqlite_%'";
            command.Parameters.AddWithValue("$table", entity.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actualIndexes[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        return entity.Fields.Where(field => field.Kind == "Relationship").All(field =>
        {
            var indexName = $"IX_{entity.Name}_{field.Name}";
            return actualIndexes.TryGetValue(indexName, out var sql)
                && string.Equals(sql, BuildIndexSql(entity, field), StringComparison.Ordinal);
        });
    }

    private static string BuildCreateTableSql(PersistenceEntity entity, string? tableName = null)
    {
        var definitions = entity.Fields.Select(field =>
        {
            var parts = new List<string> { Quote(field.Name), SqliteType(field) };
            if (field.Name == "Id")
            {
                parts.Add("PRIMARY KEY");
            }
            if (!field.Nullable)
            {
                parts.Add("NOT NULL");
            }
            parts.AddRange(ExpectedCheckFragments(field));
            if (field.Kind == "Relationship" && field.RelationshipTargetKind != "SystemIdentity")
            {
                parts.Add($"REFERENCES {Quote(field.RelationshipTarget!)} ({Quote("Id")}) ON DELETE {SqliteDeleteBehavior(field.OnDelete)}");
            }
            return string.Join(" ", parts);
        });
        return $"CREATE TABLE {Quote(tableName ?? entity.Name)} ({string.Join(", ", definitions)})";
    }

    private static string SqliteType(PersistenceField field) => field.TypeName switch
    {
        "Boolean" or "Int" => "INTEGER",
        _ => "TEXT"
    };

    private static string SqliteDeleteBehavior(string? behavior) => behavior switch
    {
        "Cascade" => "CASCADE",
        "SetNull" => "SET NULL",
        _ => "RESTRICT"
    };

    private static IEnumerable<string> ExpectedCheckFragments(PersistenceField field)
    {
        if (field.TypeName == "Boolean")
        {
            yield return $"CHECK ({Quote(field.Name)} IN (0, 1))";
        }
        if (field.MaxLength is not null)
        {
            yield return $"CHECK (length({Quote(field.Name)}) <= {field.MaxLength.Value})";
        }
        if (field.Kind == "Enum" && field.EnumValues.Count > 0)
        {
            yield return $"CHECK ({Quote(field.Name)} IN ({string.Join(", ", field.EnumValues.Select(SqlString))}))";
        }
    }

    private static string BuildIndexSql(PersistenceEntity entity, PersistenceField field)
    {
        var indexName = $"IX_{entity.Name}_{field.Name}";
        return $"CREATE INDEX {Quote(indexName)} ON {Quote(entity.Name)} ({Quote(field.Name)})";
    }

    private static object? ToDatabaseValue(PersistenceField field, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        if (field.TypeName == "Boolean")
        {
            return value.GetValue<bool>() ? 1 : 0;
        }
        if (field.TypeName == "Int")
        {
            return value.GetValue<int>();
        }
        if (field.TypeName is "Decimal" or "Money")
        {
            return value.GetValue<decimal>().ToString(CultureInfo.InvariantCulture);
        }

        return value.GetValue<string>();
    }

    private static JsonObject ReadRecord(SqliteDataReader reader, PersistenceEntity entity)
    {
        var record = new JsonObject();
        for (var index = 0; index < entity.Fields.Count; index++)
        {
            var field = entity.Fields[index];
            var name = JsonName(field.Name);
            if (reader.IsDBNull(index))
            {
                record[name] = null;
            }
            else if (field.TypeName == "Boolean")
            {
                record[name] = reader.GetInt64(index) != 0;
            }
            else if (field.TypeName == "Int")
            {
                record[name] = reader.GetInt32(index);
            }
            else if (field.TypeName is "Decimal" or "Money")
            {
                record[name] = decimal.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
            }
            else
            {
                record[name] = reader.GetString(index);
            }
        }

        return record;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ColumnList(PersistenceEntity entity) => string.Join(", ", entity.Fields.Select(field => Quote(field.Name)));

    private static string JsonName(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string SqlString(string value) => $"'{value.Replace("'", "''")}'";
}

public enum BeverSqliteSchemaStatus
{
    Uninitialized,
    Current,
    SchemaMismatch,
    SchemaDrift
}

public enum BeverSqliteIdentitySchemaStatus
{
    Uninitialized,
    Current,
    SchemaMismatch,
    SchemaDrift
}

public sealed record BeverSqliteIdentityRecord(
    string Id,
    string Email,
    string NormalizedEmail,
    string Label,
    string PasswordHash,
    string SecurityStamp,
    long SessionVersion);

public sealed record BeverSqliteDirectGrant(string Id, string IdentityId, string Entity, string Action, string? RecordId);

public sealed record BeverSqliteIdentityAuditEntry(string Operation, string? IdentityId, string MetadataJson);

internal sealed record PasswordHashSubject(string Id, string NormalizedEmail);

public sealed class BeverSqliteDeleteRestrictedException : Exception
{
    public BeverSqliteDeleteRestrictedException(string entity, string id, string dependentEntity, string relationship)
        : base($"Cannot delete '{entity}' record '{id}' because active '{dependentEntity}' records reference it through '{relationship}'.")
    {
        DependentEntity = dependentEntity;
        Relationship = relationship;
    }

    /// <summary>The entity whose records still reference the deleted record.</summary>
    public string DependentEntity { get; }

    /// <summary>The referencing field on <see cref="DependentEntity"/>.</summary>
    public string Relationship { get; }
}

using Erp.Core.Domain.Metadata;

namespace Erp.Core.Infrastructure.Metadata;

public sealed record MetadataDdlPlan(
    IReadOnlyList<string> TransactionalStatements,
    IReadOnlyList<string> ConcurrentIndexStatements);

/// <summary>
/// Generates lock-safe PostgreSQL DDL (<c>ADD COLUMN ... NULL</c>, <c>CREATE INDEX CONCURRENTLY</c>),
/// Row-Level Security policies, and append-only <c>&lt;table&gt;_audit</c> triggers from an <see cref="ObjectDefinition"/>.
/// </summary>
public static class MetadataDdlGenerator
{
    public static MetadataDdlPlan GenerateForObject(ObjectDefinition objectDef)
    {
        var (schema, table) = objectDef.GetSchemaAndTable();
        var auditTable = objectDef.GetAuditTableName();

        var transactional = new List<string>
        {
            $"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            GRANT USAGE ON SCHEMA "{schema}" TO erp_app;
            """,
            $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."{table}" (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL,
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL,
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL
            );
            """
        };

        var concurrentIndexes = new List<string>
        {
            $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_{schema}_{table}_tenant_id" ON "{schema}"."{table}" (tenant_id);""",
            $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_{schema}_{auditTable}_tenant_record" ON "{schema}"."{auditTable}" (tenant_id, record_id, audit_id);"""
        };

        foreach (var field in objectDef.Fields)
        {
            var colName = field.GetColumnName(objectDef.OwnerApp);
            if (field.Type == FieldType.Money)
            {
                transactional.Add(
                    $"""
                    ALTER TABLE "{schema}"."{table}" ADD COLUMN IF NOT EXISTS "{colName}_amount" numeric(18, 4) NULL;
                    ALTER TABLE "{schema}"."{table}" ADD COLUMN IF NOT EXISTS "{colName}_currency" character varying(3) NULL;
                    """);
            }
            else
            {
                var sqlType = GetSqlColumnType(field);
                transactional.Add(
                    $"""ALTER TABLE "{schema}"."{table}" ADD COLUMN IF NOT EXISTS "{colName}" {sqlType} NULL;""");
            }

            if (field.Type == FieldType.Reference && !string.IsNullOrWhiteSpace(field.TargetObject))
            {
                var (targetSchema, targetTable) = IdentifierNaming.SplitObjectId(field.TargetObject);
                var fkName = $"fk_{schema}_{table}_{colName}";
                var onDeleteClause = field.OnDelete switch
                {
                    RelationDeleteBehavior.Cascade => "ON DELETE CASCADE",
                    RelationDeleteBehavior.SetNull => "ON DELETE SET NULL",
                    _ => "ON DELETE RESTRICT"
                };

                transactional.Add(
                    $"""
                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint WHERE conname = '{fkName}'
                        ) THEN
                            ALTER TABLE "{schema}"."{table}"
                                ADD CONSTRAINT "{fkName}"
                                FOREIGN KEY ("{colName}")
                                REFERENCES "{targetSchema}"."{targetTable}" (id)
                                {onDeleteClause}
                                NOT VALID;
                            ALTER TABLE "{schema}"."{table}" VALIDATE CONSTRAINT "{fkName}";
                        END IF;
                    END $$;
                    """);

                concurrentIndexes.Add(
                    $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_{schema}_{table}_{colName}" ON "{schema}"."{table}" (tenant_id, "{colName}");""");
            }
        }

        foreach (var key in objectDef.Keys)
        {
            if (key.Fields.Count == 0)
            {
                continue;
            }

            var keyName = IdentifierNaming.ToSnakeCase(key.Name);
            var keyCols = new List<string> { "tenant_id" };
            if (key.ScopeToCompany && objectDef.IsCompanyScoped)
            {
                keyCols.Add("company_id");
            }

            foreach (var keyFieldName in key.Fields)
            {
                var matchedField = objectDef.Fields.FirstOrDefault(
                    f => string.Equals(f.Name, keyFieldName, StringComparison.OrdinalIgnoreCase));
                var col = matchedField?.GetColumnName(objectDef.OwnerApp)
                    ?? IdentifierNaming.ToSnakeCase(keyFieldName);
                keyCols.Add($"\"{col}\"");
            }

            var colsJoined = string.Join(", ", keyCols);
            concurrentIndexes.Add(
                $"""CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "ux_{schema}_{table}_{keyName}" ON "{schema}"."{table}" ({colsJoined});""");
        }

        // Enable RLS on main table and create append-only shadow audit table with triggers
        transactional.Add(
            $"""
            ALTER TABLE "{schema}"."{table}" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "{schema}"."{table}" FORCE ROW LEVEL SECURITY;

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policies
                     WHERE schemaname = '{schema}'
                       AND tablename = '{table}'
                       AND policyname = 'tenant_isolation'
                ) THEN
                    CREATE POLICY tenant_isolation ON "{schema}"."{table}"
                        USING (tenant_id = current_setting('app.tenant_id')::uuid)
                        WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
                END IF;
            END $$;

            GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema}"."{table}" TO erp_app;

            CREATE TABLE IF NOT EXISTS "{schema}"."{auditTable}" (
                audit_id bigserial PRIMARY KEY,
                record_id uuid NOT NULL,
                tenant_id uuid NOT NULL,
                operation character varying(10) NOT NULL,
                old_data jsonb NULL,
                new_data jsonb NULL,
                changed_by uuid NOT NULL,
                changed_at timestamptz NOT NULL DEFAULT now(),
                reason text NULL
            );

            ALTER TABLE "{schema}"."{auditTable}" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "{schema}"."{auditTable}" FORCE ROW LEVEL SECURITY;

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_policies
                     WHERE schemaname = '{schema}'
                       AND tablename = '{auditTable}'
                       AND policyname = 'tenant_isolation'
                ) THEN
                    CREATE POLICY tenant_isolation ON "{schema}"."{auditTable}"
                        USING (tenant_id = current_setting('app.tenant_id')::uuid)
                        WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
                END IF;
            END $$;

            REVOKE ALL ON "{schema}"."{auditTable}" FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT ON "{schema}"."{auditTable}" TO erp_app;
            GRANT USAGE, SELECT ON SEQUENCE "{schema}"."{auditTable}_audit_id_seq" TO erp_app;

            DROP TRIGGER IF EXISTS "trg_{table}_audit" ON "{schema}"."{table}";
            CREATE TRIGGER "trg_{table}_audit"
                AFTER INSERT OR UPDATE OR DELETE ON "{schema}"."{table}"
                FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();

            DROP TRIGGER IF EXISTS "trg_{table}_audit_immutable" ON "{schema}"."{auditTable}";
            CREATE TRIGGER "trg_{table}_audit_immutable"
                BEFORE UPDATE OR DELETE OR TRUNCATE ON "{schema}"."{auditTable}"
                FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();
            """);

        return new MetadataDdlPlan(transactional, concurrentIndexes);
    }

    public static string GetSqlColumnType(FieldDefinition field) => field.Type switch
    {
        FieldType.String => $"character varying({Math.Clamp(field.MaxLength ?? 255, 1, 4000)})",
        FieldType.Text => "text",
        FieldType.Integer => "bigint",
        FieldType.Decimal => $"numeric(18, {Math.Clamp(field.Scale ?? 2, 0, 10)})",
        FieldType.Boolean => "boolean",
        FieldType.Date => "date",
        FieldType.DateTime => "timestamptz",
        FieldType.Uuid => "uuid",
        FieldType.Reference => "uuid",
        FieldType.Enum => "character varying(100)",
        FieldType.Json => "jsonb",
        FieldType.Blob => "uuid",
        FieldType.Money => throw new InvalidOperationException("Money fields map to two physical columns (_amount and _currency)."),
        _ => throw new ArgumentOutOfRangeException(nameof(field), $"Unsupported field type {field.Type}.")
    };
}


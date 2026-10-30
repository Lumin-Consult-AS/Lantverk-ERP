using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Metadata;
using Xunit;

namespace Erp.Core.Tests;

public sealed class MetadataAndFilterUnitTests
{
    [Fact]
    public void YamlMetadataParser_ParsesSingleObjectAndLockSafeDdlIsGenerated()
    {
        const string yaml = """
            id: demo.contactnote
            ownerApp: demo.contacts
            label: Kontaktnotat
            keys:
              - name: party_followup
                fields: [party, followUp]
            fields:
              party:    { type: reference, to: core.party, required: true, onDelete: cascade }
              text:     { type: text, required: true }
              followUp: { type: date }
              budget:   { type: money }
            """;

        var bundle = YamlMetadataParser.Parse(yaml);
        var obj = Assert.Single(bundle.Objects);
        Assert.Equal("demo.contactnote", obj.Id);
        Assert.Equal("demo.contacts", obj.OwnerApp);
        Assert.Equal(4, obj.Fields.Count);

        var ddl = MetadataDdlGenerator.GenerateForObject(obj);

        // Verify lock-safe ADD COLUMN ... NULL statements
        Assert.Contains(ddl.TransactionalStatements, s => s.Contains("ADD COLUMN IF NOT EXISTS \"party\" uuid NULL"));
        Assert.Contains(ddl.TransactionalStatements, s => s.Contains("ADD COLUMN IF NOT EXISTS \"text\" text NULL"));
        Assert.Contains(ddl.TransactionalStatements, s => s.Contains("ADD COLUMN IF NOT EXISTS \"follow_up\" date NULL"));
        Assert.Contains(ddl.TransactionalStatements, s => s.Contains("ADD COLUMN IF NOT EXISTS \"budget_amount\" numeric(18, 4) NULL"));
        Assert.Contains(ddl.TransactionalStatements, s => s.Contains("ADD COLUMN IF NOT EXISTS \"budget_currency\" character varying(3) NULL"));

        // Verify RLS and append-only audit table + triggers
        Assert.Contains(ddl.TransactionalStatements, s =>
            s.Contains("CREATE TABLE IF NOT EXISTS \"demo\".\"contactnote_audit\"")
            && s.Contains("REVOKE ALL ON \"demo\".\"contactnote_audit\" FROM PUBLIC, erp_app;")
            && s.Contains("GRANT SELECT, INSERT ON \"demo\".\"contactnote_audit\" TO erp_app;")
            && s.Contains("EXECUTE FUNCTION core.capture_audit_row()")
            && s.Contains("EXECUTE FUNCTION core.prevent_audit_modification()"));

        // Verify concurrent index generation
        Assert.All(ddl.ConcurrentIndexStatements, s => Assert.Contains("CONCURRENTLY", s));
    }

    [Fact]
    public void FilterExpressionParser_ParsesComplexAndOrInContainsExpressions()
    {
        var node = FilterExpressionParser.Parse(
            "(status eq 'posted' or status eq 'approved') and amount gt 100.50 and name contains 'Trevare' and followUp isEmpty and channel in ('email', 'phone')");

        var rootAnd = Assert.IsType<FilterGroup>(node);
        Assert.Equal(LogicalOperator.And, rootAnd.Logical);
        Assert.Equal(5, rootAnd.Clauses.Count);

        var firstOr = Assert.IsType<FilterGroup>(rootAnd.Clauses[0]);
        Assert.Equal(LogicalOperator.Or, firstOr.Logical);
        Assert.Equal(2, firstOr.Clauses.Count);

        var inClause = Assert.IsType<FilterCondition>(rootAnd.Clauses[4]);
        Assert.Equal("channel", inClause.Field);
        Assert.Equal(FilterOperator.In, inClause.Operator);
    }
}


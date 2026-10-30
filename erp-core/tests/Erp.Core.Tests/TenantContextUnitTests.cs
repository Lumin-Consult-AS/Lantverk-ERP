using Erp.Core.Domain.Tenancy;
using Xunit;

namespace Erp.Core.Tests;

public sealed class TenantContextUnitTests
{
    [Fact]
    public void Set_WithEmptyGuid_ThrowsArgumentException()
    {
        var context = new TenantContext();
        Assert.Throws<ArgumentException>(() => context.Set(Guid.Empty));
    }

    [Fact]
    public void BeginScope_RestoresPreviousContextOnDispose()
    {
        var context = new TenantContext();
        var tenantA = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userB = Guid.NewGuid();

        context.Set(tenantA, userA, roles: ["role.a"]);
        Assert.Equal(tenantA, context.TenantId);
        Assert.True(context.IsAuthenticated);

        using (context.BeginScope(tenantB, userB, roles: ["role.b"]))
        {
            Assert.Equal(tenantB, context.TenantId);
            Assert.Equal(userB, context.UserId);
            Assert.Equal(["role.b"], context.Roles);
        }

        Assert.Equal(tenantA, context.TenantId);
        Assert.Equal(userA, context.UserId);
        Assert.Equal(["role.a"], context.Roles);
    }
}


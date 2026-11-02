using System.Reflection;

namespace Lims.UnitTests;

/// <summary>Module boundaries from design section 3: modules talk through Contracts, not each other.</summary>
public class ArchitectureTests
{
    private static IEnumerable<string> Refs(string assembly) =>
        Assembly.Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Lims.", StringComparison.Ordinal));

    [Theory]
    [InlineData("Lims.Instruments", new[] { "Lims.Clinical", "Lims.Hmis", "Lims.Host" })]
    [InlineData("Lims.Clinical", new[] { "Lims.Instruments", "Lims.Hmis", "Lims.Host" })]
    [InlineData("Lims.Hmis", new[] { "Lims.Instruments", "Lims.Clinical", "Lims.Host" })]
    [InlineData("Lims.Messaging", new[] { "Lims.Instruments", "Lims.Clinical", "Lims.Hmis", "Lims.Host" })]
    [InlineData("Lims.Audit", new[] { "Lims.Messaging", "Lims.Instruments", "Lims.Clinical", "Lims.Hmis", "Lims.Host" })]
    [InlineData("Lims.Contracts", new[] { "Lims.Platform", "Lims.Messaging", "Lims.Instruments", "Lims.Clinical", "Lims.Hmis", "Lims.Host" })]
    public void Module_does_not_reference_forbidden_modules(string module, string[] forbidden)
    {
        var refs = Refs(module).ToList();
        Assert.Empty(refs.Intersect(forbidden));
    }

    [Fact]
    public void Simulators_do_not_share_middleware_link_code()
    {
        Assert.DoesNotContain("Lims.Instruments", Refs("Lims.Sim.E411"));
    }
}

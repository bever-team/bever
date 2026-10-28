namespace Bever.AspNetCore.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void BeverAspNetCoreDoesNotReferenceBeverCore()
    {
        var referencedAssemblies = typeof(BeverRuntime).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, assembly => assembly.Name == "Bever.Core");
    }
}

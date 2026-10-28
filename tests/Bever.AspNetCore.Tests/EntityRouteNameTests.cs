namespace Bever.AspNetCore.Tests;

public sealed class EntityRouteNameTests
{
    [Theory]
    [InlineData("Product", "products")]
    [InlineData("SalesOrder", "sales-orders")]
    [InlineData("CustomerAddress", "customer-addresses")]
    [InlineData("Company", "companies")]
    public void ConvertsEntityNameToPluralKebabCaseRoute(string entityName, string expected)
    {
        Assert.Equal(expected, EntityRouteName.FromEntityName(entityName));
    }
}

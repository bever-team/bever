using Bever.Model;

namespace Bever.AspNetCore.Tests;

public sealed class BeverRuntimeTests
{
    [Fact]
    public void ResolvesEntitiesByPluralKebabCaseRouteName()
    {
        var runtime = new BeverRuntime(new BeverModel
        {
            BeverLanguageVersion = "0.6",
            SemanticModelVersion = "0.10",
            Entities = [new EntityModel { Name = "SalesOrder" }]
        });

        Assert.True(runtime.TryGetEntity("sales-orders", out var entity));
        Assert.Equal("SalesOrder", entity.Model.Name);
    }

    [Fact]
    public void RejectsRouteNameCollisions()
    {
        var model = new BeverModel
        {
            BeverLanguageVersion = "0.6",
            SemanticModelVersion = "0.10",
            Entities =
            [
                new EntityModel { Name = "Product" },
                new EntityModel { Name = "product" }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
    }

    [Fact]
    public void BuildsActionsFromSemanticModel()
    {
        var runtime = new BeverRuntime(new BeverModel
        {
            BeverLanguageVersion = "0.6",
            SemanticModelVersion = "0.10",
            Entities = [new EntityModel { Name = "Product" }],
            Actions = [new ActionModel { Verb = "Read", Entity = "Product" }]
        });

        var action = Assert.Single(runtime.Actions);
        Assert.Equal(BeverEntityAction.Read, action.Action);
        Assert.Equal("products", action.Entity.RouteName);
    }

    [Fact]
    public void RejectsUnknownActionVerb()
    {
        var model = CreateModel(new ActionModel { Verb = "Publish", Entity = "Product" });

        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("0.8")]
    public void RejectsOtherSemanticModelVersions(string version)
    {
        var model = CreateModel();
        model.SemanticModelVersion = version;

        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
    }

    [Fact]
    public void RejectsActionForUnknownEntity()
    {
        var model = CreateModel(new ActionModel { Verb = "Read", Entity = "Order" });

        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
    }

    [Fact]
    public void RejectsDuplicateActions()
    {
        var model = CreateModel(
            new ActionModel { Verb = "Read", Entity = "Product" },
            new ActionModel { Verb = "read", Entity = "Product" });

        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
    }

    private static BeverModel CreateModel(params ActionModel[] actions) => new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Entities = [new EntityModel { Name = "Product" }],
        Actions = actions.ToList()
    };
}

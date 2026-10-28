using System.Text.Json.Nodes;

using Bever.Model;

namespace Bever.AspNetCore.Tests;

public sealed class InMemoryBeverDatabaseProviderTests
{
    private static readonly BeverEntityContext Entity = new(new EntityModel { Name = "Product" }, "products");

    [Fact]
    public async Task CreateGeneratesServerIdInsteadOfUsingClientId()
    {
        var provider = new InMemoryBeverDatabaseProvider();
        var values = new JsonObject
        {
            ["id"] = "client-id",
            ["name"] = "Widget"
        };

        var record = await provider.CreateAsync(Entity, values, CancellationToken.None);

        Assert.NotEqual("client-id", record["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeleteSoftDeletesRecordAndHidesItFromReads()
    {
        var provider = new InMemoryBeverDatabaseProvider();
        var record = await provider.CreateAsync(Entity, new JsonObject { ["name"] = "Widget" }, CancellationToken.None);
        var id = Assert.IsType<string>(record["id"]?.GetValue<string>());

        var deleted = await provider.DeleteAsync(Entity, id, null, CancellationToken.None);
        var fetched = await provider.GetAsync(Entity, id, CancellationToken.None);
        var listed = await provider.ListAsync(Entity, CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(fetched);
        Assert.Empty(listed);
    }

    [Fact]
    public async Task DeleteReturnsFalseForAlreadyDeletedRecord()
    {
        var provider = new InMemoryBeverDatabaseProvider();
        var record = await provider.CreateAsync(Entity, new JsonObject { ["name"] = "Widget" }, CancellationToken.None);
        var id = Assert.IsType<string>(record["id"]?.GetValue<string>());

        await provider.DeleteAsync(Entity, id, null, CancellationToken.None);
        var deletedAgain = await provider.DeleteAsync(Entity, id, null, CancellationToken.None);

        Assert.False(deletedAgain);
    }

    [Fact]
    public async Task ReturnedRecordsAreCloned()
    {
        var provider = new InMemoryBeverDatabaseProvider();
        var created = await provider.CreateAsync(Entity, new JsonObject { ["name"] = "Widget" }, CancellationToken.None);
        var id = Assert.IsType<string>(created["id"]?.GetValue<string>());

        created["name"] = "Mutated Outside Provider";
        var fetched = await provider.GetAsync(Entity, id, CancellationToken.None);

        Assert.Equal("Widget", fetched?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task PatchReplacesRecordWithoutMutatingReturnedClones()
    {
        var provider = new InMemoryBeverDatabaseProvider();
        var created = await provider.CreateAsync(Entity, new JsonObject { ["name"] = "Widget" }, CancellationToken.None);
        var id = Assert.IsType<string>(created["id"]?.GetValue<string>());

        var patched = await provider.PatchAsync(Entity, id, new JsonObject { ["name"] = "Updated" }, CancellationToken.None);
        patched!["name"] = "Mutated Outside Provider";
        var fetched = await provider.GetAsync(Entity, id, CancellationToken.None);

        Assert.Equal("Updated", fetched?["name"]?.GetValue<string>());
    }
}

namespace Bever.AspNetCore;

/// <summary>Where Bever finds its model and data. Bind from the "Bever" configuration section or set in AddBever.</summary>
public sealed class BeverOptions
{
    /// <summary>The compiled semantic model, relative to the content root.</summary>
    public string ModelPath { get; set; } = "../bev/.output/model.json";

    /// <summary>The URL prefix of the API routes.</summary>
    public string ApiPrefix { get; set; } = "/api/v1";

    /// <summary>The SQLite database file, relative to the content root.</summary>
    public string DatabasePath { get; set; } = "../.bever/bever.db";
}

using Bever.Model;

namespace Bever.AspNetCore;

public sealed class BeverRuntime
{
    private readonly Dictionary<string, BeverEntityContext> entitiesByRoute;

    public BeverRuntime(BeverModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        BeverModelValidator.Validate(model, "<in-memory Bever model>");
        Model = model;
        entitiesByRoute = BuildEntityRouteMap(model);
        Actions = BuildActions(model, entitiesByRoute);
    }

    public BeverModel Model { get; }

    public IReadOnlyCollection<BeverEntityContext> Entities => entitiesByRoute.Values;

    public IReadOnlyList<BeverActionContext> Actions { get; }

    public bool TryGetEntity(string routeName, out BeverEntityContext entity) =>
        entitiesByRoute.TryGetValue(routeName, out entity!);

    private static Dictionary<string, BeverEntityContext> BuildEntityRouteMap(BeverModel model)
    {
        var entities = new Dictionary<string, BeverEntityContext>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in model.Entities)
        {
            var routeName = EntityRouteName.FromEntityName(entity.Name);
            if (!entities.TryAdd(routeName, new BeverEntityContext(entity, routeName)))
            {
                throw new InvalidOperationException($"Multiple Bever entities resolve to the same route name '{routeName}'.");
            }
        }

        return entities;
    }

    private static IReadOnlyList<BeverActionContext> BuildActions(BeverModel model, Dictionary<string, BeverEntityContext> entitiesByRoute)
    {
        var actions = new List<BeverActionContext>();
        var actionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in model.Actions)
        {
            if (!Enum.TryParse<BeverEntityAction>(action.Verb, ignoreCase: true, out var parsedAction))
            {
                throw new InvalidOperationException($"Unsupported Bever action verb '{action.Verb}'.");
            }

            if (string.IsNullOrWhiteSpace(action.Entity))
            {
                throw new InvalidOperationException($"Bever action '{action.Verb}' does not declare an entity.");
            }

            var routeName = EntityRouteName.FromEntityName(action.Entity);
            if (!entitiesByRoute.TryGetValue(routeName, out var entity) || !string.Equals(entity.Model.Name, action.Entity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Bever action '{action.Verb} {action.Entity}' references an unknown entity.");
            }

            var actionKey = $"{parsedAction}:{entity.RouteName}:{action.Name}";
            if (!actionKeys.Add(actionKey))
            {
                throw new InvalidOperationException($"Duplicate Bever action '{parsedAction} {action.Entity}{(action.Name is null ? "" : $" As {action.Name}")}'.");
            }

            actions.Add(new BeverActionContext(action, parsedAction, entity));
        }

        return actions;
    }
}

public sealed record BeverEntityContext(EntityModel Model, string RouteName);

public sealed record BeverActionContext(ActionModel Model, BeverEntityAction Action, BeverEntityContext Entity);

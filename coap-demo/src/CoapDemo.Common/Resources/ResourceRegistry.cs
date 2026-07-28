namespace CoapDemo.Common.Resources;

/// <summary>Holds every routable resource and answers path lookups for the request handler and discovery.</summary>
public sealed class ResourceRegistry
{
    private readonly Dictionary<string, ICoapResource> _byPath;

    public ResourceRegistry(IEnumerable<ICoapResource> resources)
    {
        _byPath = resources.ToDictionary(r => r.Path);
    }

    public IReadOnlyCollection<ICoapResource> All => _byPath.Values;

    public bool TryGet(string? path, out ICoapResource resource)
    {
        resource = null!;
        return path is not null && _byPath.TryGetValue(path, out resource!);
    }
}

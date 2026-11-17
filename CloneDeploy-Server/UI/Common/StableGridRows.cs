namespace CloneDeploy_Server.UI.Common;

/// <summary>Retains the row objects used as grid render keys while replacing their displayed data.</summary>
public sealed class StableGridRows<T, TKey>(Func<T, TKey> key) where TKey : notnull
{
    private Dictionary<TKey, StableGridRow<T>> rows = [];
    public IReadOnlyList<StableGridRow<T>> Items { get; private set; } = [];

    public void Update(IEnumerable<T> values)
    {
        var next = new Dictionary<TKey, StableGridRow<T>>();
        var items = new List<StableGridRow<T>>();
        foreach (var value in values)
        {
            var id = key(value);
            var row = rows.GetValueOrDefault(id) ?? new StableGridRow<T>(value);
            row.Value = value;
            next.Add(id, row);
            items.Add(row);
        }
        rows = next;
        Items = items;
    }
}

public sealed class StableGridRow<T>(T value)
{
    public T Value { get; internal set; } = value;
}

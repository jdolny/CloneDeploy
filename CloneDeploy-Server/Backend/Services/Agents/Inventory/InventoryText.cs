using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.Backend.Services;

internal static class InventoryText
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static void RemoveNullCharacters(AgentInventory inventory) => Clean(inventory);

    private static void Clean(object? value)
    {
        if (value is null) return;
        if (value is IList list)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] is string text)
                    list[i] = text.Replace("\0", "", StringComparison.Ordinal);
                else
                    Clean(list[i]);
            return;
        }
        var type = value.GetType();
        if (type.IsValueType || type == typeof(string) || type.Assembly != typeof(AgentInventory).Assembly) return;
        foreach (var property in Properties.GetOrAdd(type, t => t.GetProperties()
                     .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray()))
        {
            var child = property.GetValue(value);
            if (child is string text)
            {
                if (text.Contains('\0')) property.SetValue(value, text.Replace("\0", "", StringComparison.Ordinal));
            }
            else Clean(child);
        }
    }
}

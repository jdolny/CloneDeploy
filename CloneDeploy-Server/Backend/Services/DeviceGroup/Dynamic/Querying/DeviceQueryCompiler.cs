using System.Globalization;
using System.Linq.Expressions;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Turns saved group rules into a search for matching devices.
/// </summary>
public static class DeviceQueryCompiler
{
    public static Expression<Func<Device, bool>> Compile(DeviceQueryDefinitionDto definition,
        CloneDeployDbContext db, IReadOnlyList<DirectoryObject> directory)
    {
        if (definition.Version != 1 || definition.Root is null || definition.Root.Scope != "" || definition.Root.Field != null)
            throw new InvalidOperationException("Unsupported query version or root.");
        var count = 0;
        var device = Expression.Parameter(typeof(Device), "device");
        var result = Build(definition.Root, device, "", 0);
        return Expression.Lambda<Func<Device, bool>>(result, device);

        Expression Build(DeviceQueryNodeDto node, Expression current, string scope, int depth)
        {
            if (node is null || ++count > 100 || depth > 6 || node.Children is null || !System.Enum.IsDefined(node.Match))
                throw new InvalidOperationException("Use at most 100 conditions and six nesting levels.");
            Expression body;
            if (node.Scope == "DeviceGroup")
            {
                if (scope != "" || node.Field != null || node.Children.Count != 0 ||
                    !int.TryParse(node.Value, out var groupId) || groupId <= 0)
                    throw new InvalidOperationException("Select an available device group at device level.");
                var ids = DeviceGroupMembershipQuery.DeviceIds(db, [groupId]);
                Expression<Func<Device, bool>> predicate = d => ids.Contains(d.Id);
                body = new ReplaceParameter(predicate.Parameters[0], current).Visit(predicate.Body)!;
            }
            else if (node.Scope is "DirectoryGroup" or "DirectoryOu")
            {
                if (scope != "" || node.Field != null || node.Children.Count != 0) throw new InvalidOperationException("Directory conditions belong at device level.");
                var target = directory.SingleOrDefault(x => x.DirectorySourceId == node.DirectorySourceId && x.ObjectGuid == node.ObjectGuid && !x.IsMissing);
                var kind = node.Scope == "DirectoryGroup" ? DirectoryObjectKind.Group : DirectoryObjectKind.OrganizationalUnit;
                if (target is null || target.Kind != kind) throw new InvalidOperationException("Select an available directory group or OU. A missing target cannot be evaluated.");
                var key = target.DirectorySourceId;
                var guid = target.ObjectGuid;
                Expression<Func<Device, bool>> predicate;
                if (kind == DirectoryObjectKind.Group)
                    predicate = d => d.DirectorySourceId == key && db.DirectoryObjects.Any(o => o.DirectorySourceId == key && !o.IsMissing && o.Kind == DirectoryObjectKind.Computer &&
                                                                                                o.ObjectGuid == d.DirectoryObjectGuid &&
                                                                                                db.DirectoryEffectiveGroupMemberships.Any(m => m.DirectorySourceId == key && m.GroupGuid == guid && m.MemberGuid == o.ObjectGuid));
                else
                {
                    var parents = new HashSet<Guid> { guid };
                    if (node.IncludeDescendants)
                    {
                        var pending = new Queue<Guid>();
                        pending.Enqueue(guid);
                        var children = directory.Where(x => x.DirectorySourceId == key && !x.IsMissing && x.ParentGuid != null)
                            .ToLookup(x => x.ParentGuid!.Value);
                        while (pending.TryDequeue(out var parent))
                            foreach (var child in children[parent])
                                if (child.Kind is DirectoryObjectKind.OrganizationalUnit or DirectoryObjectKind.Container && parents.Add(child.ObjectGuid))
                                    pending.Enqueue(child.ObjectGuid);
                    }

                    var ids = parents.ToArray();
                    predicate = d => d.DirectorySourceId == key && db.DirectoryObjects.Any(o => o.DirectorySourceId == key && !o.IsMissing && o.Kind == DirectoryObjectKind.Computer &&
                                                                                                o.ParentGuid != null && ids.Contains(o.ParentGuid.Value) &&
                                                                                                o.ObjectGuid == d.DirectoryObjectGuid);
                }

                body = new ReplaceParameter(predicate.Parameters[0], current).Visit(predicate.Body)!;
            }
            else if (node.Scope != "")
            {
                if (scope != "" || node.Field != null) throw new InvalidOperationException("Inventory categories belong at device level.");
                var category = DeviceQueryCatalog.Get(node.Scope);
                var navigation = Expression.Property(current, category.Navigation!);
                if (category.Collection)
                {
                    var elementType = node.Scope switch { "Software" => typeof(DeviceSoftware), "Certificates" => typeof(DeviceCertificate), "Tags" => typeof(DeviceTag), _ => category.Type };
                    var element = Expression.Parameter(elementType, "entry");
                    var innerNavigation = node.Scope switch { "Software" => "Software", "Certificates" => "Certificate", "Tags" => "Tag", _ => null };
                    Expression record = innerNavigation != null ? Expression.Property(element, innerNavigation) : element;
                    var criteria = Group(node, record, node.Scope, depth);
                    if (innerNavigation != null) criteria = Expression.AndAlso(Expression.NotEqual(record, Expression.Constant(null, record.Type)), criteria);
                    body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [elementType], navigation, Expression.Lambda(criteria, element));
                }
                else body = Expression.AndAlso(Expression.NotEqual(navigation, Expression.Constant(null, navigation.Type)), Group(node, navigation, node.Scope, depth));
            }
            else if (node.Field is { } field)
            {
                if (node.Children.Count != 0) throw new InvalidOperationException("A condition cannot contain child conditions.");
                body = Rule(node, current, scope, field);
            }
            else body = Group(node, current, scope, depth);

            return node.Negate ? Expression.Not(body) : body;
        }

        Expression Group(DeviceQueryNodeDto node, Expression current, string scope, int depth)
        {
            if (node.Children.Count == 0) throw new InvalidOperationException("Add at least one condition to each match group.");
            var expressions = node.Children.Select(x => Build(x, current, scope, depth + 1));
            return expressions.Aggregate((left, right) => node.Match == QueryMatch.All ? Expression.AndAlso(left, right) : Expression.OrElse(left, right));
        }
    }

    private static Expression Rule(DeviceQueryNodeDto node, Expression current, string scope, string field)
    {
        var property = DeviceQueryCatalog.Property(scope, field);
        if (!DeviceQueryCatalog.Operators(scope, field).Contains(node.Operator)) throw new InvalidOperationException("This operator is not valid for the selected field.");
        var value = node.Value ?? "";
        if (scope == "" && field == "AgentPlatform" && value is not (CloneDeploy_Agent.Contracts.AgentPlatform.Windows or CloneDeploy_Agent.Contracts.AgentPlatform.MacOS))
            throw new InvalidOperationException("Select Windows or macOS for Platform.");
        if (value.Length > 1000) throw new InvalidOperationException("Condition values must be at most 1000 characters.");
        if (scope == "" && field is "IsAdSync" or "AdDisabled")
        {
            if (!bool.TryParse(value, out var expected))
                throw new InvalidOperationException($"Enter a valid Boolean for {DeviceQueryCatalog.Label(field)}.");
            Expression<Func<Device, bool>> predicate = field == "IsAdSync"
                ? d => d.DirectoryObject != null && d.DirectoryObject.DirectorySource.IsEnabled && !d.DirectoryObject.DirectorySource.ApplicationAccessOnly
                : d => d.DirectoryObject != null && d.DirectoryObject.DirectorySource.IsEnabled && !d.DirectoryObject.DirectorySource.ApplicationAccessOnly && d.DirectoryObject.Disabled;
            var linkedValue = new ReplaceParameter(predicate.Parameters[0], current).Visit(predicate.Body)!;
            return Expression.Equal(linkedValue, Capture(expected));
        }

        Expression member = current;
        Expression pathExists = Expression.Constant(true);
        var parts = field.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            member = Expression.Property(member, parts[i]);
            if (i < parts.Length - 1)
                pathExists = Expression.AndAlso(pathExists, Expression.NotEqual(member, Expression.Constant(null, member.Type)));
        }

        var underlying = Nullable.GetUnderlyingType(member.Type);
        var type = underlying ?? member.Type;
        if (type == typeof(string) && node.Operator != QueryOperator.IsEmpty && string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Enter a text value, or use Is empty to match missing/empty values.");
        var nullable = !type.IsValueType || underlying != null;
        var isNull = nullable ? (Expression)Expression.Equal(member, Expression.Constant(null, member.Type)) : Expression.Constant(false);
        if (node.Operator == QueryOperator.IsEmpty)
            return Expression.OrElse(Expression.Not(pathExists), type == typeof(string) ? Expression.OrElse(isNull, Expression.Equal(member, Capture(""))) : isNull);
        Expression operand;
        try
        {
            if (type == typeof(string)) operand = Capture(value.ToLowerInvariant());
            else if (type == typeof(bool)) operand = Capture(bool.Parse(value));
            else if (type == typeof(int)) operand = Capture(int.Parse(value, CultureInfo.InvariantCulture));
            else if (type == typeof(long)) operand = Capture(long.Parse(value, CultureInfo.InvariantCulture));
            else if (type == typeof(DateTime))
            {
                if (!value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) throw new FormatException();
                operand = Capture(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
            }
            else if (type.IsEnum)
            {
                var parsed = System.Enum.Parse(type, value, true);
                if (!System.Enum.IsDefined(type, parsed)) throw new FormatException();
                operand = Expression.Constant(parsed, type);
            }
            else throw new InvalidOperationException("Unsupported query field type.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException($"Enter a valid {type.Name} for {DeviceQueryCatalog.Label(field)}. Dates must use UTC, for example 2026-01-01T00:00:00Z.");
        }

        Expression left = type == typeof(string) ? Expression.Call(member, nameof(string.ToLower), Type.EmptyTypes) : member;
        if (underlying != null) operand = Expression.Convert(operand, member.Type);
        Expression comparison = node.Operator switch
        {
            QueryOperator.Equal => Expression.Equal(left, operand),
            QueryOperator.Contains => Expression.Call(left, nameof(string.Contains), Type.EmptyTypes, operand),
            QueryOperator.StartsWith => Expression.Call(left, nameof(string.StartsWith), Type.EmptyTypes, operand),
            QueryOperator.GreaterThan => Expression.GreaterThan(left, operand),
            QueryOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(left, operand),
            QueryOperator.LessThan => Expression.LessThan(left, operand),
            QueryOperator.LessOrEqual => Expression.LessThanOrEqual(left, operand),
            _ => throw new InvalidOperationException("Unknown query operator.")
        };
        return Expression.AndAlso(pathExists, nullable ? Expression.AndAlso(Expression.Not(isNull), comparison) : comparison);
    }

    private static Expression Capture<T>(T value) => ((Expression<Func<T>>)(() => value)).Body;

    /// <summary>
    /// Helps combine device matching rules so they all refer to the same item.
    /// </summary>
    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : base.VisitParameter(node);
    }
}

using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;

namespace CloneDeploy_Server.UI.Pages.PoliciesAndActions.Policies.Shared;

public sealed class PolicyActionEditorItem(PolicyActionOptionDto option, PolicyAction? assignment)
{
    public PolicyActionOptionDto Option { get; } = option;
    public PolicyAction? Assignment { get; set; } = assignment;
}

// Keeps the catalog and the execution sequence in sync without replacing condition settings.
public sealed class PolicyActionEditor
{
    private readonly Policy _policy;
    private readonly List<PolicyActionEditorItem> _items;

    public static PolicyDetailsDto CreateDraft(PolicyDetailsDto details)
    {
        // Only action assignments are edited here. Keep cancellation independent of the page's other forms.
        return new PolicyDetailsDto
        {
            Policy = new Policy
            {
                Id = details.Policy.Id, Guid = details.Policy.Guid, Name = details.Policy.Name,
                PolicyActions = details.Policy.PolicyActions.Select(x => new PolicyAction
                {
                    Id = x.Id, PolicyId = x.PolicyId, ActionGuid = x.ActionGuid, ActionType = x.ActionType,
                    Order = x.Order, ConditionId = x.ConditionId, ConditionFailedAction = x.ConditionFailedAction,
                    ConditionNextAction = x.ConditionNextAction
                }).ToList()
            },
            Actions = details.Actions,
            Conditions = details.Conditions
        };
    }

    public PolicyActionEditor(PolicyDetailsDto details)
    {
        _policy = details.Policy;
        _items = details.Actions.Select(option => new PolicyActionEditorItem(option,
            _policy.PolicyActions.FirstOrDefault(x => x.ActionGuid == option.Guid))).ToList();
        foreach (var assignment in _policy.PolicyActions.Where(x => _items.All(item => item.Option.Guid != x.ActionGuid)))
        {
            _items.Add(new PolicyActionEditorItem(new PolicyActionOptionDto(assignment.ActionGuid, 0,
                $"Unavailable action ({assignment.ActionGuid})", assignment.ActionType), assignment));
        }
    }

    public bool ReadOnly { get; set; }
    public IEnumerable<PolicyActionEditorItem> Assigned => _items.Where(x => x.Assignment is not null)
        .OrderBy(x => x.Assignment!.Order).ThenBy(x => x.Option.Guid);
    public IEnumerable<PolicyActionEditorItem> Items => Assigned.Concat(_items.Where(x => x.Assignment is null).OrderBy(x => x.Option.Name));

    public IEnumerable<PolicyActionEditorItem> Catalog(string? search, ActionType? type)
    {
        return _items.Where(x => x.Assignment is null && x.Option.Id > 0 &&
            (!type.HasValue || x.Option.Type == type) &&
            (string.IsNullOrWhiteSpace(search) || x.Option.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Option.Name);
    }

    public string? RemovalBlockedBy(PolicyActionEditorItem item)
    {
        return Assigned.FirstOrDefault(x => x != item &&
            x.Assignment!.ConditionFailedAction == PolicyConditionFailedAction.GotoModule &&
            x.Assignment.ConditionNextAction == item.Option.Guid)?.Option.Name;
    }

    public bool AssignOrMove(PolicyActionEditorItem item, int index)
    {
        if (ReadOnly || !_items.Contains(item) || (item.Assignment is null && item.Option.Id <= 0)) return false;
        var ordered = Assigned.Where(x => x != item).ToList();
        if (item.Assignment is null)
        {
            item.Assignment = new PolicyAction { PolicyId = _policy.Id, ActionGuid = item.Option.Guid, ActionType = item.Option.Type };
            _policy.PolicyActions.Add(item.Assignment);
        }
        ordered.Insert(index < 0 ? ordered.Count : Math.Clamp(index, 0, ordered.Count), item);
        SetOrder(ordered);
        return true;
    }

    public bool Move(PolicyActionEditorItem item, int offset)
    {
        var ordered = Assigned.ToList();
        var index = ordered.IndexOf(item);
        if (index < 0 || index + offset < 0 || index + offset >= ordered.Count) return false;
        return AssignOrMove(item, index + offset);
    }

    public bool Remove(PolicyActionEditorItem item)
    {
        if (ReadOnly || !_items.Contains(item) || item.Assignment is null || RemovalBlockedBy(item) is not null) return false;
        _policy.PolicyActions.Remove(item.Assignment);
        item.Assignment = null;
        SetOrder(Assigned.ToList());
        return true;
    }

    private static void SetOrder(List<PolicyActionEditorItem> ordered)
    {
        for (var index = 0; index < ordered.Count; index++) ordered[index].Assignment!.Order = index + 1;
    }
}

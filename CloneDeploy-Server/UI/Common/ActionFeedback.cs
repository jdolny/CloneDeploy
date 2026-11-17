using MudBlazor;

namespace CloneDeploy_Server.UI.Common;

public static class ActionFeedback
{
    // Evaluate prerequisites when clicked, before invoking the original action.
    public static Func<Task> GuardAction(this ISnackbar snackbar, Func<bool> unavailable, string reason, Func<Task> action)
        => async () =>
        {
            if (unavailable())
            {
                snackbar.Add(reason, Severity.Error);
                return;
            }

            await action();
        };

    public static Func<Task> GuardAction(this ISnackbar snackbar, Func<bool> unavailable, string reason, Action action)
        => snackbar.GuardAction(unavailable, reason, () =>
        {
            action();
            return Task.CompletedTask;
        });
}

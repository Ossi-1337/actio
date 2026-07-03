using System.Text;
using Actio.Core.Actions;

namespace Actio.Cli;

internal static class ActionCompatibilityFormatter
{
    public static string Format(IReadOnlyList<ActionCompatibilityEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Actio action compatibility matrix");
        builder.AppendLine();
        builder.AppendLine("| Action | Status | Action type | Supported refs | Milestone |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {entry.Name} | {entry.StatusText} | {entry.ActionType} | {entry.SupportedRefs} | {entry.RequiredMilestone} |");
        }

        builder.AppendLine();
        builder.AppendLine("Details:");

        foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {entry.Name}: {entry.CurrentBehavior} Limitations: {entry.Limitations} Evidence: {entry.Evidence}");
        }

        return builder.ToString().TrimEnd();
    }
}

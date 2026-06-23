using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal static class JobGraphPlanner
{
    public static JobExecutionPlan Plan(IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        var errors = new List<string>();
        var remainingNeeds = jobs.ToDictionary(
            job => job.Key,
            job => new HashSet<string>(job.Value.Needs, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var dependents = jobs.Keys.ToDictionary(
            jobName => jobName,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var job in jobs.Values)
        {
            foreach (var neededJob in job.Needs)
            {
                if (string.Equals(job.Name, neededJob, StringComparison.Ordinal))
                {
                    errors.Add($"workflow.jobs.{job.Name}.needs cannot reference itself.");
                    continue;
                }

                if (!jobs.ContainsKey(neededJob))
                {
                    errors.Add($"workflow.jobs.{job.Name}.needs references unknown job '{neededJob}'.");
                    continue;
                }

                if (dependents.TryGetValue(neededJob, out var dependentJobs))
                {
                    dependentJobs.Add(job.Name);
                }
            }
        }

        if (errors.Count > 0)
        {
            return new JobExecutionPlan([], errors);
        }

        var ready = new Queue<string>(jobs.Values
            .Where(job => remainingNeeds[job.Name].Count == 0)
            .Select(job => job.Name));
        var orderedJobs = new List<WorkflowJob>();

        while (ready.Count > 0)
        {
            var jobName = ready.Dequeue();
            orderedJobs.Add(jobs[jobName]);

            foreach (var dependentJob in dependents[jobName])
            {
                remainingNeeds[dependentJob].Remove(jobName);

                if (remainingNeeds[dependentJob].Count == 0)
                {
                    ready.Enqueue(dependentJob);
                }
            }
        }

        if (orderedJobs.Count != jobs.Count)
        {
            return new JobExecutionPlan(
                [],
                ["workflow.jobs contains a circular needs dependency."]);
        }

        return new JobExecutionPlan(orderedJobs, []);
    }
}

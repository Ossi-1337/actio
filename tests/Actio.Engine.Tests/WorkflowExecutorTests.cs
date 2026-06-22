using Actio.Core.Workflows;
using Actio.Engine.Execution;

namespace Actio.Engine.Tests;

public sealed class WorkflowExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RunsStepsInOrderAndReturnsSuccess()
    {
        var runner = new FakeRunnerProvider([0, 0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Restore", "dotnet restore", null),
                    new WorkflowStep("Test", "dotnet test", null)
                ]));

        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            output,
            error);

        Assert.True(result.Success);
        Assert.Equal(2, result.SuccessfulSteps);
        Assert.Equal(2, result.TotalSteps);
        Assert.Equal(["Restore", "Test"], runner.Requests.Select(request => request.StepName));
        Assert.Equal("true", runner.Requests[0].Environment["DOTNET_NOLOGO"]);
        Assert.Contains("[test] Restore", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_StopsAfterFailedStep()
    {
        var runner = new FakeRunnerProvider([0, 42]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("One", "echo one", null),
                    new WorkflowStep("Two", "exit 42", null),
                    new WorkflowStep("Three", "echo three", null)
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(3, result.TotalSteps);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains(result.Errors, error => error.Contains("exit code 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNeedsUntilJobDagMilestone()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                ["prepare"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("job DAG milestone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureForUnsupportedRunner()
    {
        var runner = new FakeRunnerProvider([0], supportsRunner: false);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "unknown",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(0, result.SuccessfulSteps);
        Assert.Equal(1, result.TotalSteps);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowDocument CreateWorkflow(params WorkflowJob[] jobs)
    {
        return new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "true"
            },
            jobs.ToDictionary(job => job.Name, StringComparer.Ordinal));
    }

    private sealed class FakeRunnerProvider : IRunnerProvider
    {
        private readonly Queue<int> _exitCodes;
        private readonly bool _supportsRunner;

        public FakeRunnerProvider(IEnumerable<int> exitCodes, bool supportsRunner = true)
        {
            _exitCodes = new Queue<int>(exitCodes);
            _supportsRunner = supportsRunner;
        }

        public List<StepExecutionRequest> Requests { get; } = [];

        public bool SupportsRunner(string runsOn)
        {
            return _supportsRunner;
        }

        public Task<StepExecutionResult> ExecuteStepAsync(
            StepExecutionRequest request,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new StepExecutionResult(_exitCodes.Dequeue()));
        }
    }
}

namespace Actio.Cli;

public static class CliHelpText
{
    public const string Root = """
        Actio - local-first workflow runner.

        Usage:
          actio run <workflow>.yml
          actio <workflow>.yml
          actio web
          actio status <run-id>
          actio rerun <run-id>
          actio cancel <run-id>
          actio compatibility
          actio cache <command>
          actio [options]

        Commands:
          run <workflow>.yml   Run a workflow from .workflows, with .github/workflows fallback.
          web                  Start the local Actio web UI.
          status <run-id>      Show stored status for a workflow run.
          rerun <run-id>       Rerun a completed workflow run.
          cancel <run-id>      Request cancellation for a running workflow run.
          compatibility        Show the known GitHub Actions compatibility matrix.
          cache                Inspect or clean Actio cache entries.

        Arguments:
          <workflow>.yml       Bare workflow filename, for example ci.yml.
          <run-id>             Stored Actio run id.

        Options:
          -h, --help              Show help.
          --version               Show version.

        Examples:
          actio run ci.yml
          actio ci.yml
          actio web
          actio status 20260703090029234-555a1799
          actio rerun 20260703090029234-555a1799
          actio cancel 20260703090029234-555a1799
          actio compatibility
          actio cache list
          actio run --help
        """;

    public const string Run = """
        Actio run - run a workflow.

        Usage:
          actio run <workflow>.yml [options]
          actio <workflow>.yml [options]

        Arguments:
          <workflow>.yml          Bare workflow filename, for example ci.yml.

        Options:
          -h, --help              Show help for the run command.
          --input NAME=VALUE      Provide a workflow_dispatch input. Can be repeated.

        Description:
          Resolves .workflows/<workflow>.yml first, then .github/workflows/<workflow>.yml
          as a GitHub Actions compatibility fallback. Supported steps execute in Docker.
          CLI runs are recorded as workflow_dispatch runs. Inputs are validated against
          workflow_dispatch.inputs when the workflow defines them.
          Local uses references work today. External action references are validated
          and warn when mutable, then execute when their action type is supported.

        Examples:
          actio run ci.yml
          actio run ci.yml --input environment=staging
          actio ci.yml
        """;

    public const string Rerun = """
        Actio rerun - rerun a completed workflow run.

        Usage:
          actio rerun <run-id>

        Options:
          -h, --help           Show help for the rerun command.

        Description:
          Reads the stored run record from ACTIO_HOME, reuses the workflow file, project root,
          and workflow_dispatch inputs, then starts a new local run.

        Examples:
          actio rerun 20260703090029234-555a1799
        """;

    public const string Cancel = """
        Actio cancel - request cancellation for a running workflow run.

        Usage:
          actio cancel <run-id>

        Options:
          -h, --help           Show help for the cancel command.

        Description:
          Writes a local cancellation request into ACTIO_HOME. A running Actio process polls
          this request and cancels active runner work through the normal cancellation path.

        Examples:
          actio cancel 20260703090029234-555a1799
        """;

    public const string Status = """
        Actio status - show stored workflow run status.

        Usage:
          actio status <run-id>

        Options:
          -h, --help           Show help for the status command.

        Description:
          Reads the local run record from ACTIO_HOME and prints the workflow name, status,
          duration, job count, artifact count, and workflow file path.

        Examples:
          actio status 20260703090029234-555a1799
        """;

    public const string Web = """
        Actio web - start the local web UI.

        Usage:
          actio web [options]

        Options:
          -h, --help           Show help for the web command.
          --project-root PATH  Project root to show workflows and runs for.
          --actio-home PATH    Actio storage root. Defaults to ACTIO_HOME or user-local storage.
          --url URL            URL to bind. Defaults to http://127.0.0.1:17345.

        Description:
          Serves workflow history, run details, logs, artifacts, and workflow files from local storage.

        Examples:
          actio web
          actio web --url http://127.0.0.1:17345
        """;

    public const string Cache = """
        Actio cache - inspect or clean local cache entries.

        Usage:
          actio cache list
          actio cache clean

        Commands:
          list                 Show local action and dependency cache entries.
          clean                Remove local action and dependency cache entries.

        Options:
          -h, --help           Show help for the cache command.

        Description:
          Uses ACTIO_HOME or the user-local Actio storage root.

        Examples:
          actio cache list
          actio cache clean
        """;

    public const string Compatibility = """
        Actio compatibility - show known action compatibility.

        Usage:
          actio compatibility

        Options:
          -h, --help           Show help for the compatibility command.

        Description:
          Prints Actio's curated compatibility matrix for common GitHub Actions and selected
          third-party actions. This is not an allowlist or a live Marketplace scan. Unlisted
          GitHub actions are resolved from their action metadata at runtime.

        Examples:
          actio compatibility
        """;
}

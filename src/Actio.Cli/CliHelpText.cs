namespace Actio.Cli;

public static class CliHelpText
{
    public const string Root = """
        Actio - local-first workflow runner.

        Usage:
          actio run <workflow>.yml
          actio <workflow>.yml
          actio web
          actio cache <command>
          actio [options]

        Commands:
          run <workflow>.yml   Run a workflow from the project's .workflows directory.
          web                  Start the local Actio web UI.
          cache                Inspect or clean Actio cache entries.

        Arguments:
          <workflow>.yml       Bare workflow filename inside .workflows, for example ci.yml.

        Options:
          -h, --help           Show help.
          --version            Show version.

        Examples:
          actio run ci.yml
          actio ci.yml
          actio web
          actio cache list
          actio run --help
        """;

    public const string Run = """
        Actio run - run a workflow.

        Usage:
          actio run <workflow>.yml
          actio <workflow>.yml

        Arguments:
          <workflow>.yml       Bare workflow filename inside .workflows, for example ci.yml.

        Options:
          -h, --help           Show help for the run command.

        Description:
          Parses .workflows/<workflow>.yml and executes supported run steps in Docker.

        Examples:
          actio run ci.yml
          actio ci.yml
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
          list                 Show local action cache entries.
          clean                Remove local action cache entries.

        Options:
          -h, --help           Show help for the cache command.

        Description:
          Uses ACTIO_HOME or the user-local Actio storage root.

        Examples:
          actio cache list
          actio cache clean
        """;
}

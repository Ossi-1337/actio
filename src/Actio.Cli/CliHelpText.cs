namespace Actio.Cli;

public static class CliHelpText
{
    public const string Root = """
        Actio - local-first workflow runner.

        Usage:
          actio run <workflow>.yml
          actio <workflow>.yml
          actio [options]

        Commands:
          run <workflow>.yml   Run a workflow from the project's .workflows directory.

        Arguments:
          <workflow>.yml       Bare workflow filename inside .workflows, for example ci.yml.

        Options:
          -h, --help           Show help.
          --version            Show version.

        Examples:
          actio run ci.yml
          actio ci.yml
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
}

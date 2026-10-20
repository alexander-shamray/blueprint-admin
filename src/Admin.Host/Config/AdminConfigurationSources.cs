namespace Admin.Host.Config;

public static class AdminConfigurationSources
{
    /// <summary>
    /// The <c>BLUEPRINT_</c> environment variables, then the command line again. <c>CreateBuilder</c> adds the command line
    /// before any source added here, so without the second add the environment would beat a <c>--Admin:Key=value</c>; a value
    /// named on the command line is the more deliberate of the two (<see cref="AdminOptions"/>' summary).
    /// </summary>
    public static IConfigurationBuilder AddAdminSources(this IConfigurationBuilder configuration, string[] args) =>
        configuration.AddEnvironmentVariables("BLUEPRINT_").AddCommandLine(args);
}

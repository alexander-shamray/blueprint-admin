namespace Admin.Host.Config;

/// <summary>
/// The two clones and the Compose file as absolute paths, validated once at
/// startup so that a wrong directory fails the host with the key to fix
/// rather than failing the first command with "file not found".
/// </summary>
public sealed record RepoPaths(string BackendDir, string FrontendDir, string ComposeFile)
{
    public static RepoPaths From(AdminOptions options, string baseDir)
    {
        string backend = Path.GetFullPath(options.BackendDir, baseDir);
        string frontend = Path.GetFullPath(options.FrontendDir, baseDir);
        string compose = Path.GetFullPath(options.ComposeFile, backend);

        if (!options.FakePlatform)
        {
            if (!Directory.Exists(backend))
            {
                throw new InvalidOperationException($"Admin:BackendDir resolves to '{backend}', which does not exist.");
            }

            if (!File.Exists(compose))
            {
                throw new InvalidOperationException($"Admin:ComposeFile resolves to '{compose}', which does not exist.");
            }

            if (!File.Exists(Path.Combine(frontend, "package.json")))
            {
                throw new InvalidOperationException($"Admin:FrontendDir resolves to '{frontend}', which has no package.json.");
            }
        }

        return new RepoPaths(backend, frontend, compose);
    }
}

using System.Runtime.Loader;
using KSA.Multiplayer.DedicatedServer;

// Locates the local KSA installation before any KSA type is loaded.
string? gameDir = KsaInstall.Locate(args);
if (gameDir == null)
{
    Console.WriteLine("ERROR: Could not find a Kitten Space Agency installation.");
    Console.WriteLine("The dedicated server loads the game's assemblies from your local");
    Console.WriteLine("KSA install. Set \"gamePath\" in server_config.json, or pass -gamepath <dir>.");
    return 1;
}

AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
{
    if (assemblyName.Name is null)
        return null;

    // Resolves assemblies from the server directory, then the game install.
    string local = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
    if (File.Exists(local))
        return context.LoadFromAssemblyPath(local);

    string fromGame = Path.Combine(gameDir, assemblyName.Name + ".dll");
    return File.Exists(fromGame) ? context.LoadFromAssemblyPath(fromGame) : null;
};

return ServerHost.Run(args);

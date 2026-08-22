using System.Text.Json;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Locates the local Kitten Space Agency installation directory.</summary>
    public static class KsaInstall
    {
        private const string Marker = "KSA.dll";

        public static string? Locate(string[] args)
        {
            foreach (string? candidate in EnumerateCandidates(args))
            {
                if (IsValid(candidate, out string? resolved))
                    return resolved;
            }
            return null;
        }

        private static IEnumerable<string?> EnumerateCandidates(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-gamepath", StringComparison.OrdinalIgnoreCase))
                    yield return args[i + 1];
            }

            yield return Environment.GetEnvironmentVariable("KSA_PATH");
            yield return FromConfigFile();

            foreach (string path in DefaultLocations())
                yield return path;
        }

        private static string? FromConfigFile()
        {
            try
            {
                string configPath = Path.Combine(AppContext.BaseDirectory, "server_config.json");
                if (!File.Exists(configPath))
                    return null;

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("gamePath", out JsonElement value))
                    return value.GetString();
            }
            catch
            {
                // Ignores a malformed config file.
            }
            return null;
        }

        private static IEnumerable<string> DefaultLocations()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsWindows())
            {
                yield return "C:\\Program Files\\Kitten Space Agency";
                yield return "C:\\Program Files (x86)\\Kitten Space Agency";
                yield return Path.Combine(home, "Kitten Space Agency");
            }
            else
            {
                yield return Path.Combine(home, "Games", "KSA");
                yield return Path.Combine(home, "KSA");
                yield return "/opt/ksa";
                yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Kitten Space Agency");
            }

            yield return AppContext.BaseDirectory;
        }

        private static bool IsValid(string? path, out string? resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string full = Path.GetFullPath(path.Trim());
                if (!File.Exists(Path.Combine(full, Marker)))
                    return false;

                resolved = full;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

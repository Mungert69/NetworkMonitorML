// Tests/EnvBootstrapper.cs
using System;
using System.IO;
using DotNetEnv;

namespace NetworkMonitorML.IntegrationTests
{
    internal static class EnvBootstrapper
    {
        private static bool _loaded;

        public static void EnsureLoaded(string? explicitPath = null)
        {
            if (_loaded) return;

            // 1) explicit path
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                Env.Load(explicitPath);
                _loaded = true;
                return;
            }

            // 2) cwd
            const string fileName = ".env";
            if (File.Exists(fileName))
            {
                Env.Load(fileName);
                _loaded = true;
                return;
            }

            // 3) walk up to repo root
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                {
                    Env.Load(candidate);
                    _loaded = true;
                    return;
                }
                dir = dir.Parent;
            }

            // not found -> do nothing
        }
    }
}

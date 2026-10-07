using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// What to do with an exception from one of the per-frame passes. The same failure every
    /// frame used to be written to the log every frame (a 1 MB log from a few minutes' play);
    /// it is written in full once, and counted after that.
    /// </summary>
    internal static class Failures
    {
        private static readonly Dictionary<string, int> _seen = new Dictionary<string, int>();

        internal static void Report(string what, Exception e)
        {
            string key = what + "|" + e.GetType().FullName + "|" + e.Message;
            _seen.TryGetValue(key, out int times);
            _seen[key] = ++times;

            if (times == 1) Plugin.Logger.LogError(what + ": " + e);
            else if (times == 600 || times % 36000 == 0)
                Plugin.Logger.LogError($"{what}: the same error as before, {times} times now ({e.Message}).");

            Compat.Check(e);
        }
    }

    /// <summary>
    /// The plugin is built against one version of the game. On another, a class or member it
    /// uses can be missing or shaped differently, and the first use of it throws: for the
    /// player, part of the plugin silently does nothing (the first report was "the toggle
    /// hides the UI but the camera does not move", on an older game where
    /// MapManager.mapListTable was not static). When that happens, every reference this
    /// plugin makes into the game is tried once and the ones that fail are listed together,
    /// so one log says everything that copy of the game lacks.
    /// </summary>
    internal static class Compat
    {
        private static bool _checked;

        internal static void Check(Exception e)
        {
            if (_checked) return;
            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (!(inner is MissingMemberException) && !(inner is TypeLoadException)) continue;
                _checked = true;
                Tell();
                return;
            }
        }

        private static void Tell()
        {
            List<string> missing;
            try { missing = Missing(); }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not list what this game version lacks: " + e.Message);
                return;
            }

            string version = "unknown";
            try { version = Application.version + " (Unity " + Application.unityVersion + ")"; }
            catch { }

            Plugin.Logger.LogWarning(
                $"This copy of the game (version {version}) differs from the one {Plugin.Name} {Plugin.Version} " +
                $"was made for: {missing.Count} thing(s) it uses are missing or different here, so parts of " +
                "it will not work. Updating the game should fix it. If you report this, include these lines:" +
                (missing.Count == 0 ? "\n  (none found by the check; the error above is all there is)"
                                    : "\n  " + string.Join("\n  ", missing)));
            Notice.Tell($"{Plugin.Name}: this game version is missing things it needs, so parts of it " +
                        "will not work. Update the game; the log (LogOutput.log) has the details.", 20f);
        }

        /// <summary>
        /// Every type, method and field this plugin refers to outside itself, resolved the way
        /// the runtime resolves them on first use; the messages of those that do not resolve.
        /// </summary>
        private static List<string> Missing()
        {
            var missing = new List<string>();
            var assembly = typeof(Plugin).Assembly;
            if (string.IsNullOrEmpty(assembly.Location)) return missing;
            var module = assembly.ManifestModule;

            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();

            void Try(int token, bool type)
            {
                try
                {
                    if (type) module.ResolveType(token);
                    else module.ResolveMember(token);
                }
                catch (MissingMemberException e) { Add(e.Message); }
                catch (TypeLoadException e) { Add(e.Message); }
                catch { /* needs type arguments, and the like: not what is being looked for */ }
            }
            void Add(string message)
            {
                if (!missing.Contains(message)) missing.Add(message);
            }

            foreach (var handle in metadata.TypeReferences) Try(MetadataTokens.GetToken(handle), true);
            foreach (var handle in metadata.MemberReferences) Try(MetadataTokens.GetToken(handle), false);
            return missing;
        }
    }
}

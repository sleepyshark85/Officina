using Microsoft.Extensions.FileSystemGlobbing;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>The protected paths of a working copy as it is now, which a command must not see or change (WS-05).</summary>
internal static class ProtectedPaths
{
    /// <summary>The hidden and the read-only files and folders in the working copy, by full path. Links are skipped; they lead nowhere inside the sandbox.</summary>
    public static (List<string> Hidden, List<string> ReadOnly) Find(string directory, WorkspaceOptions workspace)
    {
        var (hidden, readOnly) = (new Matcher(), new Matcher());
        foreach (var path in WorkspaceOptions.FixedProtectedPaths.Concat(workspace.ProtectedPaths))
        {
            (path.Access == PathAccess.Hidden ? hidden : readOnly).AddInclude(path.Path);
        }

        var found = (Hidden: new List<string>(), ReadOnly: new List<string>());
        Visit(directory);
        return found;

        void Visit(string folder)
        {
            // The default also skips hidden entries, which on Linux are the dot files, such as .env, that matter most here.
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = Path.GetRelativePath(directory, entry.FullName).Replace('\\', '/');
                if (hidden.Match(relative).HasMatches)
                {
                    found.Hidden.Add(entry.FullName);
                }
                else if (readOnly.Match(relative).HasMatches)
                {
                    found.ReadOnly.Add(entry.FullName);
                }
                else if (entry is DirectoryInfo)
                {
                    Visit(entry.FullName);
                }
            }
        }
    }
}

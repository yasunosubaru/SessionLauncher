using System;
using System.IO;

namespace SessionLauncher.App.Models
{
    /// <summary>
    /// One top-level OpenCode conversation, as described by a row of
    /// TOP-LEVEL-SESSIONS.md.
    /// </summary>
    /// <param name="Id">OpenCode session id, e.g. <c>ses_4a1b2c3d4e5f6g7h8i9j0k1l</c>.</param>
    /// <param name="Title">Conversation title. May be empty for untitled sessions.</param>
    /// <param name="Directory">Directory the conversation belongs to, using forward slashes.</param>
    /// <param name="Agent">Agent that ran it, e.g. <c>build</c>.</param>
    /// <param name="Updated">Last activity, local time.</param>
    /// <param name="Messages">Number of messages in the conversation.</param>
    /// <param name="ProjectPath">
    /// <paramref name="Directory"/> rewritten to a native path, for explorer.exe.
    /// A drive root becomes <c>C:\</c>; an empty directory stays empty.
    /// </param>
    /// <param name="RawDirectory">The directory cell exactly as it appeared in the file.</param>
    public sealed record SessionInfo(
        string Id,
        string Title,
        string Directory,
        string Agent,
        DateTimeOffset Updated,
        int Messages,
        string ProjectPath,
        string RawDirectory)
    {
        /// <summary>Title if we have one, otherwise the id, for display in the list.</summary>
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title;

        /// <summary>Short id for the UI, e.g. <c>ses_f171…</c>.</summary>
        public string ShortId => Id.Length <= 12 ? Id : Id[..12];

        /// <summary>True when the conversation points at a directory that still exists.</summary>
        public bool DirectoryExists =>
            !string.IsNullOrEmpty(ProjectPath) &&
            System.IO.Directory.Exists(ProjectPath);   // fully qualified: the record's own
                                                       // `Directory` property shadows the type

        /// <summary>Last path segment, for a compact secondary line.</summary>
        public string DirectoryLeaf
        {
            get
            {
                if (string.IsNullOrEmpty(ProjectPath)) return string.Empty;
                var trimmed = ProjectPath.TrimEnd('\\', '/');
                var idx = trimmed.LastIndexOfAny(new[] { '\\', '/' });
                return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
            }
        }
    }
}

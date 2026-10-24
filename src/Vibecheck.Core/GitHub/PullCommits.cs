using Vibecheck.Diffing;

namespace Vibecheck.GitHub;

/// <summary>
/// Brings a pull request's commits into the reviewer's repository and works out the range it compares, as GitHub
/// does. See <c>docs/pull-requests.md</c> sections 4.2 and 4.3.
/// </summary>
public static class PullCommits
{
    /// <summary>
    /// Returns the name of the remote whose address points at a GitHub repository, over HTTPS or SSH, or
    /// <c>origin</c> when none does and the working tree has one, or else <see langword="null"/>.
    /// </summary>
    public static string? RemoteOf(string dir, GitHubRepository repository)
    {
        var lines = (Git.TryRun(dir, "config", "--get-regexp", @"^remote\..*\.url$") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remotes = lines.Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).Select(p => (Name: p[0]["remote.".Length..^".url".Length], Url: p[1])).ToList();
        var wanted = $"{repository.Host}/{repository.NameWithOwner}";
        foreach (var (name, url) in remotes)
        {
            // An SSH address such as git@host:owner/name.git reads as host/owner/name once its user and colon go.
            var plain = url.Trim().TrimEnd('/');
            if (plain.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) plain = plain[..^4];
            plain = plain.Contains("://", StringComparison.Ordinal) ? plain[(plain.IndexOf("://", StringComparison.Ordinal) + 3)..] : plain.Replace(':', '/');
            if (plain.Contains('@')) plain = plain[(plain.IndexOf('@') + 1)..];
            if (plain.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return remotes.Any(r => r.Name == "origin") ? "origin" : null;
    }

    /// <summary>Returns a value indicating whether a pull request's head and base commits are both present in the repository.</summary>
    public static bool Present(string dir, PullSnapshot pull) =>
        new[] { pull.HeadOid, pull.BaseOid }.All(oid => Git.TryRun(dir, "cat-file", "-e", oid + "^{commit}") is not null);

    /// <summary>
    /// Fetches a pull request's head and its base branch from a remote. The empty refmap keeps git from updating any
    /// ref, so only objects and <c>FETCH_HEAD</c> are written, and the reviewer's branches and remote-tracking refs stay
    /// as they were.
    /// </summary>
    public static void Fetch(string dir, string remote, int number, string baseRefName) =>
        Git.Run(dir, "fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "--refmap=", remote, $"refs/pull/{number}/head", $"refs/heads/{baseRefName}");

    /// <summary>
    /// Returns the commits a pull request compares, as GitHub does: the merge base of the base branch's tip on GitHub
    /// and the head, and the head. Both commits must be present.
    /// </summary>
    public static (string Base, string Head) Range(string dir, PullSnapshot pull) =>
        (Git.Run(dir, "merge-base", pull.BaseOid, pull.HeadOid), pull.HeadOid);
}

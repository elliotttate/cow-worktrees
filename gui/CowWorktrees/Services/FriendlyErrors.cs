using System.Text.RegularExpressions;

namespace CowWorktrees.Services;

/// <summary>What the person can do about an error, as a button.</summary>
public enum FriendlyAction { None, InstallGit, OpenDiskSettings, OpenFolder, OpenTroubleshooting }

/// <summary>A script's failure in plain words: what happened, why, and what to do. <see cref="Raw"/> is always shown too.</summary>
public sealed record FriendlyError(string Title, string Explanation, string Raw, FriendlyAction Action = FriendlyAction.None, string? ActionLabel = null, string? ActionTarget = null, bool Known = true);

/// <summary>
/// Known failure messages of setup.ps1, uninstall.ps1, cow_worktree.ps1 and migrate_worktree.ps1, explained.
/// The wording of the explanations follows docs\troubleshooting.md. A message that is not known is shown as
/// it is: nothing is ever hidden.
/// </summary>
public static class FriendlyErrors
{
    private static readonly string[] Prefixes = { "setup:", "uninstall:", "cow_worktree:", "migrate_worktree:" };
    private static readonly Regex Progress = new(@"^\s*\d+(\.\d+)?s\s+", RegexOptions.Compiled);

    /// <summary>The script's own failure line ("setup: ..."), with the lines that belong to it, or null.</summary>
    public static string? FailureText(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var prefix = Prefixes.FirstOrDefault(p => lines[i].StartsWith(p, StringComparison.Ordinal));
            if (prefix == null) continue;
            var text = lines[i][prefix.Length..].Trim();
            // a failure can go on for a few lines (a list of files); they follow it directly
            var more = lines.Skip(i + 1).TakeWhile(l => l.Length > 0 && !Prefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal))).Take(12);
            return string.Join("\n", new[] { text }.Concat(more));
        }
        return null;
    }

    public static FriendlyError Explain(IReadOnlyList<string> lines, int? exitCode = null)
    {
        var raw = FailureText(lines)
                  ?? lines.Select(l => Progress.Replace(l, "")).LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
                  ?? (exitCode is { } code ? $"The script stopped with exit code {code} and said nothing." : "The script stopped and said nothing.");
        return Explain(raw);
    }

    public static FriendlyError Explain(string raw)
    {
        bool Has(string s) => raw.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("git is not on PATH"))
            return new("Git is not installed", "Worktrees are a Git feature, so Git for Windows has to be on this PC. Install it, then try again; the app looks for it again by itself.", raw, FriendlyAction.InstallGit, "Install Git");
        if (Has("Git 2.38 or newer is needed"))
            return new("Git is too old", "This needs Git 2.38 or later. Install the current Git for Windows over the old one, then try again.", raw, FriendlyAction.InstallGit, "Update Git");
        if (Has("is not inside a Git repository"))
            return new("That folder is not a Git repository", "Choose the folder of a project that is under Git: the one that has a hidden .git folder in it.", raw);
        if (Has("must be on a ReFS volume") || Has("is not on a ReFS volume"))
            return new("That drive cannot make copy-on-write copies", "Worktrees have to live on a Dev Drive (a drive formatted with ReFS). On other drives every copy takes the full size of the project. Choose a folder on a Dev Drive, or create one in Windows Settings.", raw, FriendlyAction.OpenDiskSettings, "Open disk settings");
        if (Regex.IsMatch(raw, @"drive \w: not found", RegexOptions.IgnoreCase))
            return new("That drive is not there", "The drive you chose for the worktrees is not connected. If it is a Dev Drive on a virtual disk, it may need to be attached again in Windows Settings.", raw, FriendlyAction.OpenDiskSettings, "Open disk settings");
        if (Has("-Root must not be inside the repository"))
            return new("Choose a folder outside the project", "The worktrees cannot live inside the project's own folder: Git would see them as files of the project. Choose a folder on the Dev Drive that is not inside it.", raw);
        if (Has("could not work out a base ref") || Has("is not a commit in"))
            return new("The starting branch was not found", "New worktrees start from one branch of the project, usually origin/main. That branch could not be found. Open Repository settings and choose a branch that exists.", raw);
        if (Has("exists but is not a worktree of"))
            return new("The folder is already in use", "There is already a folder called \"base\" at that place, and it does not belong to this project. Choose another folder for the worktrees, or move that one away.", raw, FriendlyAction.OpenFolder, "Open the folder");
        if (Has("git worktree add for the base failed") || Has("git worktree add failed"))
            return new("Git could not make the copy", "Git refused to check out the project into the new folder. The details below say why; the most common reasons are a full drive and a path that is too long.", raw, FriendlyAction.OpenTroubleshooting, "Troubleshooting");
        if (Has("must stay clean") || Has("it must be clean") || Has("is the base clean"))
            return new("The base copy has changed files", "Every worktree is cloned from the base copy, so it must match the project exactly. Something edited or built in it. Put the listed files back (in the base folder: git checkout -- <file>), then try again.", raw, FriendlyAction.OpenFolder, "Open the base folder");
        if (Has("Filename too long"))
            return new("A path is too long for Git", "Windows limits path lengths unless Git is told otherwise. Run this once, then try again:  git config --global core.longpaths true", raw);
        if (Has("still in the pool"))
            return new("Worktrees are still here", "The base copy can only be deleted when no worktrees are left next to it. Remove the worktrees listed below first (or keep the base copy and just disconnect the repository).", raw);
        if (Has("another refresh of the base is running") || Has("another base build is running"))
            return new("It is already running", "A refresh of the base copy is running right now, perhaps the daily upkeep. Wait for it to finish, then try again.", raw);
        if (Has("has no build section"))
            return new("No build is set up", "This repository has no build command, so there is nothing to build. Add one in Repository settings under Faster first builds.", raw);
        if (Has("building the base failed"))
            return new("The build of the base copy failed", "The build command stopped with an error. The build log has the details; new worktrees still work, they just start without build output.", raw, FriendlyAction.OpenFolder, "Open the folder");
        if (Has("git worktree remove refused"))
            return new("The worktree has work in it", "Git will not remove a worktree that has changed or untracked files, or that is locked, so nothing is lost by accident. Commit or delete the files first.", raw);
        if (Has("already exists"))
            return new("That name is taken", "There is already a folder with that name next to the other worktrees. Choose another name.", raw);
        if (Has("the main checkout cannot be migrated"))
            return new("This is the project's main folder", "Only extra worktrees can be moved. The main folder is the project itself and stays where it is.", raw);
        if (Has("is a sparse checkout"))
            return new("Nothing to gain here", "This worktree is a sparse checkout: it already takes almost no space, so it is left as it is.", raw);
        if (Has("processes are running from") || Has("something still has"))
            return new("Something is still using the old folder", "A terminal, an editor, a build or an AI session is open in the old worktree. Close it, then run this again: nothing has been removed, and the copy that was made is reused.", raw);
        if (Has("staged changes"))
            return new("There are staged changes", "The old worktree has changes that are staged but not committed. Commit them or unstage them (git restore --staged .), then try again.", raw);
        if (Has("git status differs between old and new") || Has("files differ between old and new"))
            return new("The copy does not match yet", "The new copy was compared with the old worktree and they differ, so nothing was removed. This usually means something changed files in the old worktree while it was being copied. Run it again.", raw);
        if (Has("timed out"))
            return new("It took too long", "The script was stopped because it ran far longer than it should. Look at the details below for the last thing it did.", raw);
        return new("It did not work", "The script stopped with the message below.", raw, FriendlyAction.OpenTroubleshooting, "Troubleshooting", null, Known: false);
    }

    public const string TroubleshootingUrl = "https://github.com/elliotttate/cow-worktrees/blob/main/docs/troubleshooting.md";
    public const string GitDownloadUrl = "https://git-scm.com/download/win";
}

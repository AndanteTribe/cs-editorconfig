#:package Octokit@14.*

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Octokit;

static class FilePaths
{
    public const string EditorConfig = ".editorconfig";
    public const string GitAttributes = ".gitattributes";
    public const string UpdateStream = ".github/workflows/update-stream.yml";
    public const string BranchPrefix = "feature/update-editorconfig-";
}

var token = Environment.GetEnvironmentVariable("GH_TOKEN")
    ?? throw new InvalidOperationException("GH_TOKEN is not set.");
var sourceRepo = Environment.GetEnvironmentVariable("SOURCE_REPO")
    ?? throw new InvalidOperationException("SOURCE_REPO is not set.");
var commitSha = Environment.GetEnvironmentVariable("COMMIT_SHA")
    ?? throw new InvalidOperationException("COMMIT_SHA is not set.");
var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
    ?? throw new InvalidOperationException("GITHUB_WORKSPACE is not set.");

var branchName = string.Create(FilePaths.BranchPrefix.Length + 7, commitSha, static (span, sha) =>
{
    FilePaths.BranchPrefix.AsSpan().CopyTo(span);
    sha.AsSpan()[..7].CopyTo(span[FilePaths.BranchPrefix.Length..]);
});

var client = new GitHubClient(new ProductHeaderValue("cs-editorconfig-distributor"))
{
    Credentials = new Credentials(token)
};

var editorConfigContent = await File.ReadAllTextAsync(Path.Combine(workspace, FilePaths.EditorConfig));
var gitAttributesContent = await File.ReadAllTextAsync(Path.Combine(workspace, FilePaths.GitAttributes));

var installationRepos = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent();
var changed = false;
var tasks = new List<Task>();

foreach (var repo in installationRepos.Repositories)
{
    if (repo.FullName == sourceRepo)
    {
        continue;
    }

    changed = true;
    tasks.Add(ProcessRepositoryWithLogging(client, repo, branchName, editorConfigContent, gitAttributesContent, sourceRepo));
}

await Task.WhenAll(tasks);

if (!changed)
{
    Console.WriteLine("No repositories to update.");
}

static async Task ProcessRepositoryWithLogging(
    GitHubClient client,
    Repository repo,
    string branchName,
    string editorConfigContent,
    string gitAttributesContent,
    string sourceRepo)
{
    Console.WriteLine("::group::Processing " + repo.FullName);
    try
    {
        await ProcessRepository(client, repo, branchName, editorConfigContent, gitAttributesContent, sourceRepo);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Failed to process " + repo.FullName + ": " + ex.Message);
    }
    Console.WriteLine("::endgroup::");
}

static async Task ProcessRepository(
    GitHubClient client,
    Repository repo,
    string branchName,
    string editorConfigContent,
    string gitAttributesContent,
    string sourceRepo)
{
    var owner = repo.Owner.Login;
    var name = repo.Name;

    var baseRef = await client.Git.Reference.Get(owner, name, "heads/" + repo.DefaultBranch);
    var baseSha = baseRef.Object.Sha;
    var baseCommit = await client.Git.Commit.Get(owner, name, baseSha);

    var (currentEditorConfig, _) = await TryGetFileContent(client, owner, name, FilePaths.EditorConfig);
    var (currentGitAttributes, _) = await TryGetFileContent(client, owner, name, FilePaths.GitAttributes);
    var updateStreamSha = await TryGetFileSha(client, owner, name, FilePaths.UpdateStream);

    var editorConfigChanged = currentEditorConfig is null
        || !editorConfigContent.AsSpan().SequenceEqual(currentEditorConfig.AsSpan());
    var gitAttributesChanged = currentGitAttributes is null
        || !gitAttributesContent.AsSpan().SequenceEqual(currentGitAttributes.AsSpan());
    var updateStreamExists = updateStreamSha is not null;

    if (!editorConfigChanged && !gitAttributesChanged && !updateStreamExists)
    {
        Console.WriteLine("No changes in " + repo.FullName + ", skipping.");
        return;
    }

    var newTreeRequest = new NewTree { BaseTree = baseCommit.Tree.Sha };

    if (editorConfigChanged)
    {
        newTreeRequest.Tree.Add(new NewTreeItem
        {
            Path = FilePaths.EditorConfig,
            Mode = "100644",
            Type = TreeType.Blob,
            Content = editorConfigContent
        });
    }

    if (gitAttributesChanged)
    {
        newTreeRequest.Tree.Add(new NewTreeItem
        {
            Path = FilePaths.GitAttributes,
            Mode = "100644",
            Type = TreeType.Blob,
            Content = gitAttributesContent
        });
    }

    if (updateStreamExists)
    {
        // Setting Sha = null on an existing path removes the file from the tree
        newTreeRequest.Tree.Add(new NewTreeItem
        {
            Path = FilePaths.UpdateStream,
            Mode = "100644",
            Type = TreeType.Blob,
            Sha = null
        });
    }

    var newTree = await client.Git.Tree.Create(owner, name, newTreeRequest);
    var newCommit = await client.Git.Commit.Create(owner, name, new NewCommit(
        "chore: update cs-editorconfig",
        newTree.Sha,
        new[] { baseSha }));

    await client.Git.Reference.Create(owner, name, new NewReference(
        "refs/heads/" + branchName,
        newCommit.Sha));

    var prBody = $"""
        Automated update of `.editorconfig` and `.gitattributes` from [cs-editorconfig](https://github.com/{sourceRepo}).

        `.github/workflows/update-stream.yml` has been removed as updates are now distributed automatically by the GitHub App.
        """;

    try
    {
        await client.PullRequest.Create(owner, name, new NewPullRequest(
            "chore: update cs-editorconfig",
            branchName,
            repo.DefaultBranch)
        {
            Body = prBody
        });
        Console.WriteLine("Created PR in " + repo.FullName);
    }
    catch (Exception ex)
    {
        Console.WriteLine("PR may already exist for " + repo.FullName + ": " + ex.Message);
    }
}

static async Task<(string? Content, string? Sha)> TryGetFileContent(
    GitHubClient client, string owner, string repo, string path)
{
    try
    {
        var contents = await client.Repository.Content.GetAllContents(owner, repo, path);
        if (contents.Count == 0) return (null, null);
        var file = contents[0];
        var content = file.Encoding == "base64"
            ? Encoding.UTF8.GetString(Convert.FromBase64String(
                file.Content.Replace("\r", "").Replace("\n", "")))
            : file.Content;
        return (content, file.Sha);
    }
    catch (NotFoundException)
    {
        return (null, null);
    }
}

static async Task<string?> TryGetFileSha(
    GitHubClient client, string owner, string repo, string path)
{
    try
    {
        var contents = await client.Repository.Content.GetAllContents(owner, repo, path);
        return contents.Count > 0 ? contents[0].Sha : null;
    }
    catch (NotFoundException)
    {
        return null;
    }
}

#:package Octokit@14.*

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Octokit;

var token = Environment.GetEnvironmentVariable("GH_TOKEN")
    ?? throw new InvalidOperationException("GH_TOKEN is not set.");
var sourceRepo = Environment.GetEnvironmentVariable("SOURCE_REPO")
    ?? throw new InvalidOperationException("SOURCE_REPO is not set.");
var branchName = FilePaths.BranchPrefix + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");

var client = new GitHubClient(new ProductHeaderValue("cs-editorconfig-formatter"))
{
    Credentials = new Credentials(token)
};

var installationRepos = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent();
var changed = false;
var tasks = new List<Task>(installationRepos.Repositories.Count);

foreach (var repo in installationRepos.Repositories)
{
    if (repo.FullName == sourceRepo || repo.Archived)
    {
        continue;
    }

    changed = true;
    tasks.Add(ProcessRepositoryWithLogging(client, repo, branchName, token, sourceRepo));
}

await Task.WhenAll(tasks);

if (!changed)
{
    Console.WriteLine("No repositories to format.");
}

static async Task ProcessRepositoryWithLogging(
    GitHubClient client,
    Repository repo,
    string branchName,
    string token,
    string sourceRepo)
{
    Console.WriteLine("::group::Processing " + repo.FullName);
    try
    {
        await ProcessRepository(client, repo, branchName, token, sourceRepo);
    }
    finally
    {
        Console.WriteLine("::endgroup::");
    }
}

static async Task ProcessRepository(
    GitHubClient client,
    Repository repo,
    string branchName,
    string token,
    string sourceRepo)
{
    var owner = repo.Owner.Login;
    var name = repo.Name;
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        // `dotnet format` requires the target repo to be present on the local filesystem.
        // Because the repos to process are discovered dynamically at runtime from the GitHub App
        // installation API, we must clone and format each repo inside this loop.
        // Moving `dotnet format` to the workflow level would require a two-step design
        // (emit repo list → dynamic matrix), which adds significant complexity for no practical gain.
        var cloneUrl = $"https://x-access-token:{token}@github.com/{owner}/{name}.git";
        await RunAsync("git", ["clone", "--depth=1", cloneUrl, "."], tempDir);

        // Exit code 0 = no changes needed, 2 = changes needed, other = error (e.g. no project files)
        var verifyExitCode = await RunAndGetExitCodeAsync(
            "dotnet", ["format", "--verify-no-changes"], tempDir);

        if (verifyExitCode == 0)
        {
            Console.WriteLine($"No formatting changes needed in {repo.FullName}.");
            return;
        }

        if (verifyExitCode != 2)
        {
            Console.WriteLine($"dotnet format exited with code {verifyExitCode} in {repo.FullName}, skipping.");
            return;
        }

        await RunAsync("dotnet", ["format"], tempDir);

        var diffOutput = await RunAndGetOutputAsync("git", ["diff", "--name-only"], tempDir);
        var changedFiles = diffOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (changedFiles.Count == 0)
        {
            Console.WriteLine($"No C# files changed in {repo.FullName} after formatting.");
            return;
        }

        Console.WriteLine($"Formatting changed {changedFiles.Count} C# file(s) in {repo.FullName}.");

        var baseRef = await client.Git.Reference.Get(owner, name, "heads/" + repo.DefaultBranch);
        var baseSha = baseRef.Object.Sha;
        var baseCommit = await client.Git.Commit.Get(owner, name, baseSha);

        var newTreeRequest = new NewTree { BaseTree = baseCommit.Tree.Sha };
        foreach (var filePath in changedFiles)
        {
            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, filePath));
            newTreeRequest.Tree.Add(new NewTreeItem
            {
                Path = filePath,
                Mode = "100644",
                Type = TreeType.Blob,
                Content = content
            });
        }

        var newTree = await client.Git.Tree.Create(owner, name, newTreeRequest);
        var newCommit = await client.Git.Commit.Create(owner, name, new NewCommit(
            "chore: apply dotnet format",
            newTree.Sha,
            new[] { baseSha }));

        await client.Git.Reference.Create(owner, name, new NewReference(
            "refs/heads/" + branchName,
            newCommit.Sha));

        var prBody = $"""
            Automated application of `dotnet format` using the `.editorconfig` from [cs-editorconfig](https://github.com/{sourceRepo}).
            """;

        var pr = await client.PullRequest.Create(owner, name, new NewPullRequest(
            "chore: apply dotnet format",
            branchName,
            repo.DefaultBranch)
        {
            Body = prBody
        });
        Console.WriteLine($"Created PR in {repo.FullName} ({pr.HtmlUrl})");
    }
    finally
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to clean up temp directory: {ex.Message}");
        }
    }
}

static async Task RunAsync(string command, string[] args, string workingDirectory)
{
    var (exitCode, _, stderr) = await RunCoreAsync(command, args, workingDirectory);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"Command '{command} {string.Join(" ", args)}' failed with exit code {exitCode}:\n{stderr}");
    }
}

static async Task<int> RunAndGetExitCodeAsync(string command, string[] args, string workingDirectory)
{
    var (exitCode, _, _) = await RunCoreAsync(command, args, workingDirectory);
    return exitCode;
}

static async Task<string> RunAndGetOutputAsync(string command, string[] args, string workingDirectory)
{
    var (_, stdout, _) = await RunCoreAsync(command, args, workingDirectory);
    return stdout;
}

static async Task<(int ExitCode, string Stdout, string Stderr)> RunCoreAsync(
    string command, string[] args, string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = command,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = new Process { StartInfo = startInfo };
    process.Start();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask);
    return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
}

static class FilePaths
{
    public const string BranchPrefix = "feature/dotnet-format-";
}

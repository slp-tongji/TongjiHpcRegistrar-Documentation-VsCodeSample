#:package CliWrap@3.10.5

using CliWrap;

// 约定命令行参数
var pythonScript = new FileInfo(args[0]);    // 要执行的脚本路径
var environment = new DirectoryInfo(args[1]);    // pyproject.toml 所在的目录
var outputDirectory = new DirectoryInfo(args[2]);    // 输出目录
var partition = args[3];    // 要使用的分区
var count = int.Parse(args[4]);    // 使用的 CPU 或者 GPU 数量（具体是 CPU 还是 GPU 按照分区判断）

var projectName = "sample_project";
var sbatchScript = 
    $"""
    #!/usr/bin/bash
    cd "{environment.FullName}"
    uv run "{pythonScript.FullName}"
    """;
var cpusPerGpu = 7;
string? email = null;

var partitionDictionary = new Dictionary<string, string?>()
{
    { "intel", null },
    { "amd", null },
    { "L40", "l40" },
    { "A800", "a800" },
};
var partitionGpu = partitionDictionary[partition];

var scriptName = Path.GetFileNameWithoutExtension(pythonScript.Name);

var outputPath = new DirectoryInfo(Path.Join(
    outputDirectory.FullName, scriptName,
    DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff")));
outputPath.Create();

var sbatchScriptPath = Path.Join(outputPath.FullName, $"{scriptName}.sh");
File.WriteAllText(sbatchScriptPath, sbatchScript);

var arguments = new Dictionary<string, string?>
{
    // { "exclude", ... },

    { "partition", partition },
    { "nodes", "1" },
    { "ntasks-per-node", "1" },
    { "gres", partitionGpu is null ? null : $"gpu:{partitionGpu}:{count}" },
    { "gpus-per-task", partitionGpu is null ? null : $"{partitionGpu}:{count}" },
    { "cpus-per-task", partitionGpu is null ? $"{count}" : $"{count * cpusPerGpu}" },
    // { "mem-per-cpu", ... },
    // { "mem-per-gpu", ... },

    { "job-name", $"{projectName}_{scriptName}" },
    { "comment", pythonScript.FullName },
    { "output", Path.Join(outputPath.FullName, "%j.out") },
    { "error", Path.Join(outputPath.FullName, "%j.err") },
    { "mail-type", "ALL" },
    { "mail-user", email },
};

using var standardOutput = Console.OpenStandardOutput();
using var standardError = Console.OpenStandardError();

var command = Cli.Wrap("/usr/bin/sbatch").WithArguments(arguments
    .Where(x => x.Value is not null)
    .SelectMany(x => new[] { $"--{x.Key}", $"{x.Value}" })
    .Append(sbatchScriptPath))
    .WithStandardOutputPipe(PipeTarget.ToStream(standardOutput))
    .WithStandardErrorPipe(PipeTarget.ToStream(standardError));
await command.ExecuteAsync();
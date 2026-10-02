#:package CliWrap@3.10.5

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;

// 约定命令行参数
var pythonScript = new FileInfo(args[0]);    // 要执行的脚本路径
var environment = new DirectoryInfo(args[1]);    // pyproject.toml 所在的目录
var launchJson = new FileInfo(args[2]);    // 生成的 launch.json 位置
var sshScriptFileDirectory = new DirectoryInfo(args[3]);    // 临时脚本目录
var partition = args[4];    // 要使用的分区
var count = int.Parse(args[5]);    // 使用的 CPU 或者 GPU 数量（具体是 CPU 还是 GPU 按照分区判断）

var projectName = "sample_project";
var cpusPerGpu = 7;

var port = Random.Shared.Next(10000, 65536);
var sshScript =
    $"""
    #!/usr/bin/bash
    cd "{environment.FullName}"

    # 由于是使用 SSH 访问目标节点，不会保留原本的环境变量。
    # 例如，如果使用 Conda，需要在这里激活环境。

    echo "服务即将启动，请按 F5 开始远程调试"
    echo "================================="
    uv run python -X frozen_modules=off -m debugpy --listen 0.0.0.0:{port} --wait-for-client "{pythonScript.FullName}"
    echo "================================="
    """;

var partitionDictionary = new Dictionary<string, string?>()
{
    { "intel", null },
    { "amd", null },
    { "L40", "l40" },
    { "A800", "a800" },
};
var partitionGpu = partitionDictionary[partition];

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

    { "job-name", $"{projectName}_{Path.GetFileNameWithoutExtension(pythonScript.Name)}" },
    { "comment", pythonScript.FullName },
};

using var standardOutput = Console.OpenStandardOutput();
using var standardError = Console.OpenStandardError();

Console.WriteLine("正在申请资源……");
var sallocOutputBuilder = new StringBuilder();
var sallocCommand = Cli.Wrap("/usr/bin/salloc").WithArguments(arguments
    .Where(x => x.Value != null)
    .SelectMany(x => new[] { $"--{x.Key}", $"{x.Value}" })
    .Append("--no-shell"));
sallocCommand = sallocCommand.WithStandardOutputPipe(PipeTarget.Merge(
    PipeTarget.ToStream(standardOutput),
    PipeTarget.ToStringBuilder(sallocOutputBuilder)));
sallocCommand = sallocCommand.WithStandardErrorPipe(PipeTarget.Merge(
    PipeTarget.ToStream(standardError),
    PipeTarget.ToStringBuilder(sallocOutputBuilder)));
await sallocCommand.ExecuteAsync();
var sallocOutput = sallocOutputBuilder.ToString();

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
var match = Regex.Match(sallocOutput, @"Granted job allocation (\d+)");
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

if (!match.Success)
{
    Console.WriteLine("未能成功解析作业 ID。程序将退出。");
    Console.WriteLine("请注意！salloc 指示成功但未发现作业 ID。您可能需要手动取消作业！");
    return;
}

string jobId = match.Groups[1].Value;
try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };

    Console.WriteLine("正在解析节点名称……");
#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
    match = Regex.Match(sallocOutput, @"Nodes\s+(.+)\s+are ready for job");
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
    if (!match.Success)
    {
        Console.WriteLine("未能成功解析节点名称。程序将退出。");
        return;
    }
    string nodeName = match.Groups[1].Value;

    if (cancellation.IsCancellationRequested)
    {
        Console.WriteLine("已取消。");
        return;
    }

    var scontrolOutput = new StringBuilder();
    var scontrol = await (Cli.Wrap("/usr/bin/scontrol")
        .WithArguments(["show", "hostnames", nodeName]) |
        scontrolOutput).ExecuteAsync(cancellation.Token);
    var possibleHosts = scontrolOutput.ToString().Split(
        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    if (possibleHosts.Length != 1)
    {
        Console.WriteLine("未能成功解析节点名称。程序将退出。");
        return;
    }
    var host = possibleHosts[0];

    Console.WriteLine("正在写入 launch.json ……");
    await File.WriteAllTextAsync(launchJson.FullName,
        $$"""
        {
            "version": "0.2.0",
            "configurations": [
                {
                    "name": "Python Debugger: {{host}}:{{port}}",
                    "type": "debugpy",
                    "request": "attach",
                    "connect": {
                        "host": "{{host}}",
                        "port": {{port}}
                    }
                }
            ]
        }
        """, cancellation.Token);

    sshScriptFileDirectory.Create();
    var sshScriptFile = Path.Combine(sshScriptFileDirectory.FullName, $"ssh_script_{port}.sh");
    Console.WriteLine($"正在写入 {sshScriptFile} ……");
    await File.WriteAllTextAsync(sshScriptFile, sshScript, cancellation.Token);
    Debug.Assert(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
    File.SetUnixFileMode(sshScriptFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    Console.WriteLine("正在启动……");
    var sshCommand = Cli.Wrap("/usr/bin/ssh")
        .WithArguments([
            "-F", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"),
            "-o", "StrictHostKeyChecking=accept-new",
            host,
            sshScriptFile
        ])
        .WithStandardOutputPipe(PipeTarget.ToStream(standardOutput))
        .WithStandardErrorPipe(PipeTarget.ToStream(standardError));
    await sshCommand.ExecuteAsync(cancellation.Token);
}
finally
{
    try
    {
        await Cli.Wrap("/usr/bin/scancel").WithArguments([jobId]).ExecuteAsync();
        Console.WriteLine("已结束作业。");
    }
    catch
    {
        Console.WriteLine("请注意！作业取消失败。您可能需要手动取消作业！");
    }
}
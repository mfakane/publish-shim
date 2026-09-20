using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

internal static class Program
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint StillActive = 259;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: PublishShim.IntegrationTests <PublishShim.MSBuild.nupkg>");
            return 2;
        }

        var packagePath = Path.GetFullPath(args[0]);
        var packageDirectory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("The package directory could not be resolved.");
        var testRoot = Path.Combine(Path.GetTempPath(), "PublishShim.IntegrationTests", "path with spaces-日本語", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var runner = new IntegrationTestRunner(packagePath, packageDirectory, testRoot);
            await runner.RunAsync();
            Console.WriteLine("PublishShim integration tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (string.Equals(Environment.GetEnvironmentVariable("PUBLISH_SHIM_KEEP_TEST_OUTPUT"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Test output was kept at '{testRoot}'.");
            }
            else
            {
                try
                {
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        try
                        {
                            Directory.Delete(testRoot, recursive: true);
                            break;
                        }
                        catch when (attempt < 29)
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
                catch
                {
                    Console.Error.WriteLine($"Warning: test output was left at '{testRoot}'.");
                }
            }
        }
    }

    private sealed class IntegrationTestRunner
    {
        private readonly string packagePath;
        private readonly string packageDirectory;
        private readonly string testRoot;
        private readonly string dotnetPath;
        private readonly string packageVersion;

        public IntegrationTestRunner(string packagePath, string packageDirectory, string testRoot)
        {
            this.packagePath = packagePath;
            this.packageDirectory = packageDirectory;
            this.testRoot = testRoot;
            dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var packageFileName = Path.GetFileNameWithoutExtension(packagePath);
            const string packagePrefix = "PublishShim.MSBuild.";
            packageVersion = packageFileName.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)
                ? packageFileName[packagePrefix.Length..]
                : throw new ArgumentException($"Unexpected package name '{packageFileName}'.", nameof(packagePath));
        }

        public async Task RunAsync()
        {
            await RunScenarioAsync("disabled", publishShim: false, outputType: "Exe", kind: null, shimDirectory: null, verify: VerifyDisabledAsync);
            await RunScenarioAsync("auto-console", publishShim: true, outputType: "Exe", kind: "Auto", shimDirectory: null, verify: VerifyConsoleAsync);
            await RunScenarioAsync("native-aot", publishShim: true, outputType: "Exe", kind: "Auto", shimDirectory: null, verify: VerifyNativeAotAsync, publishAot: true, repeatPublish: false);
            await RunScenarioAsync("custom-exe", publishShim: true, outputType: "Exe", kind: "Exe", shimDirectory: "payload", verify: VerifyConsoleAsync);
            await RunScenarioAsync("winexe", publishShim: true, outputType: "WinExe", kind: "WinExe", shimDirectory: null, verify: VerifyWinExeAsync);
            await RunScenarioAsync("multiple-entrypoints", publishShim: false, outputType: "Exe", kind: null, shimDirectory: null, verify: VerifyMultipleEntryPointsAsync, repeatPublish: false, customTargets: CreateMultipleEntryPointTargets());
        }

        private async Task RunScenarioAsync(
            string name,
            bool publishShim,
            string outputType,
            string? kind,
            string? shimDirectory,
            Func<string, string, string, Task> verify,
            bool publishAot = false,
            bool repeatPublish = true,
            string? customTargets = null)
        {
            var scenarioDirectory = Path.Combine(testRoot, name);
            Directory.CreateDirectory(scenarioDirectory);
            var projectName = $"PublishShimTest_{name}";
            var projectPath = Path.Combine(scenarioDirectory, $"{projectName}.csproj");
            var publishDirectory = Path.Combine(scenarioDirectory, "publish");
            var outputPath = Path.Combine(scenarioDirectory, "出力", "sample-output.txt");

            await File.WriteAllTextAsync(projectPath, CreateProject(projectName, publishShim, outputType, kind, shimDirectory, publishAot, customTargets));
            await File.WriteAllTextAsync(Path.Combine(scenarioDirectory, "Program.cs"), CreateSampleProgram());
            await File.WriteAllTextAsync(Path.Combine(scenarioDirectory, "NuGet.Config"), CreateNuGetConfig(publishAot));

            RunDotnet(scenarioDirectory, "restore", projectPath, "--configfile", Path.Combine(scenarioDirectory, "NuGet.Config"));
            RunDotnet(scenarioDirectory, "publish", projectPath, "--configuration", "Release", "--no-restore", "--output", publishDirectory);

            await verify(publishDirectory, projectName, outputPath);

            if (repeatPublish && (publishShim || customTargets is not null))
            {
                RunDotnet(scenarioDirectory, "publish", projectPath, "--configuration", "Release", "--no-restore", "--output", publishDirectory);
                await verify(publishDirectory, projectName, outputPath);
            }
        }

        private async Task VerifyDisabledAsync(string publishDirectory, string projectName, string outputPath)
        {
            var executable = Path.Combine(publishDirectory, $"{projectName}.exe");
            Assert(File.Exists(executable), "PublishShim=false must leave the application executable in the publish root.");
            Assert(!Directory.Exists(Path.Combine(publishDirectory, ".app")), "PublishShim=false must not create .app.");
            await Task.CompletedTask;
        }

        private Task VerifyNativeAotAsync(string publishDirectory, string projectName, string outputPath)
        {
            return VerifyConsoleCoreAsync(publishDirectory, projectName, outputPath, verifyUnusualArgv0: false);
        }

        private Task VerifyConsoleAsync(string publishDirectory, string projectName, string outputPath)
        {
            return VerifyConsoleCoreAsync(publishDirectory, projectName, outputPath, verifyUnusualArgv0: true);
        }

        private async Task VerifyConsoleCoreAsync(string publishDirectory, string projectName, string outputPath, bool verifyUnusualArgv0)
        {
            var executable = Path.Combine(publishDirectory, $"{projectName}.exe");
            var application = Path.Combine(publishDirectory, "payload", $"{projectName}.exe");
            var defaultApplication = Path.Combine(publishDirectory, ".app", $"{projectName}.exe");
            var actualApplication = File.Exists(application) ? application : defaultApplication;

            var publishedFiles = string.Join(", ", Directory.Exists(publishDirectory)
                ? Directory.GetFiles(publishDirectory, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(publishDirectory, path))
                : Array.Empty<string>());
            Assert(File.Exists(executable), $"The generated console shim is missing. Publish output: {publishedFiles}");
            Assert(File.Exists(actualApplication), "The relocated console application is missing.");
            Assert(!string.Equals(executable, actualApplication, StringComparison.OrdinalIgnoreCase), "The shim and application paths must differ.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var result = RunProcess(
                executable,
                publishDirectory,
                new[] { "--exit", "37", "argument with spaces", string.Empty, "ユニコード", "--stdio" },
                new Dictionary<string, string?>
                {
                    ["PUBLISH_SHIM_SAMPLE_OUTPUT"] = outputPath,
                    ["PUBLISH_SHIM_ROOT"] = "stale-root-value",
                    ["PUBLISH_SHIM_EXE"] = "stale-exe-value"
                },
                redirectOutput: true);

            Assert(result.ExitCode == 37, $"The console shim must propagate the child exit code. Actual: {result.ExitCode}.");
            Assert(result.StdOut.Contains("stdout-marker", StringComparison.Ordinal), "Console stdout was not inherited by the child.");
            Assert(result.StdErr.Contains("stderr-marker", StringComparison.Ordinal), "Console stderr was not inherited by the child.");

            var output = await File.ReadAllTextAsync(outputPath);
            Assert(output.Contains($"PublishShimRoot={Path.GetFullPath(publishDirectory)}", StringComparison.OrdinalIgnoreCase), "The console shim must expose its publish root through PUBLISH_SHIM_ROOT.");
            Assert(output.Contains($"PublishShimExe={executable}", StringComparison.OrdinalIgnoreCase), "The console shim must expose its executable path through PUBLISH_SHIM_EXE.");
            Assert(output.Contains("Arg[2]=argument with spaces", StringComparison.Ordinal), "The console shim lost a quoted argument.");
            Assert(output.Contains("Arg[3]=\n", StringComparison.Ordinal) || output.Contains("Arg[3]=\r\n", StringComparison.Ordinal), "The console shim lost an empty argument.");
            Assert(output.Contains("Arg[4]=ユニコード", StringComparison.Ordinal), "The console shim lost a Unicode argument.");

            if (verifyUnusualArgv0)
            {
                var unusualArgv0Result = RunWithCustomCommandLine(
                    executable,
                    publishDirectory,
                    "\"shim\\\"with-a-quote\" --exit 41",
                    new Dictionary<string, string?>
                    {
                        ["PUBLISH_SHIM_SAMPLE_OUTPUT"] = outputPath,
                        ["PUBLISH_SHIM_ROOT"] = "stale-root-value",
                        ["PUBLISH_SHIM_EXE"] = "stale-exe-value"
                    });
                Assert(unusualArgv0Result == 41, $"The console shim must preserve the argument tail with unusual argv[0] quoting. Actual: {unusualArgv0Result}.");
            }
        }

        private async Task VerifyWinExeAsync(string publishDirectory, string projectName, string outputPath)
        {
            var executable = Path.Combine(publishDirectory, $"{projectName}.exe");
            var application = Path.Combine(publishDirectory, ".app", $"{projectName}.exe");

            Assert(File.Exists(executable), "The generated GUI shim is missing.");
            Assert(File.Exists(application), "The relocated GUI application is missing.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var stopwatch = Stopwatch.StartNew();
            var result = RunProcess(
                executable,
                publishDirectory,
                Array.Empty<string>(),
                new Dictionary<string, string?>
                {
                    ["PUBLISH_SHIM_SAMPLE_OUTPUT"] = outputPath,
                    ["PUBLISH_SHIM_ROOT"] = "stale-root-value",
                    ["PUBLISH_SHIM_EXE"] = "stale-exe-value"
                },
                redirectOutput: false);
            stopwatch.Stop();

            Assert(result.ExitCode == 0, $"The GUI shim must exit successfully after spawning the child. Actual: {result.ExitCode}.");
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "The GUI shim must not wait for the child process.");
            await WaitForFileAsync(outputPath);
            var output = await File.ReadAllTextAsync(outputPath);
            Assert(output.Contains($"PublishShimRoot={Path.GetFullPath(publishDirectory)}", StringComparison.OrdinalIgnoreCase), "The GUI shim must expose its publish root through PUBLISH_SHIM_ROOT.");
            Assert(output.Contains($"PublishShimExe={executable}", StringComparison.OrdinalIgnoreCase), "The GUI shim must expose its executable path through PUBLISH_SHIM_EXE.");
        }

        private async Task VerifyMultipleEntryPointsAsync(string publishDirectory, string projectName, string outputPath)
        {
            var cliExecutable = Path.Combine(publishDirectory, $"{projectName}-cli.exe");
            var guiExecutable = Path.Combine(publishDirectory, $"{projectName}-gui.exe");
            var application = Path.Combine(publishDirectory, ".app", $"{projectName}.exe");
            var cliOutputPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "multiple-cli.txt");
            var guiOutputPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "multiple-gui.txt");

            Assert(File.Exists(cliExecutable), "The CLI entry point shim is missing.");
            Assert(File.Exists(guiExecutable), "The GUI entry point shim is missing.");
            Assert(File.Exists(application), "The shared application executable is missing.");
            Assert(!File.Exists(Path.Combine(publishDirectory, $"{projectName}.exe")), "The custom entry point target must not leave the original executable in the publish root.");

            var cliResult = RunProcess(
                cliExecutable,
                publishDirectory,
                new[] { "--exit", "43" },
                new Dictionary<string, string?>
                {
                    ["PUBLISH_SHIM_SAMPLE_OUTPUT"] = cliOutputPath,
                    ["PUBLISH_SHIM_ROOT"] = "stale-root-value",
                    ["PUBLISH_SHIM_EXE"] = "stale-exe-value"
                },
                redirectOutput: true);

            Assert(cliResult.ExitCode == 43, $"The CLI entry point must propagate the child exit code. Actual: {cliResult.ExitCode}.");
            var cliOutput = await File.ReadAllTextAsync(cliOutputPath);
            Assert(cliOutput.Contains($"PublishShimRoot={Path.GetFullPath(publishDirectory)}", StringComparison.OrdinalIgnoreCase), "The CLI entry point must expose the shared publish root.");
            Assert(cliOutput.Contains($"PublishShimExe={cliExecutable}", StringComparison.OrdinalIgnoreCase), "The CLI entry point must expose its own shim path.");

            var guiResult = RunProcess(
                guiExecutable,
                publishDirectory,
                Array.Empty<string>(),
                new Dictionary<string, string?>
                {
                    ["PUBLISH_SHIM_SAMPLE_OUTPUT"] = guiOutputPath,
                    ["PUBLISH_SHIM_ROOT"] = "stale-root-value",
                    ["PUBLISH_SHIM_EXE"] = "stale-exe-value"
                },
                redirectOutput: false);

            Assert(guiResult.ExitCode == 0, $"The GUI entry point must exit successfully after spawning the child. Actual: {guiResult.ExitCode}.");
            await WaitForFileAsync(guiOutputPath);
            var guiOutput = await File.ReadAllTextAsync(guiOutputPath);
            Assert(guiOutput.Contains($"PublishShimRoot={Path.GetFullPath(publishDirectory)}", StringComparison.OrdinalIgnoreCase), "The GUI entry point must expose the shared publish root.");
            Assert(guiOutput.Contains($"PublishShimExe={guiExecutable}", StringComparison.OrdinalIgnoreCase), "The GUI entry point must expose its own shim path.");
        }

        private string CreateProject(string projectName, bool publishShim, string outputType, string? kind, string? shimDirectory, bool publishAot, string? customTargets = null)
        {
            var kindProperty = kind is null ? string.Empty : $"\n    <PublishShimKind>{kind}</PublishShimKind>";
            var directoryProperty = shimDirectory is null ? string.Empty : $"\n    <PublishShimDirectory>{shimDirectory}</PublishShimDirectory>";
            var aotProperty = publishAot ? "\n    <PublishAot>true</PublishAot>" : string.Empty;
            var trimmedProperty = $"{publishAot.ToString().ToLowerInvariant()}";
            return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>{outputType}</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishTrimmed>{trimmedProperty}</PublishTrimmed>
    <PublishShim>{publishShim.ToString().ToLowerInvariant()}</PublishShim>{kindProperty}{directoryProperty}{aotProperty}
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""PublishShim.MSBuild"" Version=""{packageVersion}"" />
  </ItemGroup>
{customTargets}
</Project>
";
        }

        private static string CreateMultipleEntryPointTargets() => """
  <Target Name="GenerateMultiplePublishShims" AfterTargets="Publish">
    <RelocatePublishArtifactsTask
        PublishDirectory="$(PublishDir)"
        TargetExecutableName="$(AssemblyName).exe"
        ShimDirectory=".app">
      <Output TaskParameter="ActualApplicationRelativePath"
              PropertyName="_PublishShimActualApplicationRelativePath" />
    </RelocatePublishArtifactsTask>
    <GeneratePublishShimTask
        PublishDirectory="$(PublishDir)"
        ShimExecutableName="$(AssemblyName)-cli.exe"
        TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
        PublishShimKind="Exe"
        RuntimeIdentifier="$(RuntimeIdentifier)"
        NativeShimPath="$(PublishShimNativeShimPath)"
        NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
    <GeneratePublishShimTask
        PublishDirectory="$(PublishDir)"
        ShimExecutableName="$(AssemblyName)-gui.exe"
        TargetRelativePath="$(_PublishShimActualApplicationRelativePath)"
        PublishShimKind="WinExe"
        RuntimeIdentifier="$(RuntimeIdentifier)"
        NativeShimPath="$(PublishShimNativeShimPath)"
        NativeShimDirectory="$(PublishShimNativeShimDirectory)" />
  </Target>
""";

        private static string CreateSampleProgram() => """
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--stdio", StringComparer.Ordinal))
        {
            Console.Write("stdout-marker");
            Console.Error.Write("stderr-marker");
        }

        var outputPath = Environment.GetEnvironmentVariable("PUBLISH_SHIM_SAMPLE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var lines = new List<string>
            {
                $"ArgumentCount={args.Length}",
                $"PublishShimRoot={Environment.GetEnvironmentVariable("PUBLISH_SHIM_ROOT")}",
                $"PublishShimExe={Environment.GetEnvironmentVariable("PUBLISH_SHIM_EXE")}"
            };
            for (var index = 0; index < args.Length; index++)
            {
                lines.Add($"Arg[{index}]={args[index]}");
            }

            File.WriteAllLines(outputPath, lines, Encoding.UTF8);
        }

        if (args.Length >= 2 && string.Equals(args[0], "--exit", StringComparison.Ordinal))
        {
            return int.Parse(args[1]);
        }

        return 0;
    }
}
""";

        private string CreateNuGetConfig(bool publishAot) => $@"<configuration>
  <packageSources>
    <clear />
    <add key=""local"" value=""{SecurityElement.Escape(packageDirectory)}"" />
    {GetAotPackageSource(publishAot)}
  </packageSources>
</configuration>
";

        private static string GetAotPackageSource(bool publishAot)
        {
            return publishAot
                ? "<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />"
                : string.Empty;
        }

        private ProcessResult RunDotnet(string workingDirectory, params string[] arguments)
        {
            var result = RunProcess(dotnetPath, workingDirectory, arguments, environment: null, redirectOutput: true);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Command failed ({result.ExitCode}): {dotnetPath} {string.Join(' ', arguments)}\n{result.StdOut}\n{result.StdErr}");
            }

            return result;
        }

        private static ProcessResult RunProcess(
            string fileName,
            string workingDirectory,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            bool redirectOutput)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = redirectOutput,
                    RedirectStandardError = redirectOutput,
                    CreateNoWindow = true,
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    process.StartInfo.Environment[key] = value;
                }
            }

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{fileName}'.");
            }

            var standardOutput = redirectOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var standardError = redirectOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"'{fileName}' did not exit within the test timeout.");
            }

            Task.WaitAll(standardOutput, standardError);
            var result = new ProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
            if (process.ExitCode != 0 && string.Equals(Path.GetFileName(fileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Command failed ({process.ExitCode}): dotnet {string.Join(' ', arguments)}\n{result.StdOut}\n{result.StdErr}");
            }

            return result;
        }

        private static int RunWithCustomCommandLine(
            string applicationName,
            string workingDirectory,
            string commandLine,
            IReadOnlyDictionary<string, string?> environment)
        {
            var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var processInformation = default(ProcessInformation);
            var mutableCommandLine = new StringBuilder(commandLine);
            var previousEnvironment = new Dictionary<string, string?>();

            foreach (var (key, value) in environment)
            {
                previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }

            try
            {
                if (!CreateProcess(
                        applicationName,
                        mutableCommandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for the custom argv[0] test.");
                }

                try
                {
                    if (WaitForSingleObject(processInformation.hProcess, 120_000) == Infinite)
                    {
                        throw new TimeoutException("The custom argv[0] process did not exit within the test timeout.");
                    }

                    if (!GetExitCodeProcess(processInformation.hProcess, out var exitCode))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed.");
                    }

                    return unchecked((int)exitCode);
                }
                finally
                {
                    CloseHandle(processInformation.hThread);
                    CloseHandle(processInformation.hProcess);
                }
            }
            finally
            {
                foreach (var (key, value) in previousEnvironment)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        private static async Task WaitForFileAsync(string path)
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException($"The GUI child did not create '{path}'.");
                }

                await Task.Delay(50);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int processId;
            public int threadId;
        }
    }
}

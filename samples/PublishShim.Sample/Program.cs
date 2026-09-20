using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outputPath = Environment.GetEnvironmentVariable("PUBLISH_SHIM_SAMPLE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var lines = new List<string>
            {
                $"ProcessPath={Environment.ProcessPath}",
                $"CurrentDirectory={Environment.CurrentDirectory}",
                $"ArgumentCount={args.Length}"
            };

            for (var index = 0; index < args.Length; index++)
            {
                lines.Add($"Arg[{index}]={args[index]}");
            }

            File.WriteAllLines(outputPath, lines, Encoding.UTF8);
        }

        if (args.Length >= 2 && string.Equals(args[0], "--exit", StringComparison.Ordinal))
        {
            return int.TryParse(args[1], out var exitCode) ? exitCode : 1;
        }

        return 0;
    }
}

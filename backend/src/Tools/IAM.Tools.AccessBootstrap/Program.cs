using Microsoft.Extensions.Configuration;

namespace IAM.Tools.AccessBootstrap;

/// <summary>Entry point. A named class (not top-level statements) so it doesn't clash with the API's Program in tests.</summary>
internal static class AccessBootstrapProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return await BootstrapTool.RunAsync(args, configuration, Console.Out, cancellation.Token).ConfigureAwait(false);
    }
}

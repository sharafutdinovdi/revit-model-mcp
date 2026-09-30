namespace RevitModelMcp.BatchSupervisor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0])) return 2;
        try
        {
            var store = new BatchRunStore(args[0]);
            using var lease = store.AcquireLease();
            await new BatchSupervisor(store, new FileChannelWorkerClient(),
                new RevitServerClient(), () => DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);
            return 0;
        }
        catch (IOException) { return 3; }
        catch (UnauthorizedAccessException) { return 4; }
        catch (InvalidOperationException) { return 5; }
    }
}

using System.Diagnostics;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.BatchSupervisor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0])) return 2;
        var ownsLease = false;
        try
        {
            var store = new BatchRunStore(args[0]);
            using var lease = store.AcquireLease();
            ownsLease = true;
            using var process = Process.GetCurrentProcess();
            var run = store.Read();
            store.Write(run with
            {
                SupervisorProcessId = process.Id,
                SupervisorProcessStartedUtc = process.StartTime.ToUniversalTime().ToString("O")
            });
            await new BatchSupervisor(store, new FileChannelWorkerClient(),
                new RevitServerClient(), () => DateTimeOffset.UtcNow).RunAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            if (ownsLease)
            {
                try
                {
                    var store = new BatchRunStore(args[0]);
                    store.Write(BatchStatePolicy.FailSupervisor(store.Read(),
                        $"Batch supervisor failed ({exception.GetType().Name})."));
                }
                catch (Exception persistenceException)
                {
                    Console.Error.WriteLine($"Batch supervisor could not persist failure ({persistenceException.GetType().Name}).");
                }
            }
            return exception switch
            {
                IOException => 3,
                UnauthorizedAccessException => 4,
                InvalidOperationException => 5,
                _ => 6
            };
        }
    }
}

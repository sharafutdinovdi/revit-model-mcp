using System.Diagnostics;
using System.Text.Json;
using RevitModelMcp.BatchSupervisor;

namespace RevitModelMcp.Core.Tests.Batch
{
    public sealed class FileChannelWorkerClientTests
    {
        [Test]
        public async Task MatchesHeartbeat_RetriesLockedFile()
        {
            var path = NewHeartbeatPath();
            var startedUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(path, HeartbeatJson(1234, startedUtc));
            using var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var release = Task.Run(async () =>
            {
                await Task.Delay(200);
                lockedFile.Dispose();
            });
            try
            {
                await Assert.That(FileChannelWorkerClient.MatchesHeartbeat(path, 1234, startedUtc.ToString("O"), true)).IsTrue();
            }
            finally
            {
                await release;
                File.Delete(path);
            }
        }

        [Test]
        public async Task MatchesHeartbeat_RetriesMissingFile()
        {
            var path = NewHeartbeatPath();
            var temporary = NewHeartbeatPath();
            var startedUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(temporary, HeartbeatJson(1234, startedUtc));
            var publish = Task.Run(async () =>
            {
                await Task.Delay(200);
                File.Move(temporary, path);
            });
            try
            {
                await Assert.That(FileChannelWorkerClient.MatchesHeartbeat(path, 1234, startedUtc.ToString("O"), true)).IsTrue();
            }
            finally
            {
                await publish;
                File.Delete(path);
                File.Delete(temporary);
            }
        }

        [Test]
        public async Task MatchesHeartbeat_RejectsMissingFileAfterRetries()
        {
            var path = NewHeartbeatPath();
            await Assert.That(FileChannelWorkerClient.MatchesHeartbeat(path, 1234, DateTimeOffset.UtcNow.ToString("O"), true)).IsFalse();
        }

        [Test]
        public async Task MatchesHeartbeat_RejectsDifferentProcessIdWithoutRetry()
        {
            var path = NewHeartbeatPath();
            var startedUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(path, HeartbeatJson(4321, startedUtc));
            try
            {
                var stopwatch = Stopwatch.StartNew();
                await Assert.That(FileChannelWorkerClient.MatchesHeartbeat(path, 1234, startedUtc.ToString("O"), true)).IsFalse();
                await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(300));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task MatchesHeartbeat_RejectsDifferentStartTime()
        {
            var path = NewHeartbeatPath();
            var startedUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(path, HeartbeatJson(1234, startedUtc.AddMinutes(-1)));
            try
            {
                await Assert.That(FileChannelWorkerClient.MatchesHeartbeat(path, 1234, startedUtc.ToString("O"), true)).IsFalse();
            }
            finally { File.Delete(path); }
        }

        private static string NewHeartbeatPath() =>
            Path.Combine(Path.GetTempPath(), $"instance_{Guid.NewGuid():N}.json");

        private static string HeartbeatJson(int processId, DateTimeOffset startedUtc) =>
            JsonSerializer.Serialize(new
            {
                processId,
                startedUtc = startedUtc.ToString("O"),
                updatedUtc = DateTimeOffset.UtcNow.ToString("O")
            });
    }
}

namespace System.Web.Script.Serialization
{
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }

        public T Deserialize<T>(string contents)
        {
            using var document = JsonDocument.Parse(contents);
            var values = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind is JsonValueKind.Number
                    ? (object)property.Value.GetInt32()
                    : property.Value.GetString()!);
            return (T)(object)values;
        }

        public string Serialize(object value) => JsonSerializer.Serialize(value);
    }
}

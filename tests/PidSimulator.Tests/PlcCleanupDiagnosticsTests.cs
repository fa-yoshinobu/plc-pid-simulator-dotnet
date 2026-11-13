using PidSimulator.Core;
using PidSimulator.Core.Plc;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class PlcCleanupDiagnosticsTests
{
    [Fact]
    public async Task UnexpectedClientDisposalFailureIsRecordedWithoutPreventingShutdown()
    {
        using var folder = new TestFolder();
        using var cache = new TestCache(new Connection(new IOException("client disposal failed")), new DiagnosticLog(folder.LogPath));
        Assert.True((await cache.ConnectAndWaitAsync()).Ok);
        cache.Dispose();
        string text = File.ReadAllText(Path.Combine(folder.LogPath, "pid.log"));
        Assert.Contains("PLC通信クライアントの破棄に失敗", text);
        Assert.Contains("client disposal failed", text);
        Assert.Equal(PlcConnectionState.Disconnected, cache.Status.State);
    }

    [Fact]
    public async Task CancellationDuringClientDisposalIsAnExpectedStop()
    {
        using var folder = new TestFolder();
        using var cache = new TestCache(new Connection(new OperationCanceledException()), new DiagnosticLog(folder.LogPath));
        Assert.True((await cache.ConnectAndWaitAsync()).Ok);
        cache.Dispose();
        Assert.False(Directory.Exists(folder.LogPath));
        Assert.Equal(PlcConnectionState.Disconnected, cache.Status.State);
    }

    [Fact]
    public void UnexpectedLoopFailureIsRecordedWhenDisposing()
    {
        using var folder = new TestFolder();
        using var cache = new TestCache(new Connection(), new DiagnosticLog(folder.LogPath), failDescription: true);
        cache.Connect();
        Assert.True(cache.DescriptionFailed.Wait(TimeSpan.FromSeconds(5)));
        cache.Dispose();
        string text = File.ReadAllText(Path.Combine(folder.LogPath, "pid.log"));
        Assert.Contains("PLC通信ループの終了待ちに失敗", text);
        Assert.Contains("loop description failed", text);
        cache.DescriptionFailed.Dispose();
    }

    private sealed class Connection(Exception? disposalError = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => disposalError == null ? ValueTask.CompletedTask : ValueTask.FromException(disposalError);
    }

    private sealed class TestCache(Connection connection, DiagnosticLog diagnostics, bool failDescription = false)
        : CachedPlcClient<Connection>(new PlcSettings { TimeoutMs = 200, CommCycleMs = 20 }, "test", diagnostics: diagnostics)
    {
        public ManualResetEventSlim DescriptionFailed { get; } = new();
        protected override Task<Connection> OpenClientAsync(CancellationToken ct) => failDescription
            ? Task.FromException<Connection>(new IOException("open failed")) : Task.FromResult(connection);
        protected override Task<object> ReadValueAsync(Connection client, string address, string suffix, CancellationToken ct)
            => Task.FromResult<object>((short)42);
        protected override Task<IReadOnlyDictionary<string, object>> ReadValuesAsync(Connection client, string[] keys, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, object>>(new Dictionary<string, object>());
        protected override Task WriteValueAsync(Connection client, string address, string suffix, double raw, CancellationToken ct) => Task.CompletedTask;
        protected override Task WriteValuesAsync(Connection client, IReadOnlyDictionary<string, object> updates, CancellationToken ct) => Task.CompletedTask;
        protected override bool IsPointError(Exception error) => error is ArgumentException;
        protected override string DescribeError(Exception error)
        {
            if (!failDescription) return error.Message;
            DescriptionFailed.Set();
            throw new InvalidOperationException("loop description failed");
        }
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("pid-cleanup-tests-").FullName;
        public string LogPath => Path.Combine(_path, "logs");
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}

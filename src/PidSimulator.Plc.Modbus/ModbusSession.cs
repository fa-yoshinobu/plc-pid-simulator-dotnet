using AMWD.Protocols.Modbus.Common;
using AMWD.Protocols.Modbus.Tcp;
using PidSimulator.Core.Project;

namespace PidSimulator.Plc.Modbus;

/// <summary>通信と手動テストを直列化し、期限切れ・キャンセル後の接続を再利用しない。</summary>
public sealed class ModbusSession : IAsyncDisposable
{
    private readonly ModbusTcpClient _client;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _retired;

    internal ModbusSession(PlcSettings settings)
    {
        _timeout = TimeSpan.FromMilliseconds(Math.Max(1, settings.TimeoutMs));
        _client = new ModbusTcpClient(settings.Host.Trim(), settings.Port)
        {
            ConnectTimeout = _timeout, ReadTimeout = _timeout, WriteTimeout = _timeout,
            IdleTimeout = Timeout.InfiniteTimeSpan,
        };
    }

    public bool IsUsable => Volatile.Read(ref _retired) == 0;

    internal async Task<T> ExecuteAsync<T>(Func<ModbusTcpClient, CancellationToken, Task<T>> action, bool write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsUsable) throw new IOException("Modbus接続が閉じています。");
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token, _lifetime.Token);
        bool acquired = false;
        bool started = false;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            if (!IsUsable) throw new IOException("Modbus接続が閉じています。");
            linked.Token.ThrowIfCancellationRequested();
            // ライブラリ内の通信待ちにも期限を伝え、呼出側も期限を超えて待たない。
            // 初回要求時の同期DNS解決もUIスレッドで実行しない。
            var operation = Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                started = true;
                return action(_client, linked.Token);
            }, linked.Token);
            return await operation.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (ModbusException ex) when (ex.ErrorCode != ModbusErrorCode.NoError)
        {
            // 機器が返した例外応答。接続は保持し、この点だけを異常にする。
            throw;
        }
        catch (Exception ex)
        {
            _ = Retire();
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Modbus通信をキャンセルしました。", ex, ct);
            Exception cause = deadline.IsCancellationRequested
                ? new TimeoutException("Modbus応答がタイムアウトしました。", ex) : ex;
            if (write && started) throw new ModbusWriteOutcomeUnknownException(cause);
            throw cause;
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private Task Retire()
    {
        lock (_disposeLock)
        {
            if (_disposeTask != null) return _disposeTask;
            Volatile.Write(ref _retired, 1);
            // AMWDの完了通知とDisposeを同じ呼出しスタックで実行しない。
            _disposeTask = Task.Run(async () =>
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                try { _client.Dispose(); }
                finally
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try { _lifetime.Dispose(); }
                    finally { _gate.Release(); }
                }
            });
            return _disposeTask;
        }
    }

    public ValueTask DisposeAsync() => new(Retire());
}

/// <summary>書込要求の送信開始後に、有効な応答を取得できなかった。</summary>
internal sealed class ModbusWriteOutcomeUnknownException(Exception inner)
    : IOException("Modbus書込み結果が不明です。", inner);

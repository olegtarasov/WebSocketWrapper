using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WebSocketWrapper
{
    /// <summary>
    /// A thin wrapper on top of <see cref="ClientWebSocket"/> with <see cref="IAsyncEnumerable{T}"/> receiving,
    /// automatic reconnections and watchdog.
    /// </summary>
    public class ClientWebSocketWrapper : IDisposable, IAsyncDisposable
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private readonly ILogger _logger;

        private Uri _uri;
        private ClientWebSocket _socket;
        private Timer _watchdog;
        private TimeSpan _watchdogTimeout;
        private bool _watchdogTriggered = false;
        private ManualResetEventSlim _watchdogReconnectedEvent;
        private DateTime _lastMessageTime;
        private int _reconnectInterval = 0;

        /// <summary>
        /// Fired when websocket reconnected for some reason.
        /// </summary>
        public event Action<ReconnectReason> Reconnected;

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="loggerFactory">Optional logger factory to enable logging.</param>
        public ClientWebSocketWrapper(ILoggerFactory loggerFactory = null)
        {
            _logger = loggerFactory?.CreateLogger<ClientWebSocketWrapper>();
        }

        /// <summary>
        /// Connect to a specified URI.
        /// </summary>
        /// <param name="uri">URI to connect to.</param>
        /// <param name="token">Optional cancellation token to cancel the connection process.</param>
        /// <returns>true if connected successfully, false otherwise.</returns>
        public async Task<bool> ConnectAsync(Uri uri, CancellationToken token = default)
        {
            _logger?.LogDebug($"Connecting to {uri}");
            _uri = uri;
            _reconnectInterval = 0;

            return await ReconnectAsync(token);
        }

        /// <summary>
        /// Starts a watchdog timer. The timer fires every second and checks the time the last message was received.
        /// If more than <paramref name="timeout"/> elapsed from that time, websocket reconnect is triggered.
        /// </summary>
        /// <param name="timeout">The amount of time that should elapse since the last message to trigger the reconnect.</param>
        public void StartWatchdog(TimeSpan timeout)
        {
            _logger?.LogInformation($"Starting watchdog with timeout of {timeout}.");
            _watchdog?.Dispose();
            _watchdogTriggered = false;
            _watchdogTimeout = timeout;
            _lastMessageTime = DateTime.UtcNow;
            _watchdog = new Timer(OnWatchdog, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Close the socket.
        /// </summary>
        /// <param name="token">An optional cancellation token to cancel the closing process.</param>
        /// <returns>true if connected successfully, false otherwise.</returns>
        public async Task<bool> CloseAsync(CancellationToken token = default)
        {
            if (_socket == null)
                return true;
            
            if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived &&
                _socket.State != WebSocketState.CloseSent)
            {
                _logger?.LogWarning($"Invalid socket state, can't close: {_socket.State}");
                return true;
            }
            
            _logger?.LogDebug($"Closing connection");
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                _logger?.LogError(e, $"Failed to close websocket: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a string message through the websocket.
        /// </summary>
        /// <param name="message">A message to send.</param>
        /// <param name="token">An optional cancellation token to cancel the sending process.</param>
        /// <returns>true if connected successfully, false otherwise.</returns>
        public async Task<bool> SendMessageAsync(string message, CancellationToken token = default)
        {
            try
            {
                var buff = Utf8.GetBytes(message);
                await _socket.SendAsync(new ArraySegment<byte>(buff), WebSocketMessageType.Text, true, token);
            }
            catch (TaskCanceledException)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Receive text messages as an async enumerable.
        /// </summary>
        /// <param name="token">An optional cancellation token to cancel the receiving.</param>
        /// <returns>An <see cref="IAsyncEnumerable{T}"/> which produces one full received text message at a time.</returns>
        public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            var buffer = new ArraySegment<byte>(new byte[4096]);
            await using var mem = new MemoryStream();

            // Loop over messages
            while (!token.IsCancellationRequested)
            {
                // Loop over message parts
                WebSocketReceiveResult result;
                int total = 0;
                do
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        _logger?.LogError($"Socket state is: {_socket.State}. Reconnecting.");
                        if (!await ReconnectAsync(token))
                            yield break;

                        mem.SetLength(0);
                        OnReconnected(ReconnectReason.InvalidState);

                        break;
                    }

                    try
                    {
                        result = await _socket.ReceiveAsync(buffer, token);
                        _lastMessageTime = DateTime.UtcNow;
                    }
                    catch (TaskCanceledException)
                    {
                        _logger?.LogInformation("ReceiveAsync has been cancelled.");
                        yield break;
                    }
                    catch (Exception e)
                    {
                        if (_watchdogTriggered)
                        {
                            _logger?.LogDebug("Exited ReceiveAsync due to the watchdog. Waiting for signal.");
                            try
                            {
                                _watchdogReconnectedEvent.Wait(token);
                            }
                            catch (TaskCanceledException)
                            {
                                _logger?.LogDebug("Waiting for watchdog signal has been cancelled.");
                                yield break;
                            }

                            _logger?.LogDebug("Got a signal after watchdog reconnect. Resuming ReceiveAsync.");
                            mem.SetLength(0);
                            break;
                        }

                        _logger?.LogError(e, $"Unhandled websocket error: {e.Message}. Will try to reconnect.");
                        if (!await ReconnectAsync(token))
                            yield break;
                            
                        mem.SetLength(0);
                        OnReconnected(ReconnectReason.SocketError);

                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        mem.SetLength(0);
                        
                        _logger?.LogWarning($"Remote connection close requested. Close status: {result.CloseStatus}, description: {result.CloseStatusDescription}. Closing connection and trying to reconnect.");
                        if (!await CloseAsync(token) || !await ReconnectAsync(token))
                            yield break;

                        OnReconnected(ReconnectReason.ClosedByServer);
                        
                        break;
                    }

                    mem.Write(buffer[..result.Count]);
                    checked
                    {
                        total += result.Count;
                    }
                } while (!result.EndOfMessage);

                if (mem.Length == 0)
                    continue;

                string message = Utf8.GetString(mem.GetBuffer()[..total]);
                mem.SetLength(0);

                yield return message;
            }
        }

        private async Task<bool> ReconnectAsync(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                if (_reconnectInterval > 0)
                {
                    _logger?.LogInformation($"Waiting for {_reconnectInterval} seconds before reconnecting");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_reconnectInterval), token);
                    }
                    catch (TaskCanceledException)
                    {
                        return false;
                    }
                }

                if (_socket != null)
                {
                    if (_socket.State == WebSocketState.Open)
                    {
                        if (! await CloseAsync(token))
                            return false;
                    }
                
                    _socket.Dispose();
                }
            
                _socket = new ClientWebSocket();
                
                try
                {
                    await _socket.ConnectAsync(_uri, token);
                    _reconnectInterval = 0;
                    _logger?.LogInformation("Connected");
                    return true;
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
                catch (Exception e)
                {
                    _logger?.LogError(e, $"Failed to connect: {e.Message}");
                    if (_reconnectInterval < 64)
                        _reconnectInterval = _reconnectInterval > 0 ? _reconnectInterval * 2 : 1;
                    _logger?.LogInformation($"Reconnect interval is: {_reconnectInterval} seconds.");
                }
            }

            return false;
        }

        private void OnWatchdog(object state)
        {
            if (DateTime.UtcNow - _lastMessageTime >= _watchdogTimeout)
            {
                _watchdog.Dispose();
                _watchdog = null;
                
                _logger?.LogError($"Watchdog has been triggered! The interval was {_watchdogTimeout}.");
                _watchdogTriggered = true;

                _watchdogReconnectedEvent = new ManualResetEventSlim();
                _socket.Dispose();
                _socket = null;
                Task.Run(async () => await ReconnectAsync()).ContinueWith(t =>
                {
                    _watchdogReconnectedEvent.Set();
                    OnReconnected(ReconnectReason.Watchdog);
                });
            }
        }

        /// <inheritdoc cref="Dispose"/>
        public void Dispose()
        {
            _socket?.Dispose();
            _socket = null;
        }

        /// <inheritdoc cref="DisposeAsync"/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return new ValueTask(Task.CompletedTask);
        }

        private protected virtual void OnReconnected(ReconnectReason reason)
        {
            Reconnected?.Invoke(reason);
        }
    }

    /// <summary>
    /// The reason behind a reconnect.
    /// </summary>
    public enum ReconnectReason
    {
        /// <summary>
        /// Socket was closed by server.
        /// </summary>
        ClosedByServer,
        
        /// <summary>
        /// Underlying websocket became invalid.
        /// </summary>
        InvalidState,
        
        /// <summary>
        /// Reconnect was triggered by a watchdog because no data was received in a specified timeframe.
        /// </summary>
        Watchdog,
        
        /// <summary>
        /// Some other socket error.
        /// </summary>
        SocketError
    }
}
[![GitHub Workflow Status](https://img.shields.io/github/workflow/status/olegtarasov/WebSocketWrapper/Build%20and%20publish?style=flat-square)](https://github.com/olegtarasov/WebSocketWrapper/actions)
[![Nuget](https://img.shields.io/nuget/v/OlegTarasov.WebSocketWrapper?style=flat-square)](https://www.nuget.org/packages/OlegTarasov.WebSocketWrapper)
[![Donwloads](https://img.shields.io/nuget/dt/OlegTarasov.WebSocketWrapper?label=Nuget&style=flat-square)](https://www.nuget.org/packages/OlegTarasov.WebSocketWrapper)

# WebSocketWrapper

This is a thin wrapper on top of .NET Standard `System.Net.WebSockets.ClientWebSocket` with the following feautures:

* Receive messages as an `IAsyncEnumerable`.
* Automatic reconnect. Currently, this library tries to automatically restore the connection no matter what (but uses sensible incremental intervals).
* Optional watchdog timer, which triggers a reconnect if no message is reveived for a specified period of time.

Please be aware that the library was just extracted from a different project of mine, and doesn't cover a lot of use cases. Specifically:

* ❗️ Sending and receiving of only text messages is supported.
* ❗️ Receiving messages is possible only through `IAsyncEnumerable`.
* ❗️ Automatic reconnect can't be turned off at the moment (but you can handle the `Reconnected` event and `Close()` the socket as a workaround).

## What's new

### `1.0.0`

* First version, just extracted from a different project.

## Usage

### Reconnect policy

This wrapper tries to maintain the connection no matter what. When an error happens, reconnect process is triggered:

* At first, it tries to reconnect immediately.
* If that fails, a progressive cooldown is used: the wrapper waits for 1, 2, 4, 8, 16, 32 or 64 seconds until connection is established.
* After reaching 64 seconds, cooldown time stops increasing and the wrapper tries to reconnect every 64 seconds. 

### Connecting

Use `Connect()` to initiate the connection. This method uses standard reconnect logic until it's connected. `false` is returned only
if connection process is cancelled with a token or something critical happens.

```c#
private async Task ConnectAndReceive()
{
    bool connected = await _socket.ConnectAsync(new Uri("wss://stream.binance.com:9443/stream"));
}
```

### Sending messages

Use `SendMessageAsync` to send a text message. Only text messages are supported currently.

```c#
await _socket.SendMessageAsync(request);
```

### Receiving messages

Call `ReceiveMessagesAsync` in `await foreach` to process messages as they arrive. You don't need to handle connection
failures, everything is handled behind the scenes.

```c#
private async Task ConnectAndReceive()
{
    bool connected = await _socket.ConnectAsync(new Uri("wss://stream.binance.com:9443/stream"));
    
    Task.Run(async () => await ReceiveMessagesAsync()); // Start receiving.
}

private async Task ReceiveMessagesAsync()
{
    try
    {
        await foreach (string msg in _socket.ReceiveMessagesAsync(_tokenSource.Token))
        {
            // Process the message
        }
        
        // No need to close the socket, it's in an aborted state after we cancelled receiving.
    }
    catch (TaskCanceledException)
    {
        // Cancellation token was fired
    }
    catch (Exception e)
    {
        // Some other critical error
    }
}
```

### Using watchdog

You can use a watchdog timer to monitor for incoming messages. This is useful for some quirky servers which don't drop the connection but instead
just stop sending you messages. You can specify a time interval which will trigger a reconnect if no messages are received in that interval.

Watchdog is turned off by default, you can arm it using `StartWatchdog()` method:

```c#
_socket.StartWatchdog(5);
```

Be aware that watchdog doesn't rearm automatically after a reconnect. You need to arm it again in `Reconnected` event handler.

### Handling reconnects

You can use a `Reconnected` event, which is fired after a socket succesfully restored connection. You can use this handler to
rearm a watchdog timer for example.

```c#
_socket.Reconnected += async reason =>
{
    _logger?.LogInformation($"WebSocket is being reconnected for reason: {reason}");
    _socket.StartWatchdog(5);
};
```

### Closing the socket

You can close the socket at any time with `CloseAsync()` method.

### Logging support

`ClientWebSocketWrapper` uses a standard .NET Core logging abstractions. You can turn logging on by providing an
instance of `Microsoft.Extensions.Logging.ILoggerFactory`. In this example we use Serilog with console sink.

You can also inject your standard `IloggerFactory` through .NET Core DI.

```c#
// Add the following Nuget packages to your project:
// * Serilog.Sinks.Console
// * Serilog.Extensions.Logging 

Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(theme: ConsoleTheme.None)
                .CreateLogger();

var socket = new ClientWebSocketWrapper(new SerilogLoggerFactory());
```
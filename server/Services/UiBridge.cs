using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace MapBuilder.Server.Services
{
    /// <summary>
    /// The server's line to the open pages: a Server-Sent Events stream (GET /api/events) that every page subscribes
    /// to, carrying what the AI (the MCP endpoint) is doing so the pages can follow it, and requests the pages answer
    /// (a screenshot, their state) through POST /api/ui/reply.
    /// </summary>
    internal sealed class UiBridge
    {
        private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        public int ClientCount => _clients.Count;

        /// <summary>Sends an event to every connected page (dropped when none is open).</summary>
        public void Publish(object evt)
        {
            string json = JsonSerializer.Serialize(evt, Responses.DataOptions);
            foreach (var c in _clients.Values) c.Writer.TryWrite(json);
        }

        /// <summary>A page's view of the events without a connection (tests): every event published from now on.</summary>
        internal ChannelReader<string> Listen()
        {
            var channel = Channel.CreateUnbounded<string>();
            _clients[Guid.NewGuid()] = channel;
            return channel.Reader;
        }

        /// <summary>Asks the pages for something and waits for the first answer.</summary>
        public async Task<JsonElement> Request(string kind, object? args, TimeSpan timeout)
        {
            if (_clients.IsEmpty)
                throw new InvalidOperationException("No Map Builder page is open. Open the app in a browser (http://127.0.0.1:8770) and try again.");
            string id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                Publish(new { type = "request", id, kind, args });
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                if (done != tcs.Task) throw new TimeoutException($"The page did not answer the {kind} request within {timeout.TotalSeconds:0} s.");
                return await tcs.Task;
            }
            finally { _pending.TryRemove(id, out _); }
        }

        /// <summary>A page's answer: {id, data} or {id, error}.</summary>
        public void Reply(JsonElement body)
        {
            string? id = body.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (id == null || !_pending.TryGetValue(id, out var tcs)) return;
            if (body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                tcs.TrySetException(new InvalidOperationException(e.GetString()));
            else
                tcs.TrySetResult(body.TryGetProperty("data", out var d) ? d.Clone() : default);
        }

        /// <summary>GET /api/events: the event stream, with a comment every 15 s so proxies keep it open.</summary>
        public async Task Stream(HttpContext ctx)
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var id = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _clients[id] = channel;
            var token = ctx.RequestAborted;
            try
            {
                await ctx.Response.WriteAsync($"data: {{\"type\":\"hello\",\"clients\":{_clients.Count}}}\n\n", token);
                await ctx.Response.Body.FlushAsync(token);
                Task<bool>? read = null;
                while (!token.IsCancellationRequested)
                {
                    // One wait at a time: a heartbeat leaves the pending wait in place for the next turn.
                    read ??= channel.Reader.WaitToReadAsync(token).AsTask();
                    if (await Task.WhenAny(read, Task.Delay(15000, token)) != read)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", token);
                        await ctx.Response.Body.FlushAsync(token);
                        continue;
                    }
                    bool more = await read;
                    read = null;
                    if (!more) break;
                    while (channel.Reader.TryRead(out var json))
                        await ctx.Response.WriteAsync($"data: {json}\n\n", token);
                    await ctx.Response.Body.FlushAsync(token);
                }
            }
            catch (OperationCanceledException) { /* the page closed */ }
            finally { _clients.TryRemove(id, out _); }
        }
    }
}

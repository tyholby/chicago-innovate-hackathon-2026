using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Core.Tests;

public class EnvFileTests
{
    [Fact]
    public void Parses_assignments_quotes_comments_and_exports()
    {
        var values = EnvFile.Parse("""
            # ChatGPT sign-in
            OPENAI_CODEX_HOME=C:\Users\me\.codex
            export OPENAI_CODEX_MODEL = "gpt-6-astra"
            OPENAI_CODEX_REASONING_EFFORT=high # trailing comment
            FAMILY_STUDIO_OUTPUT_DIR=
            QUOTED='a # not a comment'
            not a line
            """);
        Assert.Equal(@"C:\Users\me\.codex", values["OPENAI_CODEX_HOME"]);
        Assert.Equal("gpt-6-astra", values["openai_codex_model"]);
        Assert.Equal("high", values["OPENAI_CODEX_REASONING_EFFORT"]);
        Assert.Equal("a # not a comment", values["QUOTED"]);
        Assert.False(values.ContainsKey("FAMILY_STUDIO_OUTPUT_DIR"), "blank template values are skipped");
    }

    [Fact]
    public void Defaults_to_an_isolated_codex_home()
    {
        var env = StudioEnvironment.FromValues(new Dictionary<string, string>());
        Assert.True(env.UsesOwnCodexHome);
        Assert.EndsWith(Path.Combine("FamilyStudio", "codex-home"), env.CodexHome);
        Assert.EndsWith(Path.Combine("FamilyStudio", "sessions"), env.OutputRoot);
    }
}

public class JsonRpcConnectionTests
{
    /// <summary>A fake app-server on the other end of two in-memory pipes.</summary>
    private sealed class Wire : IDisposable
    {
        private readonly Pipe _toClient = new();
        private readonly Pipe _toServer = new();
        public readonly JsonRpcConnection Client;
        public readonly StreamReader ServerIn;
        public readonly StreamWriter ServerOut;

        public Wire()
        {
            Client = new JsonRpcConnection(new StreamReader(_toClient.Reader.AsStream()), new StreamWriter(_toServer.Writer.AsStream(), new UTF8Encoding(false)));
            ServerIn = new StreamReader(_toServer.Reader.AsStream());
            ServerOut = new StreamWriter(_toClient.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        }

        public async Task<JsonElement> ReadAsync() => JsonDocument.Parse((await ServerIn.ReadLineAsync())!).RootElement.Clone();
        public Task SendAsync(object message) => ServerOut.WriteLineAsync(JsonSerializer.Serialize(message));
        public void Dispose() { Client.Dispose(); _toClient.Writer.Complete(); }
    }

    [Fact]
    public async Task Calls_are_correlated_with_responses()
    {
        using var wire = new Wire();
        var call = wire.Client.CallAsync("account/read", new { refreshToken = false }, CancellationToken.None);
        var request = await wire.ReadAsync();
        Assert.Equal("account/read", request.GetProperty("method").GetString());
        await wire.SendAsync(new { id = request.GetProperty("id").GetInt64(), result = new { account = (object?)null } });
        var result = await call;
        Assert.Equal(JsonValueKind.Null, result.GetProperty("account").ValueKind);
    }

    [Fact]
    public async Task Errors_become_exceptions()
    {
        using var wire = new Wire();
        var call = wire.Client.CallAsync("thread/start", new { }, CancellationToken.None);
        var request = await wire.ReadAsync();
        await wire.SendAsync(new { id = request.GetProperty("id").GetInt64(), error = new { code = -32600, message = "bad model" } });
        var error = await Assert.ThrowsAsync<CodexRpcException>(() => call);
        Assert.Equal("bad model", error.Message);
        Assert.Equal(-32600, error.Code);
    }

    [Fact]
    public async Task Notifications_and_server_requests_are_dispatched()
    {
        using var wire = new Wire();
        var notified = new TaskCompletionSource<string>();
        wire.Client.Notification += (method, _) => notified.TrySetResult(method);
        wire.Client.RequestHandler = (method, _, _) => Task.FromResult<object?>(new { answer = method });

        await wire.SendAsync(new { method = "turn/started", @params = new { threadId = "t" } });
        Assert.Equal("turn/started", await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await wire.SendAsync(new { id = "srv-1", method = "currentTime/read", @params = new { threadId = "t" } });
        var reply = await wire.ReadAsync();
        Assert.Equal("srv-1", reply.GetProperty("id").GetString());
        Assert.Equal("currentTime/read", reply.GetProperty("result").GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Closing_the_stream_fails_pending_calls()
    {
        var wire = new Wire();
        var call = wire.Client.CallAsync("turn/start", new { }, CancellationToken.None);
        await wire.ReadAsync();
        wire.Dispose();
        await Assert.ThrowsAsync<IOException>(() => call);
    }
}

public class VersionAndImageTests
{
    [Theory]
    [InlineData("0.154.0", "0.154.0-alpha.6", 1)]
    [InlineData("0.154.0-alpha.10", "0.154.0-alpha.9", 1)]
    [InlineData("0.153.9", "0.154.0-alpha.1", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    public void Versions_compare_semantically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(CodexExecutable.CompareVersions(a, b)));

    [Fact]
    public void Png_and_jpeg_headers_give_their_size()
    {
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x05, 0x00, 0, 0, 0x02, 0xD0 }.CopyTo(png, 0);
        Assert.Equal(new ImageSize(1280, 720), ReferenceImages.Measure(png));

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0, 0, 0, 0 };
        Assert.Equal(new ImageSize(640, 480), ReferenceImages.Measure(jpeg));
        Assert.Null(ReferenceImages.Measure(Encoding.ASCII.GetBytes("GIF89a......")));
    }
}

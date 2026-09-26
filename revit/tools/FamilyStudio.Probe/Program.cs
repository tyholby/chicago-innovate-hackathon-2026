using System.Diagnostics;
using System.Runtime.InteropServices;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Prompts;
using FamilyStudio.Probe;

// familystudio-probe: run the Family Studio pipeline without Revit.
//
//   status                          Find Codex, show the ChatGPT account and model catalog
//   signin [--device]               Sign in with ChatGPT (browser, or a device code)
//   signout
//   single "<description>"          Brief, reference image and geometry for one item
//        [--photo file] [--size 650x700x850] [--unit mm|in] [--name text] [--materials "a;b"]
//   collection <preset>             The same for a seven-item collection, plus layout
//
// Common options: --env <file>  --model <id>  --effort <level>  --fidelity concept|refined

var options = Options.Parse(args);
if (options.Command is null or "help" or "--help")
{
    Console.WriteLine("usage: familystudio-probe status | signin [--device] | signout | single \"<description>\" [options] | collection <preset>");
    Console.WriteLine("presets: " + string.Join(", ", Presets.All.Where(p => p.Id != Presets.SingleId).Select(p => p.Id)));
    return 1;
}

var environment = options.Get("env") is string envFile
    ? StudioEnvironment.FromValues(EnvFile.Read(envFile).Concat(ProcessOverrides()).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase))
    : StudioEnvironment.Load(AppContext.BaseDirectory);

using var codex = new CodexService(new CodexOptions(environment.CodexHome, environment.CodexPath, environment.CodexWorkingDirectory, "0.1.0-probe"));
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

Console.WriteLine($"Codex home   {environment.CodexHome}{(environment.UsesOwnCodexHome ? "  (Family Studio's own)" : "")}");
await codex.ConnectAsync(cancel.Token);
Console.WriteLine($"Codex        {codex.Executable!.Version}  ({codex.Executable.Source}: {codex.Executable.Path})");
Console.WriteLine($"Account      {(codex.Account is { } a ? $"{a.Label} ({a.Plan})" : "not signed in")}");

switch (options.Command)
{
    case "status":
        foreach (var m in codex.Models)
            Console.WriteLine($"  {(m.IsDefault ? "*" : " ")} {m.Id,-22} effort {m.DefaultEffort,-7} [{string.Join(", ", m.Efforts)}]");
        return 0;

    case "signin":
    {
        var attempt = await codex.BeginSignInAsync(options.Has("device"), cancel.Token);
        if (attempt.BrowserUrl is string url)
        {
            Console.WriteLine("Opening your browser to sign in with ChatGPT...");
            Console.WriteLine(url);
            OpenBrowser(url);
        }
        else Console.WriteLine($"Go to {attempt.VerificationUrl} and enter the code {attempt.UserCode}");
        var outcome = await attempt.Completion.WaitAsync(cancel.Token);
        Console.WriteLine(outcome.Success ? $"Signed in as {codex.Account?.Label}." : $"Sign-in failed: {outcome.Error}");
        return outcome.Success ? 0 : 2;
    }

    case "signout":
        await codex.SignOutAsync(cancel.Token);
        Console.WriteLine("Signed out.");
        return 0;

    case "single":
    case "collection":
        if (codex.Account is null) { Console.WriteLine("Sign in first: familystudio-probe signin"); return 2; }
        return await RunDesignAsync(codex, environment, options, cancel.Token);

    default:
        Console.WriteLine($"Unknown command {options.Command}.");
        return 1;
}

static async Task<int> RunDesignAsync(CodexService codex, StudioEnvironment environment, Options options, CancellationToken token)
{
    var model = codex.Models.FirstOrDefault(m => m.Id == (options.Get("model") ?? environment.PreferredModel))
        ?? codex.Models.FirstOrDefault(m => m.IsDefault) ?? codex.Models.First();
    var effort = options.Get("effort") ?? environment.PreferredEffort ?? CodexModel.StartingEffort(model.Efforts, model.DefaultEffort);
    var fidelity = options.Get("fidelity") == "refined" ? Fidelity.Refined : Fidelity.Concept;
    var settings = new StageSettings(new ModelChoice(model.Id, effort), fidelity);

    StudioDraft draft;
    if (options.Command == "single")
    {
        var description = options.Positional ?? "";
        var unit = options.Get("unit") == "in" ? LengthUnit.Inches : LengthUnit.Millimetres;
        var size = options.Get("size") is string s && s.Split('x') is { Length: 3 } p ? Dimensions.Parse(p[0], p[1], p[2], unit) : null;
        var materials = options.Get("materials")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        draft = new StudioDraft("", new[] { description }, materials, new[] { options.Get("name") ?? "" },
            options.Get("photo") is string photo ? Path.GetFullPath(photo) : null, size);
    }
    else draft = Presets.Create(options.Positional ?? "office");

    var journal = SessionJournal.CreateUnder(environment.OutputRoot);
    var host = new InMemoryHost(journal);
    using var session = new StudioSession(codex, host, journal);
    Console.WriteLine($"Session      {journal.DirectoryPath}");
    Console.WriteLine($"Model        {settings.Model}  fidelity {settings.Fidelity}");
    var watch = Stopwatch.StartNew();
    string? last = null;
    session.Changed += () =>
    {
        var line = $"{session.State,-20} {session.Status}";
        if (line == last) return;
        last = line;
        Console.WriteLine($"[{watch.Elapsed:mm\\:ss}] {line}");
    };

    await session.GenerateAsync(draft, settings);
    if (session.State != StudioState.Review) return Report(session, 3);
    Console.WriteLine($"Brief        {session.Brief!.Title}: " + string.Join("; ", session.Brief.Assets.Select(a => $"{a.Name} x{a.Quantity} {a.SizeM.ToMillimetres()}")));
    Console.WriteLine($"Reference    {session.Reference!.Path}");
    session.Accept(draft);
    await session.BuildAsync(settings, reviewAfterBuild: false);
    if (session.State != StudioState.Built) return Report(session, 4);

    foreach (var asset in session.Brief.Assets)
    {
        var recipe = host.Recipes[asset.Id];
        var file = journal.PathFor($"family-{asset.Id}.svg");
        File.WriteAllText(file, AxonometricSvg.Render(new[] { (recipe, new Placement("preview", asset.Id, Vec3.Zero, 0, null, null)) }, session.Brief, $"{asset.Name}  {asset.SizeM.ToMillimetres()}"));
        Console.WriteLine($"Family       {asset.Name,-20} {recipe.Parts.Length,2} parts  {file}");
    }
    var room = journal.PathFor("room.svg");
    File.WriteAllText(room, AxonometricSvg.Render(host.Instances.Select(i => (i.Recipe, i.Placement)).ToArray(), session.Brief, session.Brief.Title));
    Console.WriteLine($"Room         {room}");
    return Report(session, 0);
}

static int Report(StudioSession session, int code)
{
    Console.WriteLine($"Result       {session.State}: {session.Status} {session.Detail}");
    foreach (var entry in session.Activity)
        Console.WriteLine($"  {entry.Time:HH:mm:ss}  {(entry.IsError ? "!" : " ")} {entry.Text}{(entry.Duration is TimeSpan d ? $"  ({d.TotalSeconds:0} s)" : "")}");
    return code;
}

static IEnumerable<KeyValuePair<string, string>> ProcessOverrides()
{
    foreach (var key in new[] { StudioEnvironment.CodexHomeKey, StudioEnvironment.CodexPathKey, StudioEnvironment.ModelKey, StudioEnvironment.EffortKey })
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
            yield return new(key, value);
}

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", url);
        else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception) { /* the URL is printed above */ }
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; private set; }
    public string? Positional { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                o._flags[name] = value;
            }
            else if (o.Command is null) o.Command = args[i];
            else o.Positional ??= args[i];
        }
        return o;
    }

    public string? Get(string name) => _flags.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => _flags.ContainsKey(name);
}

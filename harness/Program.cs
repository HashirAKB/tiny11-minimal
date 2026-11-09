// Headless driver for the official Tiny11 Builder engine (ntdevlabs/tiny11builder, Tiny11Builder.Core).
// Runs the same C# engine as the WinUI app, without the UI, so it can build on a GitHub Actions Windows runner.
//
//   Tiny11Headless download --out-dir C:\t11\src
//   Tiny11Headless build --source <iso|folder> --recipe recipe.json --patterns patterns.json
//                        --workspace C:\t11\ws --output C:\t11\out\tiny11.iso [--edition Professional]
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tiny11Builder.Core;

var cmd = args.FirstOrDefault() ?? "";
var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string Opt(string name, string? fallback = null) =>
    opts.TryGetValue(name, out var v) ? v : fallback ?? throw new ArgumentException($"--{name} is required");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var token = cts.Token;
var status = new ConsoleProgress<string>(s => Log("status", s));

try
{
    switch (cmd)
    {
        case "download": await Download(); break;
        case "build": await Build(); break;
        default:
            Console.Error.WriteLine("usage: Tiny11Headless download|build [options]");
            return 2;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"::error::{e.GetType().Name}: {e.Message}");
    Console.Error.WriteLine(e);
    return 1;
}

async Task Download()
{
    var outDir = PathSafety.Full(Opt("out-dir"));
    Directory.CreateDirectory(outDir);
    using var catalogClient = LatestWindowsMedia.CreateClient();
    var media = await new LatestWindowsMedia(catalogClient).ResolveAsync("x64", new CultureInfo(Opt("culture", "en-US")), status, token);
    Log("download", $"Latest official media: {media.Description} [{media.Language}/{media.Architecture}] {media.FileName}");
    var path = Path.Combine(outDir, media.FileName);
    using var client = OfficialImageDownloader.CreateClient();
    var lastPrint = DateTime.MinValue;
    var progress = new ConsoleProgress<DownloadProgress>(p =>
    {
        if ((DateTime.UtcNow - lastPrint).TotalSeconds < 15) return;
        lastPrint = DateTime.UtcNow;
        Log("download", $"{p.Bytes / 1048576d:N0} MiB" + (p.Total is long t ? $" / {t / 1048576d:N0} MiB" : "") +
                        $" @ {p.BytesPerSecond / 1048576d:N1} MiB/s");
    });
    var result = await new OfficialImageDownloader(client).DownloadAsync(media.Url.AbsoluteUri, path, media.Sha256, progress, token, status);
    Log("download", $"Saved {result.Path} SHA-256 {result.Sha256} (published hash verified: {result.VerifiedAgainstPublishedHash})");
    await File.WriteAllTextAsync(Path.Combine(outDir, "iso-path.txt"), result.Path, token);
    await File.WriteAllTextAsync(Path.Combine(outDir, "media.json"), JsonSerializer.Serialize(new
    {
        media.Description, media.FileName, media.Language, media.Architecture, result.Sha256, result.VerifiedAgainstPublishedHash
    }, new JsonSerializerOptions { WriteIndented = true }), token);
}

async Task Build()
{
    var source = PathSafety.Full(Opt("source"));
    var workspace = PathSafety.Full(Opt("workspace"));
    var output = PathSafety.Full(Opt("output"));
    var reportDir = PathSafety.Full(Opt("report-dir", Path.Combine(Path.GetDirectoryName(output)!, "reports")));
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    Directory.CreateDirectory(reportDir);

    var engine = new BuildEngine(new ProcessRunner(), new MediaSourceFactory(), new OfflineRegistry(), new ImageOperations(), new BuildHost());

    var dism = ToolLocator.FindDism(s => Log("tools", s));
    using (var oscClient = OfficialImageDownloader.CreateClient())
    {
        oscClient.Timeout = TimeSpan.FromSeconds(120);
        var oscdimg = await new OscdimgAcquirer(oscClient).EnsureAsync(Path.Combine(workspace, "..", "tools"), status, token);
        opts["oscdimg"] = oscdimg;
    }
    Log("tools", $"DISM: {dism}");
    Log("tools", $"oscdimg: {opts["oscdimg"]}");

    // Pick the requested edition.
    var images = await engine.InspectAsync(source, dism, token);
    foreach (var img in images) Log("inspect", $"#{img.Index} {img.Summary} edition={img.Edition}");
    var wanted = Opt("edition", "Professional");
    var image = images.FirstOrDefault(i => i.Edition.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? images.FirstOrDefault(i => i.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase) &&
                                              !i.Name.Contains("N", StringComparison.Ordinal) &&
                                              !i.Name.Contains("Education", StringComparison.OrdinalIgnoreCase) &&
                                              !i.Name.Contains("Workstation", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Edition '{wanted}' not found in the source image.");
    image.Validate();
    ToolLocator.EnsureServicingVersion(dism, image);
    Log("inspect", $"Selected #{image.Index}: {image.Name}");

    var baseRecipe = BuildRecipe.Read(Opt("recipe")) with
    {
        SourcePath = source, WorkspacePath = workspace, OutputPath = output,
        DismPath = dism, OscdimgPath = opts["oscdimg"], ImageIndex = image.Index
    };

    // Advanced removals (ExpertActions) are exact identities from the selected image's inventory.
    // We read the inventory, then select identities using the regex rules in patterns.json.
    var patterns = JsonSerializer.Deserialize<PatternFile>(await File.ReadAllTextAsync(Opt("patterns"), token),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PatternFile();
    var actions = new List<ExpertAction>();
    if (patterns.Remove.Count + patterns.Disable.Count > 0)
    {
        Log("inventory", "Reading full image inventory (read-only mount)...");
        var inventory = await engine.InventoryAsync(baseRecipe with { ExpertActions = [] }, Progress("inventory"), token);
        await File.WriteAllLinesAsync(Path.Combine(reportDir, "inventory.tsv"),
            inventory.Select(i => $"{i.Kind}\t{i.Identity}\t{i.Detail}\t{(i.Protected ? "protected" : "")}"), token);
        var never = patterns.Never.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();
        var catalogTargets = CleanupCatalog.Options.Where(o => baseRecipe.SelectedOptions.Contains(o.Id))
            .SelectMany(o => o.Targets).ToArray();

        void Select(Dictionary<string, string[]> rules, ExpertActionMode mode)
        {
            foreach (var (kindName, regexes) in rules)
            {
                var kind = Enum.Parse<ImageItemKind>(kindName, ignoreCase: true);
                var rx = regexes.Select(r => new Regex(r, RegexOptions.IgnoreCase)).ToArray();
                foreach (var item in inventory.Where(i => i.Kind == kind && rx.Any(r => r.IsMatch(i.Identity))))
                {
                    if (never.Any(n => n.IsMatch(item.Identity))) { Log("skip", $"{kind} {item.Identity} (on never-list)"); continue; }
                    if (kind == ImageItemKind.ProvisionedApp && catalogTargets.Any(t => item.Identity.StartsWith(t + "_", StringComparison.OrdinalIgnoreCase)))
                        continue; // already removed by a selected cleanup option
                    if (kind == ImageItemKind.Capability && !item.Detail.Contains("Installed", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kind == ImageItemKind.Feature && mode == ExpertActionMode.Disable &&
                        item.Detail.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    if (actions.Any(a => a.Kind == kind && a.Identity == item.Identity)) continue;
                    actions.Add(new ExpertAction(kind, item.Identity, mode));
                    Log("select", $"{mode} {kind} {item.Identity}");
                }
            }
        }
        Select(patterns.Remove, ExpertActionMode.Remove);
        Select(patterns.Disable, ExpertActionMode.Disable);
    }

    var recipe = baseRecipe with { ExpertActions = [.. actions], ExpertAcknowledged = baseRecipe.ExpertAcknowledged || actions.Count > 0 };
    recipe.Write(Path.Combine(reportDir, "effective-recipe.json"));
    Log("build", $"Options: {string.Join(", ", recipe.SelectedOptions)}");
    Log("build", $"Advanced actions: {actions.Count}");

    var result = await engine.BuildAsync(recipe, Progress("build"), token);
    Log("done", $"ISO: {result.IsoPath}");
    Log("done", $"SHA-256: {result.Sha256}");
    Log("done", $"Duration: {result.Duration}  Removed: {result.RemovedLogicalBytes / 1048576d:N0} MiB");
    foreach (var w in result.Warnings) Console.WriteLine($"::warning::{w}");
    if (File.Exists(result.ReportPath)) File.Copy(result.ReportPath, Path.Combine(reportDir, "report.json"), true);
    foreach (var name in new[] { "build.log", "operations.json", "dism.log" })
    {
        var p = Path.Combine(result.WorkDirectory, name);
        if (File.Exists(p)) File.Copy(p, Path.Combine(reportDir, name), true);
    }
    await File.WriteAllTextAsync(Path.Combine(reportDir, "sha256.txt"), $"{result.Sha256}  {Path.GetFileName(result.IsoPath)}\n", token);
}

IProgress<BuildProgress> Progress(string tag)
{
    string? last = null;
    return new ConsoleProgress<BuildProgress>(p =>
    {
        var line = $"{p.Stage}: {p.Message}" + (p.Percent is double pc ? $" ({pc:N0}%)" : "") +
                   (p.State is { } s ? $" [{s}]" : "") + (string.IsNullOrEmpty(p.Detail) ? "" : $" - {p.Detail}");
        if (line == last) return;
        last = line;
        Log(tag, line);
    });
}

static void Log(string tag, string message) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{tag}] {message}");

sealed class ConsoleProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly object gate = new();
    public void Report(T value) { lock (gate) handler(value); }
}

sealed class PatternFile
{
    public Dictionary<string, string[]> Remove { get; set; } = new();
    public Dictionary<string, string[]> Disable { get; set; } = new();
    public string[] Never { get; set; } = [];
}

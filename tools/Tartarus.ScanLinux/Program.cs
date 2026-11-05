using Tartarus.Vision;
using Tartarus.Vision.Ocr;

// tartarus-scan-linux scan <file>... [--lang zh] : what the reader sees in each screenshot
// tartarus-scan-linux sim <file> [--lang zh] [--days N] : the reading and the move hint
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length < 2) { Console.Error.WriteLine("usage: scan|sim <file>... [--lang zh]"); return 1; }
if (args[0] == "sim") return Sim.Run(args[1..], Opt, MakeScanner);
if (args[0] == "droid") return DroidTest.Run(args[1..]);
using var scanner = MakeScanner(args);
foreach (var f in args[1..].TakeWhile(a => !a.StartsWith("--")))
{
    var frame = ImageIO.Load(f);
    var r = scanner.Scan(frame);
    Console.WriteLine($"== {Path.GetFileName(f)} {frame.Width}x{frame.Height} ok={r.Ok} reason={r.Reason} presence={r.Loc?.Score:0.000} theme={r.Theme} pick={r.Pick?.Kind}");
    Console.WriteLine(SandboxText.Describe(r));
    if (r.Text is { } t)
    {
        Console.WriteLine($"  days={t.Days} objective={t.Objective} title={t.Title} cards=[{string.Join(" | ", t.Cards.Select(c => c.Text + "→" + c.Meaning))}]");
        foreach (var (role, lines) in t.ByRole) Console.WriteLine($"  [{role}] " + string.Join(" / ", lines.Select(l => l.Text)));
    }
    if (r.Notes is { Count: > 0 } n) Console.WriteLine("  notes: " + string.Join("; ", n));
}
return 0;

static string? Opt(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static ISandboxScanner MakeScanner(string[] args)
{
    var cells = new CnnCellReader(panels: new SlotReader());
    var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
    return new LayoutScanner(new OcrEngine(gameLanguage: Opt(args, "--lang") ?? "zh"), cells, presence);
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tartarus.Vision;

/// <summary>
/// Reads the screen's panels with the networks tools/train/slotnet.py trains (models/slots): how many Chromatic
/// Prisoner slots are filled, and which Torture Instrument each instrument slot holds. The slots are cut by
/// <see cref="ScreenLayout.Crop"/>, as the training crops are. Returns a partial <see cref="ScanResult"/> (Instruments,
/// Chromatic), for <see cref="CnnCellReader"/>'s panels.
/// </summary>
public sealed class SlotReader : ICellReader
{
    private readonly InferenceSession _chromatic, _instrument;
    private readonly string[] _chromaticClasses, _instrumentClasses;
    /// <summary>The instrument network's "hidden" class (a slot under a tooltip), or -1 for a network without one.</summary>
    private readonly int _hiddenClass;

    public SlotReader(string? modelDir = null, int threads = 1)
    {
        ModelDir = modelDir ?? FindModelDir() ?? throw new DirectoryNotFoundException("Slot models not found (models/slots/chromatic.onnx).");
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        Ocr.OcrEngine.Tune(options);
        _chromatic = new InferenceSession(Path.Combine(ModelDir, "chromatic.onnx"), options);
        _instrument = new InferenceSession(Path.Combine(ModelDir, "instrument.onnx"), options);
        _chromaticClasses = Classes("chromatic");
        _instrumentClasses = Classes("instrument");
        _hiddenClass = Array.IndexOf(_instrumentClasses, "hidden");

        string[] Classes(string name)
        {
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, name + ".json")));
            return meta.RootElement.GetProperty("classes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
    }

    /// <summary>The id of an instrument the network doesn't know.</summary>
    public const string Unknown = "?";
    /// <summary>The id of a slot the game's UI (a tooltip) covers: there is no instrument to read in it now.</summary>
    public const string Hidden = "hidden";

    /// <summary>Whether a slot read names an instrument (not <see cref="Unknown"/> or <see cref="Hidden"/>).</summary>
    public static bool IsInstrument(string id) => id != Unknown && id != Hidden;
    /// <summary>Least probability of an instrument read; below it the slot holds an instrument the network doesn't know.</summary>
    public const float MinConf = 0.8f;

    public string Name => "slots";
    public string ModelDir { get; }
    /// <summary>The instruments the network knows (others read as the closest of these).</summary>
    public IReadOnlyList<string> KnownInstruments => _instrumentClasses.Skip(1).Where(c => c != "hidden").ToArray();

    public static string? FindModelDir()
    {
        foreach (string dir in AppFiles.Folders)
        {
            string candidate = Path.Combine(dir, "models", "slots");
            if (File.Exists(Path.Combine(candidate, "chromatic.onnx")) && File.Exists(Path.Combine(candidate, "instrument.onnx"))) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 刑具格横向偏移（画布单位）：手机版游戏的刑具栏比电脑版靠右（2026-11-06：两台手机分别偏右约 100 和 130，
    /// 按电脑版坐标裁到的是全黑背景，整局刑具栏全是 ?/hidden）。第一次在一片范围里搜，找到后记住；之后每隔一段再核一次。
    /// </summary>
    private double _slotShift;
    private bool _slotShiftKnown;
    private int _readsSinceSearch;
    private const int ResearchEvery = 60;
    private static readonly double[] ShiftSteps = Enumerable.Range(0, 19).Select(i => i * 10.0).ToArray();  // 0 ～ 180

    public ScanResult Read(Frame frame, ScreenLayout layout)
    {
        var sw = Stopwatch.StartNew();
        var chromatic = Classify(_chromatic, Enumerable.Range(0, ScreenLayout.ChromaticSlots).Select(i => layout.Crop(frame, ScreenLayout.ChromaticSlot(i))).ToList());
        var instruments = ReadInstruments(frame, layout);

        int filled = chromatic.Count(c => _chromaticClasses[c.Class] == "filled");
        bool covered = chromatic.Any(c => _chromaticClasses[c.Class] == "hidden");
        var held = new List<InstrumentRead>();
        // Instruments fill the slots from the left: the first empty one ends the row. A slot a tooltip covers reads
        // Hidden; one the network isn't sure of, Unknown (the app shows what it read there before).
        foreach (var (cls, p) in instruments)
        {
            if (cls == 0 && p >= MinConf) break;
            string id = cls == _hiddenClass ? Hidden : p >= MinConf ? _instrumentClasses[cls] : Unknown;
            held.Add(new InstrumentRead { Id = id, Conf = p, Score = p });
        }
        var r = new ScanResult
        {
            Ok = true,
            Chromatic = new ChromaticRead { Count = filled, Conf = chromatic.Min(c => c.P), Covered = covered },
            Instruments = new InstrumentsRead { Found = true, Instruments = held },
        };
        r.Timings["slots"] = sw.Elapsed.TotalMilliseconds;
        return r;
    }

    private List<(int Class, float P)> ReadInstruments(Frame frame, ScreenLayout layout)
    {
        List<(int Class, float P)> At(double shift) =>
            Classify(_instrument, Enumerable.Range(0, ScreenLayout.InstrumentSlots).Select(i => layout.Crop(frame, ScreenLayout.InstrumentSlot(i).Offset(shift, 0))).ToList());
        // 一次读数的“把握”：认出几件刑具、把握多高（空格子也算有把握，但排在认出刑具之后）
        // 刑具从左往右填格子：第 1 格认出刑具比第 2 格认出更可信（第 1 格是空而第 2 格有刑具 = 裁偏了）
        double Score(List<(int Class, float P)> r)
        {
            double s = 0;
            bool emptyBefore = false;
            for (int i = 0; i < r.Count; i++)
            {
                var (cls, p) = r[i];
                if (cls == _hiddenClass) continue;
                if (cls == 0) { if (p >= MinConf) { s += 0.5; emptyBefore = true; } }
                else if (p >= MinConf) s += emptyBefore ? 0.5 : 2 + p;   // 空格子后面又有刑具：不合理，几乎不算
                else s += p * 0.5;
            }
            return s;
        }
        // 先按当前位置读：认得出（或者明确是空格子）就不搜。电脑版原位本来就认得出，永远不会搜，行为和以前一样
        var atCurrent = At(_slotShift);
        double currentScore = Score(atCurrent);
        // “第 1 格不确定、后面一格却认出了刑具”是裁偏的典型样子，也要搜
        bool firstUnsure = atCurrent.Count > 1 && atCurrent[0].P < MinConf && atCurrent.Skip(1).Any(r => r.Class != 0 && r.Class != _hiddenClass && r.P >= MinConf);
        bool fine = !firstUnsure && (currentScore >= 2 || atCurrent.All(r => r.Class == 0 && r.P >= MinConf));
        if (fine || (_slotShiftKnown && ++_readsSinceSearch < ResearchEvery)) return atCurrent;
        _readsSinceSearch = 0;
        // 认不出：整片扫，取把握最高的
        var tried = new List<(double Shift, List<(int, float)> Read, double Score)>();
        foreach (double shift in ShiftSteps)
        {
            var r = At(shift);
            tried.Add((shift, r, Score(r)));
        }
        // 把握最高的一片连续位置里取中间那个（边缘位置裁到一半格子，稍有抖动就认不出）
        double top = tried.Max(x => x.Score);
        var tops = tried.Where(x => x.Score >= top - 0.05).ToList();
        var best = tops[tops.Count / 2];
        if (Environment.GetEnvironmentVariable("SLOT_DEBUG") == "1")
            foreach (var x in tried) Console.Error.WriteLine($"  偏移 {x.Shift,4:0}: 得分 {x.Score:0.00}  " + string.Join(" | ", x.Read.Select(r => $"{_instrumentClasses[r.Item1]} {r.Item2:0.00}")));
        // 新位置要明显更好才换，免得在两个差不多的位置间来回跳
        if (_slotShiftKnown)
        {
            var current = tried.First(x => Math.Abs(x.Shift - _slotShift) < 1e-9);
            if (best.Score < current.Score + 1) return current.Read;
        }
        _slotShift = best.Shift;
        _slotShiftKnown = true;
        return best.Read;
    }

    /// <summary>当前用的刑具格横向偏移（画布单位），排查用。</summary>
    public double SlotShift => _slotShift;

    private static List<(int Class, float P)> Classify(InferenceSession session, List<Frame> crops)
    {
        var first = crops[0];
        var input = new DenseTensor<float>([crops.Count, 3, first.Height, first.Width]);
        for (int i = 0; i < crops.Count; i++) CnnCellReader.Fill(input, i, crops[i]);
        using var output = session.Run([NamedOnnxValue.CreateFromTensor("image", input)]);
        var t = output[0].AsTensor<float>();
        int classes = t.Dimensions[1];
        var flat = t.ToArray();
        var result = new List<(int, float)>(crops.Count);
        for (int i = 0; i < crops.Count; i++)
        {
            var row = flat.AsSpan(i * classes, classes);
            float max = float.MinValue;
            foreach (var v in row) max = Math.Max(max, v);
            float sum = 0;
            int best = 0;
            for (int c = 0; c < classes; c++)
            {
                sum += MathF.Exp(row[c] - max);
                if (row[c] > row[best]) best = c;
            }
            result.Add((best, 1f / sum));
        }
        return result;
    }

    public void Dispose()
    {
        _chromatic.Dispose();
        _instrument.Dispose();
    }
}

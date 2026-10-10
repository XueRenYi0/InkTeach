// InkDlPredictor：DL 预测器（v4）的 C# 前向（P0 模块，尚未接线到引擎）。
//
// 结构：GRU(hidden=96, L=20) → 10×位移头 μ、10×不确定度头 lv、τ 头。
// 权重：由 _ml-predict/code/export_v4.py 导出（weights.bin + weights.meta.json），
//       本类只做前向；特征管线（dt0=5ms 口径、S 在线估计、批点重定标）接入时另行实现。
//
// 数值口径与 PyTorch 逐字对齐（float32；GRU 门顺序 r/z/n，n 门先乘 reset）——
// 金标对比见 tools/dl-probe（误差应 <1e-3）。
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace InkEngine;

internal sealed class InkDlPredictor
{
    /// <summary>历史步数（v4=20）。</summary>
    public int Hist { get; private set; }
    /// <summary>GRU 隐层宽（v4=96）。</summary>
    public int Hidden { get; private set; }
    /// <summary>预测视界（ms；v4=4..40 每 4ms 一档）。</summary>
    public float[] Horizons { get; private set; }

    private readonly int _inDim = 8;
    private float[] _wih;    // (3H, inDim)
    private float[] _whh;    // (3H, H)
    private float[] _bih;    // (3H)
    private float[] _bhh;    // (3H)
    private float[][] _muW;  // H× (2, hidden)
    private float[][] _muB;  // H× (2)
    private float[][] _lvW;
    private float[][] _lvB;
    private float[] _tauW;   // (H)
    private float _tauB;

    private InkDlPredictor() { }

    /// <summary>从导出目录加载（weights.bin + weights.meta.json）。</summary>
    public static InkDlPredictor Load(string weightsBinPath, string metaJsonPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(metaJsonPath));
        var root = doc.RootElement;
        var p = new InkDlPredictor
        {
            Hist = root.GetProperty("L").GetInt32(),
            Hidden = root.GetProperty("hidden").GetInt32(),
        };
        var hz = new List<float>();
        foreach (var e in root.GetProperty("horizons").EnumerateArray())
            hz.Add(e.GetSingle());
        p.Horizons = hz.ToArray();
        int H = p.Hidden;

        var bytes = File.ReadAllBytes(weightsBinPath);
        var map = new Dictionary<string, (int off, int count)>();
        foreach (var w in root.GetProperty("weights").EnumerateArray())
        {
            int off = w.GetProperty("offset").GetInt32();
            int count = 1;
            foreach (var s in w.GetProperty("shape").EnumerateArray())
                count *= s.GetInt32();
            map[w.GetProperty("name").GetString()] = (off, count);
        }
        float[] Get(string name)
        {
            var (off, count) = map[name];
            var arr = new float[count];
            Buffer.BlockCopy(bytes, off * 4, arr, 0, count * 4);
            return arr;
        }

        p._wih = Get("gru.weight_ih_l0");
        p._whh = Get("gru.weight_hh_l0");
        p._bih = Get("gru.bias_ih_l0");
        p._bhh = Get("gru.bias_hh_l0");
        int K = p.Horizons.Length;
        p._muW = new float[K][]; p._muB = new float[K][];
        p._lvW = new float[K][]; p._lvB = new float[K][];
        for (int i = 0; i < K; i++)
        {
            p._muW[i] = Get($"mu.{i}.weight"); p._muB[i] = Get($"mu.{i}.bias");
            p._lvW[i] = Get($"lv.{i}.weight"); p._lvB[i] = Get($"lv.{i}.bias");
        }
        p._tauW = Get("tau.weight");
        p._tauB = Get("tau.bias")[0];
        return p;
    }

    private static float Sigmoid(float v) => 1f / (1f + MathF.Exp(-v));

    /// <summary>向量化点积（AVX2 + FMA；标量兜底）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (Avx2.IsSupported && Fma.IsSupported && a.Length >= Vector256<float>.Count)
        {
            ref float pa = ref MemoryMarshal.GetReference(a);
            ref float pb = ref MemoryMarshal.GetReference(b);
            int w = Vector256<float>.Count;
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            int i = 0;
            for (; i + 2 * w <= a.Length; i += 2 * w)   // 双路展开（96 长度 = 6×16）
            {
                var a0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref pa, i));
                var b0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref pb, i));
                acc0 = Fma.MultiplyAdd(a0, b0, acc0);
                var a1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref pa, i + w));
                var b1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref pb, i + w));
                acc1 = Fma.MultiplyAdd(a1, b1, acc1);
            }
            for (; i + w <= a.Length; i += w)
            {
                acc0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref Unsafe.Add(ref pa, i)),
                                       Vector256.LoadUnsafe(ref Unsafe.Add(ref pb, i)), acc0);
            }
            float s = Vector256.Sum(acc0 + acc1);
            for (; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }
        float ss = 0f;
        for (int i = 0; i < a.Length; i++) ss += a[i] * b[i];
        return ss;
    }

    /// <summary>
    /// 前向一次。x = 最近 Hist 步的 8 维特征（行主序，长度 Hist*8，最近一步在末尾）。
    /// mu/lv 长度 = Horizons.Length*2（相对位移，单位 = S；lv 为 logσ²）；tau 单位 = 32ms 归一。
    /// </summary>
    public void Predict(ReadOnlySpan<float> x, Span<float> mu, Span<float> lv, out float tau)
    {
        int H = Hidden;
        int K = Horizons.Length;
        Span<float> gih = stackalloc float[3 * H];
        Span<float> ghh = stackalloc float[3 * H];
        Span<float> h = stackalloc float[H];
        h.Clear();
        for (int t = 0; t < Hist; t++)
        {
            var xt = x.Slice(t * _inDim, _inDim);
            for (int o = 0; o < 3 * H; o++)
            {
                gih[o] = _bih[o] + Dot(_wih.AsSpan(o * _inDim, _inDim), xt);
                ghh[o] = _bhh[o] + Dot(_whh.AsSpan(o * H, H), h);
            }
            for (int j = 0; j < H; j++)
            {
                float r = Sigmoid(gih[j] + ghh[j]);
                float z = Sigmoid(gih[H + j] + ghh[H + j]);
                float n = MathF.Tanh(gih[2 * H + j] + r * ghh[2 * H + j]);
                h[j] = (1f - z) * n + z * h[j];
            }
        }
        for (int i = 0; i < K; i++)
        {
            for (int c = 0; c < 2; c++)
            {
                mu[i * 2 + c] = _muB[i][c] + Dot(_muW[i].AsSpan(c * H, H), h);
                lv[i * 2 + c] = _lvB[i][c] + Dot(_lvW[i].AsSpan(c * H, H), h);
            }
        }
        tau = _tauB + Dot(_tauW, h);
    }
}

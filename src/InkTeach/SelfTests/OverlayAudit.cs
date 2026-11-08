using InkEngine;

namespace InkTeach;

/// <summary>
/// 浮层管理器后台自检（A4.0）：不建窗口、不抢前台。
/// 只验证优先级、捕获、Esc、点外、阻断下层。
/// </summary>
internal static class OverlayAudit
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 1) 高优先级先吃，低优先级不执行
        {
            var m = new OverlayLayerManager();
            var low = new TestLayer("low", 500);
            var high = new TestLayer("high", 800);
            m.Register(low); m.Register(high);
            bool down = m.PointerDown(10, 10, 1, 1);
            Check("优先级高者先吃", down && high.DownCount == 1 && low.DownCount == 0,
                  $"high={high.DownCount} low={low.DownCount}");
        }

        // 2) 捕获：按下后 Move/Up 只发给它，哪怕移出范围
        {
            var m = new OverlayLayerManager();
            var layer = new TestLayer("capture", 800) { WantsCapture = true, Hit = true };
            m.Register(layer);
            m.PointerDown(10, 10, 7, 1);
            m.PointerMove(999, 999, 7);
            m.PointerUp(999, 999, 7);
            Check("指针捕获", layer.MoveCount == 1 && layer.UpCount == 1
                  && m.CaptureLayer == null,
                  $"move={layer.MoveCount} up={layer.UpCount}");
        }

        // 3) Esc：只关最上面允许 Esc 的一层
        {
            var m = new OverlayLayerManager();
            var low = new TestLayer("low", 500) { DismissPolicy = OverlayDismissPolicy.Escape };
            var high = new TestLayer("high", 800) { DismissPolicy = OverlayDismissPolicy.Escape };
            m.Register(low); m.Register(high);
            bool esc = m.HandleEscape();
            Check("Esc 只关最上面", esc && high.DismissCount == 1 && low.DismissCount == 0,
                  $"high={high.DismissCount} low={low.DismissCount}");
        }

        // 4) 点外：只关最上面允许 Outside 的一层
        {
            var m = new OverlayLayerManager();
            var layer = new TestLayer("outside", 800)
            {
                Bounds = new RectF { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 },
                DismissPolicy = OverlayDismissPolicy.Outside,
                Hit = false,
            };
            m.Register(layer);
            bool outside = m.HandleOutsideClick(200, 200);
            Check("点外关闭", outside && layer.DismissCount == 1,
                  $"dismiss={layer.DismissCount}");
        }

        // 5) 上层不吃但阻断：下层不许收到
        {
            var m = new OverlayLayerManager();
            var low = new TestLayer("low", 500) { Hit = true };
            var block = new TestLayer("block", 800)
            {
                Hit = true, DownReturns = false, BlocksInputBelow = true,
            };
            m.Register(low); m.Register(block);
            bool down = m.PointerDown(10, 10, 1, 1);
            Check("阻断下层", down && low.DownCount == 0 && block.DownCount == 1,
                  $"block={block.DownCount} low={low.DownCount}");
        }

        // 6) 引擎适配器：五个旧浮层都注册，优先级按表
        {
            var engine = new global::InkEngine.InkEngine();
            var layers = engine.OverlayLayers.Layers;
            bool hasAll = layers.Any(l => l.Id == "library")
                       && layers.Any(l => l.Id == "replay")
                       && layers.Any(l => l.Id == "timer")
                       && layers.Any(l => l.Id == "roll")
                       && layers.Any(l => l.Id == "ppt");
            var priorities = layers.Select(l => l.Priority).ToArray();
            bool ordered = priorities.SequenceEqual(priorities.OrderBy(p => p));
            Check("引擎浮层适配器注册", layers.Count == 5 && hasAll && ordered,
                  $"count={layers.Count} ids={string.Join(",", layers.Select(l => l.Id))}");
        }

        // 7) 优先级范围过滤：切某一层时不会误问更高/更低层
        {
            var m = new OverlayLayerManager();
            var low = new TestLayer("low", 500);
            var high = new TestLayer("high", 800);
            m.Register(low); m.Register(high);
            bool down = m.PointerDown(10, 10, 1, 1, 500, 500);
            Check("优先级范围过滤", down && low.DownCount == 1 && high.DownCount == 0,
                  $"low={low.DownCount} high={high.DownCount}");
        }

        // 8) PeekHit 只读探测：不触发任何按下（影子路由删掉后的覆盖）
        {
            var m = new OverlayLayerManager();
            var layer = new TestLayer("peek", 600);
            m.Register(layer);
            var hit = m.PeekHit(10, 10);
            Check("PeekHit 只读", ReferenceEquals(hit, layer) && layer.DownCount == 0,
                  $"hit={hit?.Id} down={layer.DownCount}");
        }

        Console.WriteLine($"  合计：{pass} 通过 / {fail} 失败");
        return fail == 0 ? 0 : 1;
    }

    private sealed class TestLayer : IOverlayLayer
    {
        public TestLayer(string id, int priority)
        {
            Id = id;
            Priority = priority;
            Bounds = new RectF { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 };
        }

        public string Id { get; }
        public int Priority { get; }
        public bool Visible { get; set; } = true;
        public bool ConsumesInput { get; set; } = true;
        public bool BlocksInputBelow { get; set; }
        public bool WantsCapture { get; set; }
        public RectF Bounds { get; set; }
        public OverlayDismissPolicy DismissPolicy { get; set; } = OverlayDismissPolicy.None;
        public bool IsAnimating => false;
        public bool Hit { get; set; } = true;
        public bool DownReturns { get; set; } = true;

        public int DownCount { get; private set; }
        public int MoveCount { get; private set; }
        public int UpCount { get; private set; }
        public int DismissCount { get; private set; }

        public bool HitTest(float x, float y) => Hit;
        public bool PointerDown(float x, float y, uint pointerId, uint pointerType)
        {
            DownCount++;
            return DownReturns;
        }
        public bool PointerMove(float x, float y, uint pointerId)
        {
            MoveCount++;
            return true;
        }
        public bool PointerUp(float x, float y, uint pointerId)
        {
            UpCount++;
            return true;
        }
        public bool Wheel(float x, float y, int delta) => false;
        public void Dismiss() => DismissCount++;
    }
}

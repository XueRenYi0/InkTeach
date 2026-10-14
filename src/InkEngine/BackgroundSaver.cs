using System.Threading;

namespace InkEngine;

/// <summary>
/// **写盘的后台通道**（2026-10-15 加；低配机"书写偶尔迟缓"的最小修复）。
///
/// 为什么要有它：板书历史 / 自动存档 / PPT 批注这三条路都是"整份序列化 → 写文件"，
/// 原来全在主线程上做。写盘这条路后面站着磁盘和**杀毒软件的实时扫描**（教室机上常见），
/// 一次几百 KB~几 MB 的落盘在慢盘上就是一次可感知的顿挫；而这类快照本来就不需要
/// "落完才算数"——10~15 秒后还会再写一份。
///
/// 纪律：
///   · **单工作线程 + FIFO 队列**：统一排队，顺序有保证（"周期写的旧内容"绝不会
///     盖掉"退出前写的新内容"——保证方式是"阻塞写等自己那一份落完，而它排在所有
///     已入队项之后"）；
///   · `blob == null` 的写 = **删除该文件**（PPT 空页的语义：删掉旧文件）；
///   · 序列化仍留在主线程（碰 Doc 只能主线程）——本类只搬"写文件"这一步；
///   · 失败一律只留痕、不抛（规矩三）：调用方不需要 try/catch；
///   · 进程退出前用 <see cref="Flush"/> 等在途写完（或走各处的"同步写"）。
/// </summary>
internal static class BackgroundSaver
{
    private sealed class Job
    {
        public string What;                  // 出错提示前缀（"板书历史" / "自动存档" / "PPT 批注"）
        public string Path;
        public byte[] Blob;                  // null = 删除 Path
        public Action After;                 // 写成功后调用（只给 Write 用；如板书历史轮转 Enforce）
        public ManualResetEventSlim Done;    // 只有"阻塞写"才有
        public volatile bool Ok;
    }

    private static readonly object _gate = new();
    private static readonly Queue<Job> _queue = new();
    private static bool _busy;               // 工作线程手里正有一份
    private static Thread _worker;
    private static readonly AutoResetEvent _signal = new(false);

    /// <summary>排一份写盘（不阻塞）。失败只留痕。
    /// `blob == null` = **删除** <paramref name="path"/>（PPT 空页语义）；
    /// 空数组 = 跳过（**不当删除**——防将来误传空内容把文件删了）。</summary>
    public static void Post(string what, string path, byte[] blob, Action after = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (blob != null && blob.Length == 0)
        {
            Console.WriteLine($"{what}：内容为空，跳过（不写也不删）");
            return;
        }
        Enqueue(new Job { What = what, Path = path, Blob = blob, After = after });
    }

    /// <summary>排一份写盘并**等它落完**（退出的最后几笔 / 同步语义的调用方用）。
    /// 返回 false = 超时 / 写失败 / 空内容跳过（失败详情已由工作线程打出来）。</summary>
    public static bool WriteBlocking(string what, string path, byte[] blob,
                                     Action after = null, int timeoutMs = 8000)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (blob != null && blob.Length == 0)
        {
            Console.WriteLine($"{what}：内容为空，跳过（不写也不删）");
            return false;
        }
        // 故意**不 using**：超时返回后工作线程可能还要 Set 这个事件，Dispose 了就会抛。
        // 阻塞写是低频路径（退出/翻页/删除），这几个对象留给 GC 回收没有代价。
        var done = new ManualResetEventSlim(false);
        var job = new Job { What = what, Path = path, Blob = blob, After = after, Done = done };
        Enqueue(job);
        if (!done.Wait(timeoutMs))
        {
            Console.WriteLine($"{what}写盘等待超时（{timeoutMs}ms；还在后台写）");
            return false;
        }
        return job.Ok;
    }

    /// <summary>等在途的写盘全部落完（最多等 timeoutMs；超时只留痕不抛）。退出前用。</summary>
    public static void Flush(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_gate)
            {
                if (_queue.Count == 0 && !_busy) return;
            }
            _signal.Set();          // 工作线程若在打盹，叫醒
            Thread.Sleep(5);
        }
    }

    // ------------------------------------------------------------------ 内部

    private static void Enqueue(Job job)
    {
        lock (_gate)
        {
            _queue.Enqueue(job);
            if (_worker == null)
            {
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "InkTeach-DiskWriter",
                };
                _worker.Start();
            }
        }
        _signal.Set();
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Job job = null;
            lock (_gate)
            {
                if (_queue.Count > 0) { job = _queue.Dequeue(); _busy = true; }
                else _busy = false;
            }
            if (job == null) { _signal.WaitOne(); continue; }

            try
            {
                if (job.Blob == null)
                {
                    if (File.Exists(job.Path)) File.Delete(job.Path);
                }
                else
                {
                    var dir = Path.GetDirectoryName(job.Path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // 先写临时文件再换名：写一半断电不留下坏文件（同各处原注释）。
                    string tmp = job.Path + ".tmp";
                    File.WriteAllBytes(tmp, job.Blob);
                    File.Move(tmp, job.Path, overwrite: true);
                    job.Ok = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{job.What}写盘失败（后台）：{ex.Message}");
            }

            if (job.Ok && job.After != null)
            {
                try { job.After(); }
                catch (Exception ex) { Console.WriteLine($"{job.What}写盘后处理失败（后台）：{ex.Message}"); }
            }

            job.Done?.Set();
            lock (_gate) { _busy = false; }
        }
    }
}

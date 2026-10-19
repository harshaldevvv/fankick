using System.Runtime.InteropServices;
using FanKick.Core;
using FanKick.Hardware;

namespace FanKick.Stress;

// CPU load (every core) and GPU load (every GPU OpenCL can see) for a set time, with a temperature cutoff: if the
// hottest CPU/GPU stays at or over it for 3 s, or the engine goes into panic, the load stops at once.
public sealed class StressTest
{
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Ready.";
    public float Peak { get; private set; } = float.NaN;
    public DateTime Started { get; private set; }
    volatile bool cpuOn, gpuOn, stop;

    public void Start(Core.Engine engine, bool cpu, bool gpu, TimeSpan duration, int cutoff, bool demo)
    {
        if (Running || (!cpu && !gpu)) return;
        Running = true; stop = false; Peak = float.NaN; Started = DateTime.Now;
        Log.Write($"stress test: {(cpu ? "CPU " : "")}{(gpu ? "GPU " : "")}for {(duration == TimeSpan.Zero ? "until stopped" : duration.ToString())}, cutoff {cutoff} °C");
        var threads = new List<Thread>();
        if (demo) { DemoHardware.CpuStress = cpu; DemoHardware.GpuStress = gpu; }
        else
        {
            if (cpu)
            {
                cpuOn = true;
                for (int i = 0; i < Environment.ProcessorCount; i++) threads.Add(Begin(BurnCpu, null));
            }
            if (gpu)
            {
                gpuOn = true;
                var devs = OpenCl.Gpus();
                if (devs.Count == 0) Status = "No OpenCL GPU found; running CPU only.";
                foreach (var d in devs) threads.Add(Begin(o => OpenCl.Burn((IntPtr)o!, () => gpuOn), d));
            }
        }
        new Thread(() => Watch(engine, duration, cutoff, threads, demo)) { IsBackground = true, Name = "stress watch" }.Start();
    }

    public void Stop() => stop = true;

    // `FanKick --load cpu|gpu|both <seconds>`: only the load, no sensors and no fans (FanKick itself keeps watching
    // temperatures and fans). For scripted thermal tests.
    public static void Load(bool cpu, bool gpu, TimeSpan time)
    {
        var t = new StressTest { cpuOn = cpu, gpuOn = gpu };
        var threads = new List<Thread>();
        if (cpu) for (int i = 0; i < Environment.ProcessorCount; i++) threads.Add(Begin(t.BurnCpu, null));
        if (gpu) foreach (var d in OpenCl.Gpus()) threads.Add(Begin(o => OpenCl.Burn((IntPtr)o!, () => t.gpuOn), d));
        Log.Write($"load: {(cpu ? "CPU " : "")}{(gpu ? "GPU " : "")}for {time.TotalSeconds:F0} s on {threads.Count} threads");
        Thread.Sleep(time);
        t.cpuOn = t.gpuOn = false;
        foreach (var th in threads) th.Join(5000);
    }

    static Thread Begin(Action<object?> work, object? arg)
    {
        var t = new Thread(o => work(o)) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.Start(arg);
        return t;
    }

    void Watch(Core.Engine engine, TimeSpan duration, int cutoff, List<Thread> threads, bool demo)
    {
        string? why = null;
        int hotFor = 0;
        while (why == null)
        {
            Thread.Sleep(1000);
            float t = engine.HottestSensor.Value;
            if (!float.IsNaN(t)) Peak = float.IsNaN(Peak) ? t : Math.Max(Peak, t);
            hotFor = t >= cutoff ? hotFor + 1 : 0;
            var ran = DateTime.Now - Started;
            if (stop) why = "Stopped.";
            else if (hotFor >= 3) why = $"Stopped by the safety cutoff: {t:0} °C for 3 s (limit {cutoff} °C).";
            else if (engine.Panic) why = "Stopped: the panic temperature was reached.";
            else if (duration > TimeSpan.Zero && ran >= duration) why = "Done.";
            else Status = $"Running {ran:mm\\:ss}{(duration > TimeSpan.Zero ? $" of {duration:mm\\:ss}" : "")}. Now {Fmt(t)}, peak {Fmt(Peak)}.";
        }
        cpuOn = gpuOn = false;
        if (demo) DemoHardware.CpuStress = DemoHardware.GpuStress = false;
        foreach (var th in threads) th.Join(5000);
        Status = $"{why} Ran {DateTime.Now - Started:mm\\:ss}, peak {Fmt(Peak)}.";
        Log.Write("stress test: " + Status);
        Running = false;
    }

    static string Fmt(float t) => float.IsNaN(t) ? "-" : $"{t:0} °C";

    void BurnCpu(object? _)
    {
        // Four independent multiply-add chains keep both FP pipes of a core busy.
        double a = 1.1, b = 1.2, c = 1.3, d = 1.4;
        while (cpuOn)
            for (int i = 0; i < 1_000_000; i++) { a = a * 0.999999 + 1e-7; b = b * 0.999998 + 2e-7; c = c * 0.999997 + 3e-7; d = d * 0.999996 + 4e-7; }
        GC.KeepAlive(a + b + c + d);
    }

    // OpenCL ships with the graphics driver on Windows and Linux, and with macOS.
    static class OpenCl
    {
        const string Lib = "OpenCL";
        [DllImport(Lib)] static extern int clGetPlatformIDs(uint n, IntPtr[]? p, out uint num);
        [DllImport(Lib)] static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint n, IntPtr[]? d, out uint num);
        [DllImport(Lib)] static extern IntPtr clCreateContext(IntPtr props, uint n, IntPtr[] d, IntPtr notify, IntPtr user, out int err);
        [DllImport(Lib)] static extern IntPtr clCreateCommandQueue(IntPtr ctx, IntPtr dev, ulong props, out int err);
        [DllImport(Lib)] static extern IntPtr clCreateProgramWithSource(IntPtr ctx, uint n, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] src, IntPtr lengths, out int err);
        [DllImport(Lib)] static extern int clBuildProgram(IntPtr prog, uint n, IntPtr[] d, string? options, IntPtr notify, IntPtr user);
        [DllImport(Lib)] static extern IntPtr clCreateKernel(IntPtr prog, string name, out int err);
        [DllImport(Lib)] static extern IntPtr clCreateBuffer(IntPtr ctx, ulong flags, UIntPtr size, IntPtr host, out int err);
        [DllImport(Lib)] static extern int clSetKernelArg(IntPtr k, uint i, UIntPtr size, ref IntPtr v);
        [DllImport(Lib)] static extern int clSetKernelArg(IntPtr k, uint i, UIntPtr size, ref int v);
        [DllImport(Lib)] static extern int clEnqueueNDRangeKernel(IntPtr q, IntPtr k, uint dim, IntPtr offset, UIntPtr[] global, IntPtr local, uint nev, IntPtr wait, IntPtr ev);
        [DllImport(Lib)] static extern int clFinish(IntPtr q);
        [DllImport(Lib)] static extern int clReleaseMemObject(IntPtr o);
        [DllImport(Lib)] static extern int clReleaseKernel(IntPtr o);
        [DllImport(Lib)] static extern int clReleaseProgram(IntPtr o);
        [DllImport(Lib)] static extern int clReleaseCommandQueue(IntPtr o);
        [DllImport(Lib)] static extern int clReleaseContext(IntPtr o);

        public static List<IntPtr> Gpus()
        {
            var list = new List<IntPtr>();
            try
            {
                clGetPlatformIDs(0, null, out uint np);
                var ps = new IntPtr[np];
                clGetPlatformIDs(np, ps, out np);
                foreach (var p in ps)
                {
                    if (clGetDeviceIDs(p, 4 /* CL_DEVICE_TYPE_GPU */, 0, null, out uint nd) != 0 || nd == 0) continue;
                    var ds = new IntPtr[nd];
                    clGetDeviceIDs(p, 4, nd, ds, out nd);
                    list.AddRange(ds);
                }
            }
            catch (Exception ex) { Log.Write("stress test: no OpenCL (" + ex.Message + ")"); }
            return list;
        }

        public static void Burn(IntPtr dev, Func<bool> go)
        {
            IntPtr ctx = 0, q = 0, prog = 0, k = 0, buf = 0;
            try
            {
                ctx = clCreateContext(0, 1, [dev], 0, 0, out int err); if (err != 0) throw new IOException("context " + err);
                q = clCreateCommandQueue(ctx, dev, 0, out err); if (err != 0) throw new IOException("queue " + err);
                const string src = "__kernel void burn(__global float* o, int n) { int i = get_global_id(0); float a = i * 1e-6f, b = 1.0001f, c = 0.9999f, d = 0.5f;" +
                    " for (int k = 0; k < n; k++) { a = mad(a, b, c); b = mad(b, c, d); c = mad(c, d, a); d = mad(d, a, b); } o[i] = a + b + c + d; }";
                prog = clCreateProgramWithSource(ctx, 1, [src], 0, out err); if (err != 0) throw new IOException("program " + err);
                err = clBuildProgram(prog, 1, [dev], null, 0, 0); if (err != 0) throw new IOException("build " + err);
                k = clCreateKernel(prog, "burn", out err); if (err != 0) throw new IOException("kernel " + err);
                const int items = 1 << 18;
                buf = clCreateBuffer(ctx, 1 /* READ_WRITE */, items * 4, 0, out err); if (err != 0) throw new IOException("buffer " + err);
                int n = 2048;
                clSetKernelArg(k, 0, (UIntPtr)IntPtr.Size, ref buf);
                clSetKernelArg(k, 1, 4, ref n);
                var global = new[] { (UIntPtr)items };
                while (go())
                {
                    // Short kernels (a few ms each) so the OS never resets the driver for a hung GPU.
                    for (int i = 0; i < 8; i++) clEnqueueNDRangeKernel(q, k, 1, 0, global, 0, 0, 0, 0);
                    clFinish(q);
                }
            }
            catch (Exception ex) { Log.Write("stress test: GPU load failed: " + ex.Message); }
            finally
            {
                if (buf != 0) clReleaseMemObject(buf);
                if (k != 0) clReleaseKernel(k);
                if (prog != 0) clReleaseProgram(prog);
                if (q != 0) clReleaseCommandQueue(q);
                if (ctx != 0) clReleaseContext(ctx);
            }
        }
    }
}

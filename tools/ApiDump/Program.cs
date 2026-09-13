using System.Reflection;
using System.Text;

var sb = new StringBuilder();

void Line(string s = "") => sb.AppendLine(s);

string Sig(MethodBase m)
{
    var ps = m.GetParameters().Select(p =>
    {
        var t = p.ParameterType;
        bool isOut = t.IsByRef && t.GetElementType()?.IsGenericType == true
                     && t.GetElementType()!.GetGenericTypeDefinition() == typeof(List<>);
        string tn = Pretty(t);
        return (p.IsOut ? "out " : (t.IsByRef ? "ref " : "")) + tn + " " + p.Name;
    });
    return $"{Pretty(m is MethodInfo mi ? mi.ReturnType : typeof(void))} {m.Name}({string.Join(", ", ps)})";
}

string Pretty(Type t)
{
    if (t.IsByRef) return Pretty(t.GetElementType()!);
    if (t.IsArray) return Pretty(t.GetElementType()!) + "[]";
    if (t.IsGenericType)
    {
        var n = t.Name.Split('`')[0];
        return n + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
    }
    return t.Name;
}

void DumpType(Type t, string filter = null)
{
    Line($"### {t.FullName}");
    if (t.IsEnum)
    {
        Line("   values: " + string.Join(", ", Enum.GetNames(t)));
        Line();
        return;
    }
    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        Line("   .ctor(" + string.Join(", ", c.GetParameters().Select(p => Pretty(p.ParameterType) + " " + p.Name)) + ")");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Line($"   prop {Pretty(p.PropertyType)} {p.Name} {(p.CanRead ? "{get;}" : "")}{(p.CanWrite ? "{set;}" : "")}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Line($"   field {Pretty(f.FieldType)} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (m.IsSpecialName) continue;
        if (filter != null && !m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        Line("   " + Sig(m));
    }
    Line();
}

void DumpAssembly(string simpleName, string typeFilterContains = null)
{
    Assembly asm;
    try { asm = Assembly.Load(simpleName); }
    catch (Exception e) { Line($"!! cannot load {simpleName}: {e.Message}"); return; }
    Line($"========== ASSEMBLY {asm.GetName().Name} ==========");
    foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
    {
        if (typeFilterContains != null && !t.Name.Contains(typeFilterContains, StringComparison.OrdinalIgnoreCase)) continue;
        Line("TYPE: " + t.FullName + (t.IsEnum ? " (enum)" : t.IsInterface ? " (interface)" : t.IsAbstract && t.IsSealed ? " (static class)" : ""));
    }
    Line();
}

// 1) List all types in each assembly so we know what exists.
foreach (var a in new[] { "Vortice.DirectComposition", "Vortice.Mathematics", "Vortice.DXGI", "Vortice.Direct2D1", "Vortice.Direct3D11" })
    DumpAssembly(a);

// 2) Detailed dumps of the specific APIs we plan to use.
void Detail(string asm, string typeName, string filter = null)
{
    var a = Assembly.Load(asm);
    var t = a.GetType(typeName);
    if (t == null) { Line($"!! type not found: {typeName}"); return; }
    DumpType(t, filter);
}

Detail("Vortice.Direct3D11", "Vortice.Direct3D11.D3D11", "CreateDevice");
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGIFactory2", "SwapChain");
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGISwapChain1", "Present");
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGISwapChain2", "Latency");
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.D2D1", "CreateFactory");
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Factory1", "Create");
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Device", "Create");
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1DeviceContext", null);
Detail("Vortice.Direct2D1", "Vortice.DirectWrite.DWrite", "Create");
Detail("Vortice.Direct2D1", "Vortice.DirectWrite.IDWriteFactory", "Create");

Detail("Vortice.Direct3D11", "Vortice.Direct3D11.ID3D11Device", "Query");
Detail("Vortice.DXGI", "Vortice.DXGI.DXGI");
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGISwapChain", null);
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGIDevice", "Adapter");
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGIAdapter3", null);
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGIAdapter", null);
Detail("Vortice.DXGI", "Vortice.DXGI.AdapterDescription1", null);
Detail("Vortice.DXGI", "Vortice.DXGI.AdapterFlags", null);
Detail("Vortice.DXGI", "Vortice.DXGI.AdapterDescription", null);
Detail("Vortice.DXGI", "Vortice.PointerUSize", null);
Detail("Vortice.DXGI", "Vortice.PointerSize", null);
Detail("Vortice.DXGI", "Vortice.DXGI.QueryVideoMemoryInfo", null);
Detail("Vortice.DXGI", "Vortice.DXGI.IDXGIObject", null);
Detail("Vortice.DXGI", "Vortice.DXGI.SwapChainDescription1", null);
Detail("Vortice.DXGI", "Vortice.DXGI.SampleDescription", null);
Detail("Vortice.DXGI", "Vortice.DXGI.ModeDescription", null);

Detail("Vortice.DirectComposition", "Vortice.DirectComposition.DComp");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionDesktopDevice");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionDevice");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionTarget");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionVisual");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionInkTrailDevice");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.IDCompositionDelegatedInkTrail");
Detail("Vortice.DirectComposition", "Vortice.DirectComposition.DCompositionInkTrailPoint");

Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1RenderTarget", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1DeviceContext1", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1GeometryRealization", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Bitmap1", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Bitmap", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Factory", "Geometry");
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1GeometrySink", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Geometry", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1SolidColorBrush", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.ID2D1Layer", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.BitmapProperties1", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.PixelFormat", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.LayerParameters1", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.StrokeStyleProperties1", null);
Detail("Vortice.Direct2D1", "Vortice.Direct2D1.BrushProperties", null);
Detail("Vortice.Mathematics", "Vortice.Mathematics.SizeI", null);
Detail("Vortice.Mathematics", "Vortice.Mathematics.RectI", null);
Detail("Vortice.Mathematics", "Vortice.Mathematics.Color4", null);
Detail("Vortice.Mathematics", "Vortice.Mathematics.Rect", null);

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "apidump.txt"), sb.ToString());
Console.WriteLine("written " + sb.Length + " chars");

// 3) Focused lookups for ambiguous shared types.
var pf = typeof(Vortice.Direct2D1.BitmapProperties1).GetConstructors()[0].GetParameters()[0].ParameterType;
Console.WriteLine($"BitmapProperties1 ctor param: {pf.FullName} in {pf.Assembly.GetName().Name}");
foreach (var p in pf.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    Console.WriteLine($"   prop {p.PropertyType.Name} {p.Name}");
foreach (var f in pf.GetFields(BindingFlags.Public | BindingFlags.Instance))
    Console.WriteLine($"   field {f.FieldType.Name} {f.Name}");

foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
{
    foreach (var t in a.GetExportedTypes())
    {
        if (t.Name == "PixelFormat" || t.Name == "RawBool" || t.Name == "RawRectF")
            Console.WriteLine($"FOUND {t.FullName} in {a.GetName().Name}");
    }
}

// 4) Resolve helper structs/enums we rely on.
var sd = typeof(Vortice.DXGI.SwapChainDescription1).GetField("SampleDescription")!.FieldType;
Console.WriteLine($"SampleDescription = {sd.FullName} (valueType={sd.IsValueType})");
foreach (var c in sd.GetConstructors()) Console.WriteLine("   .ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
var sdDef = Activator.CreateInstance(sd);
Console.WriteLine("   default = " + sdDef);

var rb = typeof(SharpGen.Runtime.RawBool);
Console.WriteLine("RawBool ctors: " + string.Join(" | ", rb.GetConstructors().Select(c => "(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")));
Console.WriteLine("RawBool implicit ops: " + string.Join(" | ", rb.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.Contains("op_")).Select(m => m.Name + ":" + m.ReturnType.Name + "<-" + m.GetParameters()[0].ParameterType.Name)));

void EnumVals<T>() where T : struct, Enum
    => Console.WriteLine($"{typeof(T).FullName} = {{ {string.Join(", ", Enum.GetNames<T>())} }}");
EnumVals<Vortice.DXGI.Format>();
EnumVals<Vortice.DXGI.AlphaMode>();
EnumVals<Vortice.DXGI.Usage>();
EnumVals<Vortice.DXGI.Scaling>();
EnumVals<Vortice.DXGI.SwapEffect>();
EnumVals<Vortice.DXGI.SwapChainFlags>();
EnumVals<Vortice.DXGI.PresentFlags>();
EnumVals<Vortice.Direct3D11.DeviceCreationFlags>();
EnumVals<Vortice.Direct3D.DriverType>();
EnumVals<Vortice.Direct3D.FeatureLevel>();
EnumVals<Vortice.Direct2D1.BitmapOptions>();
EnumVals<Vortice.Direct2D1.AntialiasMode>();
EnumVals<Vortice.Direct2D1.DrawTextOptions>();
EnumVals<Vortice.Direct2D1.InterpolationMode>();
EnumVals<Vortice.Direct2D1.PrimitiveBlend>();
EnumVals<Vortice.Direct2D1.DeviceContextOptions>();
EnumVals<Vortice.Direct2D1.FactoryType>();
EnumVals<Vortice.Direct2D1.TextAntialiasMode>();
EnumVals<Vortice.DirectWrite.FactoryType>();
EnumVals<Vortice.DirectWrite.FontWeight>();
EnumVals<Vortice.DirectWrite.FontStyle>();
EnumVals<Vortice.DirectWrite.FontStretch>();
EnumVals<Vortice.Direct2D1.CapStyle>();
EnumVals<Vortice.Direct2D1.LineJoin>();
Console.WriteLine("ID2D1DeviceContext1 methods: " + string.Join(" | ", (Assembly.Load("Vortice.Direct2D1").GetType("Vortice.Direct2D1.ID2D1DeviceContext1")?.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name) ?? Array.Empty<string>())));
Console.WriteLine("ID2D1GeometrySink bases: " + string.Join(" | ", (Assembly.Load("Vortice.Direct2D1").GetType("Vortice.Direct2D1.ID2D1GeometrySink")?.GetInterfaces().Select(i => i.Name) ?? Array.Empty<string>())));
Console.WriteLine("ID2D1SimplifiedGeometrySink methods: " + string.Join(" | ", (Assembly.Load("Vortice.Direct2D1").GetType("Vortice.Direct2D1.ID2D1SimplifiedGeometrySink")?.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name) ?? Array.Empty<string>())));

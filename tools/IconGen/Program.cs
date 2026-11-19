using System.Drawing;
using System.Drawing.Imaging;
using xinread.IconGen;

// 用法：
//   dotnet run                    生成到仓库的 Assets 目录
//   dotnet run -- <目录>           生成到指定目录
//   dotnet run -- --preview <目录>  额外输出放大预览，用于检查小尺寸下的可读性
string? previewDir = null;
var positional = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--preview" or "-p")
    {
        previewDir = i + 1 < args.Length ? args[++i] : Path.Combine(AppContext.BaseDirectory, "preview");
    }
    else
    {
        positional.Add(args[i]);
    }
}

string root = positional.Count > 0
    ? Path.GetFullPath(positional[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets"));

Directory.CreateDirectory(root);

// 与 Package.appxmanifest 里引用的文件名一一对应。
// plateScale 决定底板占画布的大小：任务栏 / 磁贴图标留边距，unplated 与 .ico 铺满。
(string Name, int Width, int Height, float PlateScale)[] targets =
[
    ("Square44x44Logo.scale-200.png", 88, 88, 0.86f),
    ("Square150x150Logo.scale-200.png", 300, 300, 0.86f),
    ("Wide310x150Logo.scale-200.png", 620, 300, 0.44f),
    ("StoreLogo.png", 50, 50, 0.90f),
    ("LockScreenLogo.scale-200.png", 48, 48, 1.00f),
    ("SplashScreen.scale-200.png", 1240, 600, 0.42f),
    ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24, 1.00f),
    ("Square44x44Logo.targetsize-48_altform-lightunplated.png", 48, 48, 1.00f),
];

foreach ((string name, int width, int height, float plateScale) in targets)
{
    using Bitmap bitmap = IconDesign.Render(width, height, plateScale);
    string path = Path.Combine(root, name);
    bitmap.Save(path, ImageFormat.Png);
    Console.WriteLine($"{name,-58} {width,5}x{height,-5} -> {new FileInfo(path).Length,7} bytes");
}

// AppIcon.ico：多个尺寸打进同一个文件，供窗口图标与 exe 使用。
WriteIco(Path.Combine(root, "AppIcon.ico"), [16, 24, 32, 48, 64, 128, 256]);
Console.WriteLine($"{"AppIcon.ico",-58} {string.Join('/', new[] { 16, 24, 32, 48, 64, 128, 256 }),5}   -> {new FileInfo(Path.Combine(root, "AppIcon.ico")).Length,7} bytes");

Console.WriteLine($"\n输出目录: {root}");

if (previewDir is not null)
{
    Console.WriteLine($"\n生成预览: {previewDir}");
    Preview.Render(previewDir);
}

static void WriteIco(string path, int[] sizes)
{
    // ICO 结构：6 字节头 + 每帧 16 字节目录项 + 各帧 PNG 负载。
    // PNG 负载从 Vista 起就被支持，且能保留 alpha 通道。
    var payloads = new List<byte[]>(sizes.Length);
    foreach (int size in sizes)
    {
        using Bitmap bitmap = IconDesign.Render(size, size, plateScale: 1.0f, superSample: size <= 64 ? 8 : 4);
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        payloads.Add(buffer.ToArray());
    }

    using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(file);

    writer.Write((ushort)0);            // reserved
    writer.Write((ushort)1);            // type = icon
    writer.Write((ushort)sizes.Length);  // image count

    int offset = 6 + (16 * sizes.Length);
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        // 256 用 0 表示，这是 ICO 格式的约定。
        writer.Write((byte)(size >= 256 ? 0 : size));  // width
        writer.Write((byte)(size >= 256 ? 0 : size));  // height
        writer.Write((byte)0);       // palette colors
        writer.Write((byte)0);       // reserved
        writer.Write((ushort)1);     // color planes
        writer.Write((ushort)32);    // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }

    foreach (byte[] payload in payloads)
    {
        writer.Write(payload);
    }
}

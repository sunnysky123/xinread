using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace xinread.IconGen;

/// <summary>
/// 预览模式：把图标按常见尺寸渲染出来，并放大到便于肉眼检查的比例。
/// 小尺寸图标的中缝、留白只有在像素级别检查才能发现问题，直接看 300px 大图是看不出来的。
/// </summary>
internal static class Preview
{
    /// <summary>需要检查的尺寸，覆盖标题栏、任务栏、开始菜单磁贴。</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 88, 256];

    /// <summary>放大倍数：用邻近插值放大，才能看清真实的像素分布。</summary>
    private const int Zoom = 6;

    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (int size in Sizes)
        {
            // 放大前先用高倍超采样画出「理想结果」，再看它缩到目标尺寸后的样子。
            using Bitmap actual = IconDesign.Render(size, size, plateScale: 1.0f, superSample: 16);

            int zoomed = size * Zoom;
            using var output = new Bitmap(zoomed, zoomed, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(output))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.FromArgb(0x20, 0x80, 0x80, 0x80));
                g.DrawImage(actual, 0, 0, zoomed, zoomed);
            }

            string path = Path.Combine(directory, $"preview-{size}.png");
            output.Save(path, ImageFormat.Png);
            Console.WriteLine($"preview-{size}.png  ({size}x{size}, 放大 {Zoom}x)");
        }
    }
}
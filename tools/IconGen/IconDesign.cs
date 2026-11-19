using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace xinread.IconGen;

/// <summary>
/// 用代码绘制应用图标：圆角方形底板 + 白色打开的书。
/// 所有坐标都写在 100×100 的设计画布上，渲染时等比缩放，
/// 因此任意尺寸都是同一份矢量几何，不会出现位图放大的模糊。
/// </summary>
internal static class IconDesign
{
    /// <summary>品牌主色。</summary>
    private static readonly Color Brand = Color.FromArgb(0xFF, 0x25, 0x63, 0xEB);

    /// <summary>渐变暗部，给底板一点立体感，同时不影响 24px 下的可读性。</summary>
    private static readonly Color BrandDeep = Color.FromArgb(0xFF, 0x1D, 0x4E, 0xD8);

    private const float Canvas = 100f;

    /// <summary>
    /// 把图标画到位图上。
    /// </summary>
    /// <param name="width">输出宽度（像素）。</param>
    /// <param name="height">输出高度（像素）。</param>
    /// <param name="plateScale">
    /// 底板占画布的比例。1 表示铺满（任务栏 unplated 图标），
    /// 小于 1 会在四周留出透明边距（磁贴图标）。
    /// </param>
    /// <param name="superSample">超采样倍数，先放大绘制再缩小，边缘更平滑。</param>
    public static Bitmap Render(int width, int height, float plateScale = 0.92f, int superSample = 4)
    {
        int bigW = width * superSample;
        int bigH = height * superSample;

        using var large = new Bitmap(bigW, bigH, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(large))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            // 以短边为基准等比排布，保证非正方形（如闪屏）时图标不拉伸。
            float unit = Math.Min(bigW, bigH);
            float plate = unit * plateScale;
            float left = (bigW - plate) / 2f;
            float top = (bigH - plate) / 2f;

            g.TranslateTransform(left, top);
            g.ScaleTransform(plate / Canvas, plate / Canvas);

            DrawPlate(g);
            DrawBook(g, Math.Min(width, height), compact: Math.Min(width, height) <= 24);
        }

        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(large, new Rectangle(0, 0, width, height));
        }

        return output;
    }

    /// <summary>圆角方形底板，带自上而下的轻微渐变。</summary>
    private static void DrawPlate(Graphics g)
    {
        using GraphicsPath path = RoundedRect(new RectangleF(0, 0, Canvas, Canvas), radius: 23f);
        using var brush = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(0, Canvas),
            Brand,
            BrandDeep);

        g.FillPath(brush, path);
    }

    /// <summary>
    /// 白色对开书：左右两页沿中缝留出空隙，外缘上下各有一个圆角，
    /// 整体略向下收拢，形成「书脊在中间、页面朝读者打开」的透视。
    /// </summary>
    /// <param name="renderSize">实际输出边长（像素），用于按像素反推中缝宽度。</param>
    /// <param name="compact">
    /// 小尺寸专用样式：书本更窄、留白更多、去掉柔和曲线。
    /// </param>
    private static void DrawBook(Graphics g, int renderSize, bool compact)
    {
        const float spineX = 50f;

        // gap 是「中缝中心到每页内缘」的距离，实际中缝宽度是 2*gap 个设计单位。
        // 换算成物理像素是 2*gap*renderSize/100，因此想要约 1.5px 的中缝就需要 gap ≥ 75/renderSize。
        // 大尺寸下这个下限会趋近 0，由 base 值保证视觉比例。
        float minimumGap = 75f / Math.Max(renderSize, 1);
        float gap = Math.Max(compact ? 2.2f : 1.4f, minimumGap);

        if (compact)
        {
            const float outerX = 21f;
            const float topY = 34f;
            const float bottomY = 68f;

            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, outerX, topY, spineX - gap - outerX, bottomY - topY);
            g.FillRectangle(white, spineX + gap, topY, (Canvas - outerX) - (spineX + gap), bottomY - topY);
            return;
        }

        const float bookOuterX = 19f;
        const float topOuterY = 30f;
        const float topSpineY = 34.5f;
        const float bottomSpineY = 73f;
        const float bottomOuterY = 68.5f;

        using var brush = new SolidBrush(Color.White);

        g.FillPath(brush, Page(
            spineX - gap, topSpineY,
            bookOuterX, topOuterY,
            bookOuterX, bottomOuterY,
            spineX - gap, bottomSpineY));

        g.FillPath(brush, Page(
            spineX + gap, topSpineY,
            Canvas - bookOuterX, topOuterY,
            Canvas - bookOuterX, bottomOuterY,
            spineX + gap, bottomSpineY));
    }

    /// <summary>
    /// 一页纸：中缝侧是直边，外侧上下角用贝塞尔曲线收圆，
    /// 避免小尺寸下出现尖锐的直角。
    /// </summary>
    private static GraphicsPath Page(
        float spineTopX, float spineTopY,
        float outerTopX, float outerTopY,
        float outerBottomX, float outerBottomY,
        float spineBottomX, float spineBottomY)
    {
        var path = new GraphicsPath();

        // 上边缘：从中缝向外，略微上扬
        float topMidX = (spineTopX + outerTopX) / 2f;
        path.AddBezier(
            new PointF(spineTopX, spineTopY),
            new PointF(topMidX, spineTopY - 2.2f),
            new PointF(topMidX, outerTopY - 1.2f),
            new PointF(outerTopX, outerTopY));

        // 外侧圆角 + 直边下行
        float corner = 3.4f;
        path.AddLine(new PointF(outerTopX, outerTopY), new PointF(outerTopX, outerTopY + corner));
        path.AddLine(new PointF(outerTopX, outerTopY + corner), new PointF(outerBottomX, outerBottomY - corner));

        // 下边缘：回到中缝，略向下凹
        float bottomMidX = (outerBottomX + spineBottomX) / 2f;
        path.AddBezier(
            new PointF(outerBottomX, outerBottomY),
            new PointF(bottomMidX, outerBottomY + 1.2f),
            new PointF(bottomMidX, spineBottomY + 2.2f),
            new PointF(spineBottomX, spineBottomY));

        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();

        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}

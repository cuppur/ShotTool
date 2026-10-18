using System.Drawing;

namespace ShotTool.Core;

// ============================================================================
//  Geometry.cs —— 共享几何基元与 DPI 换算（ShotTool.Core 命名空间）
//
//  坐标系契约（全项目唯一约定，任何组件不得自行改变）:
//    1) 一切几何量都是 int，单位 = 物理像素（physical pixels），不是 DIP、不是逻辑像素。
//    2) 坐标系 = 虚拟桌面坐标系（virtual desktop），原点可为负。
//         原点 = GetSystemMetrics(SM_XVIRTUALSCREEN), SM_YVIRTUALSCREEN 指向的点。
//         进程必须是 PerMonitorV2（见 app.manifest），此时 GetSystemMetrics /
//         EnumDisplayMonitors / 鼠标消息坐标全部以物理像素表达，无需换算。
//    3) 矩形半开区间：[Left, Right) × [Top, Bottom)。
//         Right == Left + Width，Bottom == Top + Height。
//         Contains(p) 在右/下边界上返回 false；绘制 1px 边框时这一点很重要。
//    4) overlay 窗口不变量：窗口 Bounds == CapturedFrame.VirtualDesktopBounds，
//         因此 overlay 的客户区坐标 ≡ 虚拟桌面坐标，任何几何量可直传，不做平移。
//
//  与 dev-editor 既有类型的映射关系（重要，避免混淆）:
//    ShotTool.Core.PhysPoint / PhysSize / PhysRect  —— int，物理像素（本文件，契约用）
//    ShotTool.Editor.PhysPoint / PhysSize / PhysRect —— double，Editor 内部算法用
//    两者字段名一致（X/Y/Width/Height/Left/Top/Right/Bottom），互转是逐字段拷贝 + 取整。
//    C# 名字解析规则保证 ShotTool.Editor.* 内部的 `PhysRect` 解析到 Editor 版本，
//    不会与本命名空间冲突；跨层调用时显式限定即可，参考 Core/README.md。
//
//  唯一需要 DIP 的地方是「UI 元素尺寸」（工具条按钮、圆角、图标、字号）。
//  这些用 DpiHelper.DipToPx(dip, monitorDpi) 换算，绝不写死像素。
// ============================================================================

/// <summary>整数点（物理像素，虚拟桌面坐标系）。</summary>
public readonly struct PhysPoint : IEquatable<PhysPoint>
{
    public readonly int X;
    public readonly int Y;

    public PhysPoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public static PhysPoint Zero => default;

    public bool IsZero => X == 0 && Y == 0;

    public PhysPoint Offset(int dx, int dy) => new(X + dx, Y + dy);

    public PhysPoint Offset(PhysSize delta) => new(X + delta.Width, Y + delta.Height);

    public double DistanceTo(PhysPoint other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>点到点的整数距离平方（避免开方，用于命中半径比较）。</summary>
    public long DistanceSquaredTo(PhysPoint other)
    {
        long dx = X - other.X;
        long dy = Y - other.Y;
        return dx * dx + dy * dy;
    }

    public Point ToPoint() => new(X, Y);

    public static PhysPoint FromPoint(Point p) => new(p.X, p.Y);

    /// <summary>Editor 侧 double 几何 → 契约 int 几何（四舍五入）。</summary>
    public static PhysPoint From(double x, double y)
        => new((int)Math.Round(x, MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero));

    public bool Equals(PhysPoint other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is PhysPoint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public static bool operator ==(PhysPoint a, PhysPoint b) => a.Equals(b);

    public static bool operator !=(PhysPoint a, PhysPoint b) => !a.Equals(b);

    public override string ToString() => $"{X},{Y}";
}

/// <summary>整数尺寸（物理像素）。</summary>
public readonly struct PhysSize : IEquatable<PhysSize>
{
    public readonly int Width;
    public readonly int Height;

    public PhysSize(int width, int height)
    {
        Width = Math.Abs(width);
        Height = Math.Abs(height);
    }

    public static PhysSize Empty => default;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => (long)Width * Height;

    public Size ToSize() => new(Width, Height);

    public static PhysSize FromSize(Size s) => new(s.Width, s.Height);

    public static PhysSize From(double width, double height)
        => new(
            (int)Math.Round(width, MidpointRounding.AwayFromZero),
            (int)Math.Round(height, MidpointRounding.AwayFromZero));

    public bool Equals(PhysSize other) => Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is PhysSize other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public static bool operator ==(PhysSize a, PhysSize b) => a.Equals(b);

    public static bool operator !=(PhysSize a, PhysSize b) => !a.Equals(b);

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// 整数矩形（物理像素，虚拟桌面坐标系，半开区间 [Left,Right) × [Top,Bottom)）。
/// 构造时对负宽高做归一化，因此 <see cref="Width"/> / <see cref="Height"/> 永不为负。
/// </summary>
public readonly struct PhysRect : IEquatable<PhysRect>
{
    public readonly int X;
    public readonly int Y;
    public readonly int Width;
    public readonly int Height;

    public PhysRect(int x, int y, int width, int height)
    {
        if (width < 0)
        {
            x += width;
            width = -width;
        }

        if (height < 0)
        {
            y += height;
            height = -height;
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public PhysRect(PhysPoint location, PhysSize size)
        : this(location.X, location.Y, size.Width, size.Height)
    {
    }

    public static PhysRect Empty => default;

    /// <summary>由四条边构造（常用于 Intersect/Union）。</summary>
    public static PhysRect FromEdges(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>由任意两个对角点构造，自动归一化（框选、椭圆外接框都走这里）。</summary>
    public static PhysRect FromPoints(PhysPoint a, PhysPoint b) => new(a.X, a.Y, b.X - a.X, b.Y - a.Y);

    /// <summary>Editor 侧 double 几何 → 契约 int 几何。</summary>
    public static PhysRect From(double x, double y, double width, double height)
        => new(
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(y, MidpointRounding.AwayFromZero),
            (int)Math.Round(width, MidpointRounding.AwayFromZero),
            (int)Math.Round(height, MidpointRounding.AwayFromZero));

    public int Left => X;

    public int Top => Y;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public PhysPoint Location => new(X, Y);

    public PhysSize Size => new(Width, Height);

    /// <summary>中心点（整数除法；绘制控制柄时用）。</summary>
    public PhysPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => (long)Width * Height;

    public bool Contains(PhysPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool Contains(PhysRect r) => r.Left >= Left && r.Right <= Right && r.Top >= Top && r.Bottom <= Bottom;

    public bool IntersectsWith(PhysRect r)
        => r.Left < Right && Left < r.Right && r.Top < Bottom && Top < r.Bottom;

    public PhysRect Intersect(PhysRect r)
    {
        if (!IntersectsWith(r))
        {
            return Empty;
        }

        return FromEdges(
            Math.Max(Left, r.Left),
            Math.Max(Top, r.Top),
            Math.Min(Right, r.Right),
            Math.Min(Bottom, r.Bottom));
    }

    public PhysRect Union(PhysRect r)
    {
        if (IsEmpty)
        {
            return r;
        }

        if (r.IsEmpty)
        {
            return this;
        }

        return FromEdges(
            Math.Min(Left, r.Left),
            Math.Min(Top, r.Top),
            Math.Max(Right, r.Right),
            Math.Max(Bottom, r.Bottom));
    }

    public PhysRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public PhysRect Offset(PhysPoint delta) => new(X + delta.X, Y + delta.Y, Width, Height);

    /// <summary>四边各外扩 d 像素（负数 = 收缩）。把「几何包围盒」变成「含笔宽的实际涂抹区」。</summary>
    public PhysRect Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public PhysRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public PhysRect Deflate(int d) => Inflate(-d);

    /// <summary>把矩形夹进 limit（把选区限制在虚拟桌面内）。</summary>
    public PhysRect Clamp(PhysRect limit)
    {
        PhysRect hit = Intersect(limit);
        return hit.IsEmpty ? Empty : hit;
    }

    /// <summary>保证宽高不低于 min（resize 时的最小选区，避免 0 宽高）。</summary>
    public PhysRect EnsureMinimumSize(PhysSize min)
        => new(X, Y, Math.Max(Width, min.Width), Math.Max(Height, min.Height));

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static PhysRect FromRectangle(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    public bool Equals(PhysRect other)
        => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is PhysRect other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(PhysRect a, PhysRect b) => a.Equals(b);

    public static bool operator !=(PhysRect a, PhysRect b) => !a.Equals(b);

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}

/// <summary>
/// 亚像素矩形（double，物理像素单位）。
///
/// 存在理由：dev-editor 的标注模型内部用 double 几何（笔迹、箭头端点、马赛克块边界
/// 在缩放/旋转/插值时会产生小数），强制转 int 会丢精度、导致视觉抖动。
/// 因此 <see cref="IAnnotation.VisualBounds"/> 用本类型承载实现方的原始精度，
/// 而契约层的整数接口 <see cref="IAnnotation.Bounds"/> 由默认实现派生。
///
/// 与 <see cref="PhysRect"/> 的互转是四舍五入（AwayFromZero），不是截断。
/// </summary>
public readonly struct PhysRectF : IEquatable<PhysRectF>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;

    public PhysRectF(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static PhysRectF Empty => default;

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2.0);

    public double CenterY => Y + (Height / 2.0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>转到契约整数接口（四舍五入，保证 0.5 边界不抖动）。</summary>
    public PhysRect ToPhysRect() => PhysRect.From(X, Y, Width, Height);

    public static PhysRectF FromPhysRect(PhysRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    public PhysRectF Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public PhysRectF Inflate(double d) => new(X - d, Y - d, Width + (2 * d), Height + (2 * d));

    public bool Contains(double px, double py)
        => px >= Left && px < Right && py >= Top && py < Bottom;

    public bool Equals(PhysRectF other)
        => X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is PhysRectF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(PhysRectF a, PhysRectF b) => a.Equals(b);

    public static bool operator !=(PhysRectF a, PhysRectF b) => !a.Equals(b);

    public override string ToString() => $"[{X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}]";
}

/// <summary>
/// 选区的可拖拽命中部位。框选完成后，用户可拖拽 8 个控制柄调整四边/四角，
/// 拖拽 <see cref="Body"/> 平移整个选区。
/// </summary>
public enum SelectionHandle
{
    /// <summary>没命中选区。</summary>
    None = 0,

    /// <summary>命中选区内部（非控制柄）→ 平移。</summary>
    Body,

    NW,
    N,
    NE,
    E,
    SE,
    S,
    SW,

    /// <summary>西边中点。</summary>
    W,
}

/// <summary>选区命中测试与拖拽求解（纯几何，无 UI 依赖，Editor 直接用）。</summary>
public static class SelectionGeometry
{
    /// <summary>
    /// 命中测试。handlePx 是控制柄命中方块的边长（物理像素，调用方按 DPI 换算）。
    /// 角优先于边（角上那 1/4 区域命中角更符合直觉）。
    /// </summary>
    public static SelectionHandle HitTest(PhysRect selection, PhysPoint p, int handlePx)
    {
        if (selection.IsEmpty)
        {
            return SelectionHandle.None;
        }

        int half = Math.Max(1, handlePx / 2);

        bool inX = p.X >= selection.Left - half && p.X <= selection.Right + half;
        bool inY = p.Y >= selection.Top - half && p.Y <= selection.Bottom + half;
        if (!inX || !inY)
        {
            return SelectionHandle.None;
        }

        bool nearLeft = Math.Abs(p.X - selection.Left) <= half;
        bool nearRight = Math.Abs(p.X - selection.Right) <= half;
        bool nearTop = Math.Abs(p.Y - selection.Top) <= half;
        bool nearBottom = Math.Abs(p.Y - selection.Bottom) <= half;

        if (nearTop && nearLeft)
        {
            return SelectionHandle.NW;
        }

        if (nearTop && nearRight)
        {
            return SelectionHandle.NE;
        }

        if (nearBottom && nearLeft)
        {
            return SelectionHandle.SW;
        }

        if (nearBottom && nearRight)
        {
            return SelectionHandle.SE;
        }

        if (nearTop)
        {
            return SelectionHandle.N;
        }

        if (nearBottom)
        {
            return SelectionHandle.S;
        }

        if (nearLeft)
        {
            return SelectionHandle.W;
        }

        if (nearRight)
        {
            return SelectionHandle.E;
        }

        // 用选区本身（不是外扩区）判断内部，避免外扩容差把「选区外 2px」当成平移
        return selection.Contains(p) ? SelectionHandle.Body : SelectionHandle.None;
    }

    /// <summary>
    /// 按拖拽增量求新选区。拖边只动一条边，拖角动两条边，拖 Body 平移。
    /// 结果已归一化（拖过头不产生负宽高）并按 minSize 兜底，且被 limit 裁剪。
    /// </summary>
    public static PhysRect ResizeBy(
        PhysRect selection,
        SelectionHandle handle,
        int dx,
        int dy,
        PhysSize minSize,
        PhysRect limit)
    {
        if (handle == SelectionHandle.None)
        {
            return selection;
        }

        if (handle == SelectionHandle.Body)
        {
            return ClampPosition(selection.Offset(dx, dy), limit);
        }

        int left = selection.Left;
        int top = selection.Top;
        int right = selection.Right;
        int bottom = selection.Bottom;

        if (handle is SelectionHandle.NW or SelectionHandle.W or SelectionHandle.SW)
        {
            left += dx;
        }

        if (handle is SelectionHandle.NE or SelectionHandle.E or SelectionHandle.SE)
        {
            right += dx;
        }

        if (handle is SelectionHandle.NW or SelectionHandle.N or SelectionHandle.NE)
        {
            top += dy;
        }

        if (handle is SelectionHandle.SW or SelectionHandle.S or SelectionHandle.SE)
        {
            bottom += dy;
        }

        left = Math.Clamp(left, limit.Left, limit.Right);
        right = Math.Clamp(right, limit.Left, limit.Right);
        top = Math.Clamp(top, limit.Top, limit.Bottom);
        bottom = Math.Clamp(bottom, limit.Top, limit.Bottom);

        PhysRect result = PhysRect.FromEdges(left, top, right, bottom).EnsureMinimumSize(minSize);
        return result.Clamp(limit);
    }

    /// <summary>平移时不让选区跑出 limit（保留尺寸，贴着边走）。</summary>
    public static PhysRect ClampPosition(PhysRect rect, PhysRect limit)
    {
        if (rect.Width > limit.Width || rect.Height > limit.Height)
        {
            return rect.Clamp(limit);
        }

        int x = Math.Clamp(rect.X, limit.Left, limit.Right - rect.Width);
        int y = Math.Clamp(rect.Y, limit.Top, limit.Bottom - rect.Height);
        return new PhysRect(x, y, rect.Width, rect.Height);
    }

    /// <summary>最小选区尺寸：4×4 物理像素。低于此值视为误触，框选结束时丢弃。</summary>
    public static PhysSize DefaultMinimumSelection { get; } = new(4, 4);

    /// <summary>8 个控制柄的中心点（绘制与命中都用这一套，保证一致）。顺序：NW,N,NE,E,SE,S,SW,W。</summary>
    public static void GetHandleCenters(PhysRect selection, Span<PhysPoint> buffer)
    {
        if (buffer.Length < 8)
        {
            throw new ArgumentException("buffer must hold 8 points", nameof(buffer));
        }

        PhysPoint center = selection.Center;
        buffer[0] = new PhysPoint(selection.Left, selection.Top);
        buffer[1] = new PhysPoint(center.X, selection.Top);
        buffer[2] = new PhysPoint(selection.Right, selection.Top);
        buffer[3] = new PhysPoint(selection.Right, center.Y);
        buffer[4] = new PhysPoint(selection.Right, selection.Bottom);
        buffer[5] = new PhysPoint(center.X, selection.Bottom);
        buffer[6] = new PhysPoint(selection.Left, selection.Bottom);
        buffer[7] = new PhysPoint(selection.Left, center.Y);
    }
}

/// <summary>
/// DPI 换算。项目内唯一允许做「DIP ↔ 物理像素」转换的地方。
/// 除 UI 尺寸之外的任何几何量都应是物理像素，不需要经过这里。
/// </summary>
public static class DpiHelper
{
    public const uint DefaultDpi = 96;

    /// <summary>参考 DPI（Windows 默认 100% 缩放）。</summary>
    public const double ReferenceDpi = 96.0;

    public static int DipToPx(double dip, uint dpi)
        => (int)Math.Round(dip * dpi / ReferenceDpi, MidpointRounding.AwayFromZero);

    public static double PxToDip(int px, uint dpi)
        => px * ReferenceDpi / dpi;

    public static PhysSize DipToPx(PhysSize dipSize, uint dpi)
        => new(DipToPx(dipSize.Width, dpi), DipToPx(dipSize.Height, dpi));

    /// <summary>缩放系数（1.0 = 100%，2.25 = 225%）。</summary>
    public static double ScaleFactor(uint dpi) => dpi / ReferenceDpi;

    /// <summary>把 DPI 规整到合法范围，避免 0 或异常值导致除零。</summary>
    public static uint Sanitize(uint dpi) => dpi is < 48 or > 960 ? DefaultDpi : dpi;
}

// 工具条宿主：实现 Core.IToolbarHost（Dpi / Rebuild / Invalidate / ShowPanel / ShowToast）。
//
// 边界（captain 裁决）：**Editor 不建 overlay 窗口**。窗口、失效重绘、Toast 呈现、
// 面板控件的父子挂载都由 overlay（dev-capture 的 SelectionOverlayForm）提供，
// Editor 只通过下面的 IToolbarHostSurface 反向调用 —— 因此不产生 Editor → App 的反向依赖。
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>
    /// 由 overlay 实现的宿主表面（由 Editor 定义、overlay 实现，方向：overlay → Editor 提供能力）。
    /// 全部成员在 UI 线程访问。
    /// </summary>
    public interface IToolbarHostSurface
    {
        /// <summary>当前显示器 DPI（96 = 100%）。</summary>
        uint Dpi { get; }

        /// <summary>当前选区（物理像素，虚拟桌面坐标）。</summary>
        PhysRect Selection { get; }

        /// <summary>选区所在显示器的工作区（物理像素）。</summary>
        PhysRect WorkArea { get; }

        /// <summary>工具条当前矩形（物理像素）；尚未布局时返回 Empty。</summary>
        PhysRectF ToolbarBounds { get; }

        /// <summary>让工具条区域失效（overlay 内部 Invalidate）。</summary>
        void InvalidateToolbar();

        /// <summary>轻提示（约 2 秒自动消失，不抢焦点）。</summary>
        void ShowToast(string text);

        /// <summary>把面板控件挂到 overlay 上（Add 到 Controls 并设置 Bounds 由 overlay 负责）。</summary>
        void AttachPanel(ToolbarPanelHost panel);

        /// <summary>把面板控件从 overlay 移除（overlay 负责 Remove + Dispose 宿主，Content 不 Dispose）。</summary>
        void DetachPanel(ToolbarPanelHost panel);
    }

    /// <summary>工具条宿主实现（面板生命周期 + 扩展按钮重建 + 失效）。</summary>
    public sealed class ToolbarHost : IToolbarHost, IDisposable
    {
        private readonly IToolbarHostSurface _surface;
        private readonly ToolbarState _state;
        private readonly IToolbarRegistry _registry;
        private readonly Func<ToolbarContext?>? _contextProvider;

        private ToolbarPanelHost? _panel;
        private bool _disposed;

        /// <param name="surface">overlay 提供的宿主表面。</param>
        /// <param name="state">工具条状态（由 overlay/Editor 持有）。</param>
        /// <param name="registry">扩展按钮注册表（Editor 的 ToolbarRegistry）。</param>
        /// <param name="contextProvider">可选：提供 ToolbarContext 给 IToolbarItem.IsVisible 使用；null 时视为全部可见。</param>
        public ToolbarHost(
            IToolbarHostSurface surface,
            ToolbarState state,
            IToolbarRegistry registry,
            Func<ToolbarContext?>? contextProvider = null)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _contextProvider = contextProvider;
        }

        /// <summary>当前挂出的面板（null = 无）。</summary>
        public ToolbarPanelHost? CurrentPanel
        {
            get { return _panel; }
        }

        public uint Dpi
        {
            get { return _surface.Dpi; }
        }

        public double DpiScale
        {
            get
            {
                uint dpi = _surface.Dpi;
                return dpi > 0 ? dpi / DpiHelper.ReferenceDpi : 1.0;
            }
        }

        /// <summary>重新解析 IToolbarRegistry，把扩展按钮合并进工具条状态，然后失效重绘。</summary>
        public void Rebuild()
        {
            if (_disposed)
            {
                return;
            }

            // 1) 移除旧的扩展项（id 以 "ext." 开头）
            List<ToolbarItem> baseItems = new List<ToolbarItem>();
            IReadOnlyList<ToolbarItem> items = _state.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].Id.StartsWith("ext.", StringComparison.Ordinal))
                {
                    baseItems.Add(items[i]);
                }
            }

            // 2) 解析扩展按钮
            List<ToolbarItem> extensionItems = new List<ToolbarItem>();
            ToolbarContext? context = null;
            if (_contextProvider != null)
            {
                try
                {
                    context = _contextProvider();
                }
                catch (Exception)
                {
                    context = null;
                }
            }

            IReadOnlyList<IToolbarItem> resolved;
            try
            {
                resolved = _registry.Resolve(context!);
            }
            catch (Exception)
            {
                resolved = Array.Empty<IToolbarItem>();
            }

            for (int i = 0; i < resolved.Count; i++)
            {
                IToolbarItem item = resolved[i];
                ToolbarIconKey icon = MapGlyph(item.Glyph);
                ToolbarItem entry = new ToolbarItem("ext." + item.Id, item.Tooltip ?? string.Empty, icon, ToolbarItemRole.Extension, null);
                entry.Extension = item;
                try
                {
                    entry.Enabled = item.IsEnabled(context!);
                    entry.IsToggle = item.IsToggle;
                    entry.Checked = item.IsChecked;
                }
                catch (Exception)
                {
                    entry.Enabled = true;
                }

                extensionItems.Add(entry);
            }

            // 3) 重建：扩展项插在第一个 action.save 之前（= 动作区靠前的扩展位），否则追加到末尾
            int insertAt = -1;
            for (int i = 0; i < baseItems.Count; i++)
            {
                if (string.Equals(baseItems[i].Id, "action.undo", StringComparison.Ordinal))
                {
                    insertAt = i;
                    break;
                }
            }

            List<ToolbarItem> merged = new List<ToolbarItem>(baseItems.Count + extensionItems.Count);
            for (int i = 0; i < baseItems.Count; i++)
            {
                if (i == insertAt)
                {
                    merged.AddRange(extensionItems);
                }

                merged.Add(baseItems[i]);
            }

            if (insertAt < 0)
            {
                merged.AddRange(extensionItems);
            }

            _state.Clear();
            for (int i = 0; i < merged.Count; i++)
            {
                _state.Add(merged[i]);
            }

            Invalidate();
        }

        public void Invalidate()
        {
            if (_disposed)
            {
                return;
            }

            _surface.InvalidateToolbar();
        }

        /// <summary>挂出面板（null = 关闭当前面板）。同一时刻只有一个面板。</summary>
        public void ShowPanel(ToolbarPanelRequest? request)
        {
            if (_disposed)
            {
                return;
            }

            ClosePanelNow();
            if (request == null)
            {
                return;
            }

            ToolbarPanelHost host;
            try
            {
                host = new ToolbarPanelHost(request, DpiScale);
            }
            catch (Exception)
            {
                ShowToast("面板打开失败");
                return;
            }

            PhysRectF panelBounds = ToolbarPanelHost.ComputeBounds(
                request,
                _surface.ToolbarBounds,
                _surface.Selection,
                host.DesiredSizePhys,
                _surface.WorkArea,
                DpiScale);

            host.Bounds = panelBounds.ToCoreRect().ToRectangle();
            host.Closed += OnPanelClosed;
            _panel = host;

            try
            {
                _surface.AttachPanel(host);
            }
            catch (Exception)
            {
                _panel = null;
                host.Closed -= OnPanelClosed;
                host.Dispose();
                ShowToast("面板挂载失败");
            }
        }

        public void ShowToast(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text))
            {
                return;
            }

            _surface.ShowToast(text);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClosePanelNow();
        }

        /// <summary>把 Core.ToolbarGlyph 映射到自绘图标；未实现的图标返回 None（不绘制）。</summary>
        public static ToolbarIconKey MapGlyph(ToolbarGlyph glyph)
        {
            switch (glyph)
            {
                case ToolbarGlyph.Rect:
                    return ToolbarIconKey.Rect;
                case ToolbarGlyph.Ellipse:
                    return ToolbarIconKey.Ellipse;
                case ToolbarGlyph.Arrow:
                    return ToolbarIconKey.Arrow;
                case ToolbarGlyph.Pen:
                    return ToolbarIconKey.Pen;
                case ToolbarGlyph.Mosaic:
                    return ToolbarIconKey.Mosaic;
                case ToolbarGlyph.Text:
                    return ToolbarIconKey.Text;
                case ToolbarGlyph.Number:
                    return ToolbarIconKey.Number;
                case ToolbarGlyph.Undo:
                    return ToolbarIconKey.Undo;
                case ToolbarGlyph.Redo:
                    return ToolbarIconKey.Redo;
                case ToolbarGlyph.Copy:
                    return ToolbarIconKey.Copy;
                case ToolbarGlyph.Save:
                    return ToolbarIconKey.Save;
                case ToolbarGlyph.Pin:
                    return ToolbarIconKey.Pin;
                case ToolbarGlyph.Close:
                    return ToolbarIconKey.Close;
                case ToolbarGlyph.Ocr:
                    return ToolbarIconKey.Ocr;
                case ToolbarGlyph.Translate:
                    return ToolbarIconKey.Translate;
                default:
                    // AiMask / Collapse：暂未自绘，返回 None（ToolbarIcons 会跳过）
                    return ToolbarIconKey.None;
            }
        }

        private void ClosePanelNow()
        {
            ToolbarPanelHost? panel = _panel;
            if (panel == null)
            {
                return;
            }

            _panel = null;
            panel.Closed -= OnPanelClosed;
            try
            {
                _surface.DetachPanel(panel);
            }
            catch (Exception)
            {
                // 移除失败不影响状态
            }

            panel.Dispose();
        }

        private void OnPanelClosed(object? sender, EventArgs e)
        {
            ToolbarPanelHost? panel = sender as ToolbarPanelHost;
            if (panel == null || !ReferenceEquals(panel, _panel))
            {
                return;
            }

            _panel = null;
            panel.Closed -= OnPanelClosed;
            try
            {
                _surface.DetachPanel(panel);
            }
            catch (Exception)
            {
                // 同上
            }

            panel.Dispose();
            Invalidate();
        }
    }
}

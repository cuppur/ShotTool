// 扩展面板容器：把 Core.ToolbarPanelRequest.Content 包一层标题条 + 关闭按钮，按 PanelPlacement 落位。
//
// 所有权规则（Core 注释原文）：**Content 由调用方（dev-ai）创建并拥有**；
// 本容器关闭时只 Dispose 宿主自身，绝不 Dispose Content；关闭时触发 request.Closed 回调。
#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>面板宿主（WinForms 容器控件，由 overlay 直接 Add 到 Controls）。</summary>
    public sealed class ToolbarPanelHost : Panel
    {
        private readonly ToolbarPanelRequest _request;
        private readonly Control _content;
        private readonly Label _title;
        private readonly Button _close;
        private bool _closed;

        public ToolbarPanelHost(ToolbarPanelRequest request, double dpiScale)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            int titleHeight = (int)Math.Round(28.0 * scale);

            // 注意：Panel 继承 ScrollableControl，**没有 AutoScaleMode**（那是 ContainerControl/Form 的成员），
            // 所以这里不需要关自动缩放 —— 本控件的全部尺寸已按物理像素自行换算。

            BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA);
            BorderStyle = BorderStyle.FixedSingle;
            Margin = new Padding(0);

            _title = new Label
            {
                Text = request.Title ?? string.Empty,
                Dock = DockStyle.Left,
                Width = Math.Max(1, (int)Math.Round(120.0 * scale)),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding((int)Math.Round(8.0 * scale), 0, 0, 0),
                ForeColor = Color.FromArgb(0x4C, 0x4C, 0x4C),
                Font = new Font(Font.FontFamily, (float)(12.0 * scale), FontStyle.Regular, GraphicsUnit.Pixel),
            };

            _close = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = titleHeight,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                ForeColor = Color.FromArgb(0x4C, 0x4C, 0x4C),
                BackColor = Color.Transparent,
            };
            _close.FlatAppearance.BorderSize = 0;
            _close.Click += (sender, args) => ClosePanel();

            Panel titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = titleHeight,
                BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0),
                Padding = new Padding(0),
            };
            titleBar.Controls.Add(_title);
            titleBar.Controls.Add(_close);

            // 异常隔离：Content 的 PreferredSize 抛异常时用兜底尺寸，绝不让宿主构造失败
            _content = request.Content;
            _content.Dock = DockStyle.Fill;
            _content.Margin = new Padding(0);

            Controls.Add(_content);
            Controls.Add(titleBar);

            PhysSizeF desired = ResolveDesiredSize(request, scale);
            DesiredSizePhys = desired;
            Size = new Size((int)Math.Round(desired.Width), (int)Math.Round(desired.Height));
        }

        /// <summary>面板关闭（宿主自己收起后由 overlay 移除并 Dispose 本控件；Content 不 Dispose）。</summary>
        public event EventHandler? Closed;

        /// <summary>期望尺寸（物理像素；已含标题条）。</summary>
        public PhysSizeF DesiredSizePhys { get; private set; }

        public void ClosePanel()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Visible = false;
            try
            {
                Action? callback = _request.Closed;
                if (callback != null)
                {
                    callback();
                }
            }
            catch (Exception)
            {
                // 调用方回调异常不影响面板关闭
            }

            EventHandler? handler = Closed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 计算面板落位（物理像素）。selection 为选区矩形，toolbar 为工具条矩形，workArea 为显示器工作区。
        /// </summary>
        public static PhysRectF ComputeBounds(
            ToolbarPanelRequest request,
            PhysRectF toolbarBounds,
            PhysRect selection,
            PhysSizeF desiredPhys,
            PhysRect workArea,
            double dpiScale)
        {
            if (request == null)
            {
                return PhysRectF.Empty;
            }

            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            double gap = 8.0 * scale;

            double maxHeight = Math.Max(1, workArea.Height - gap * 2);
            double height = Math.Min(desiredPhys.Height, maxHeight);
            double width = desiredPhys.Width;

            PhysRectF bounds;
            switch (request.Placement)
            {
                case PanelPlacement.Right:
                {
                    // 小选区不应把结果面板压到无法显示文字和操作按钮。
                    double maxWidth = Math.Max(1, workArea.Width - gap * 2);
                    width = Math.Min(width, maxWidth);
                    double x = selection.Right + gap;
                    if (x + width > workArea.Right)
                    {
                        x = Math.Max(workArea.Left, selection.Left - gap - width);
                    }

                    bounds = new PhysRectF(x, selection.Top, width, height);
                    break;
                }

                case PanelPlacement.Floating:
                {
                    double x = request.Anchor.X;
                    double y = request.Anchor.Y;
                    bounds = new PhysRectF(x, y, width, height);
                    break;
                }

                default:
                {
                    // Bottom：贴在选区下方，宽度跟选区走
                    width = Math.Min(Math.Max(240.0 * scale, selection.Width), workArea.Width - gap * 2);
                    double x = selection.Left;
                    double y = selection.Bottom + gap;
                    if (y + height > workArea.Bottom)
                    {
                        y = Math.Max(workArea.Top, selection.Top - gap - height);
                    }

                    bounds = new PhysRectF(x, y, width, height);
                    break;
                }
            }

            // 夹进工作区
            double clampedX = Math.Min(Math.Max(bounds.X, workArea.Left), Math.Max(workArea.Left, workArea.Right - bounds.Width));
            double clampedY = Math.Min(Math.Max(bounds.Y, workArea.Top), Math.Max(workArea.Top, workArea.Bottom - bounds.Height));
            return new PhysRectF(clampedX, clampedY, bounds.Width, bounds.Height);
        }

        private static PhysSizeF ResolveDesiredSize(ToolbarPanelRequest request, double scale)
        {
            double titleHeight = 28.0 * scale;
            double fallbackWidth = 320.0 * scale;
            double fallbackHeight = 240.0 * scale;

            try
            {
                PhysSize dip = request.PreferredSizeDip;
                if (!dip.IsEmpty)
                {
                    // 输入是 DIP（96 基准），换算成物理像素
                    double width = dip.Width * scale;
                    double height = dip.Height * scale + titleHeight;
                    return new PhysSizeF(Math.Max(160.0 * scale, width), Math.Max(titleHeight + 80.0 * scale, height));
                }

                Size preferred = request.Content.PreferredSize;
                if (preferred.Width > 0 && preferred.Height > 0)
                {
                    return new PhysSizeF(preferred.Width, preferred.Height + titleHeight);
                }
            }
            catch (Exception)
            {
                // 落到兜底尺寸
            }

            return new PhysSizeF(fallbackWidth, fallbackHeight + titleHeight);
        }
    }
}

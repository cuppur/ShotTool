// 文字标注输入窗：**独立 owned 小窗**（不进 Alt+Tab、不显示任务栏按钮），输入完由调用方 overlay.Refocus()。
//
// 与 architect 约定的偏差（需同步）：
//   * 用 WS_EX_TOOLWINDOW（隐藏任务栏/Alt+Tab）—— 必须；
//   * **不用** WS_EX_NOACTIVATE：该样式让窗口无法获得键盘焦点，TextBox 收不到按键，文字根本打不进去。
//     文字输入窗必须可被激活；提交/取消后由宿主 SetForegroundWindow(overlay.Handle) 把焦点还回去。
#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>单行/多行文字输入窗（无边框、TopMost、物理像素定位）。</summary>
    public sealed class TextInputForm : Form
    {
        private readonly TextBox _textBox;
        private readonly Font _font;
        private bool _committed;

        private TextInputForm(string initial, double fontSizePhys, Color foreColor, PhysRect boundsPhys, double dpiScale)
        {
            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            _font = TextMeasurer.CreateFont(TextAnnotation.DefaultFontFamily, fontSizePhys);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA);
            Padding = new Padding(0);

            int paddingPhys = (int)Math.Round(4.0 * scale);
            int minWidth = (int)Math.Round(240.0 * scale);
            int lineHeight = (int)Math.Ceiling(_font.GetHeight());
            int width = Math.Max(minWidth, boundsPhys.Width + paddingPhys * 2);
            int height = Math.Max(lineHeight + paddingPhys * 2, boundsPhys.Height + paddingPhys * 2);
            Bounds = new Rectangle(boundsPhys.X, boundsPhys.Y, width, height);

            _textBox = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.None,
                WordWrap = false,
                BackColor = BackColor,
                ForeColor = foreColor,
                Font = _font,
                Dock = DockStyle.Fill,
                Text = initial ?? string.Empty,
                Margin = new Padding(0),
            };

            _textBox.KeyDown += OnTextBoxKeyDown;
            _textBox.LostFocus += OnTextBoxLostFocus;
            Controls.Add(_textBox);
        }

        /// <summary>提交后的文本；取消时为 null。空文本按取消处理。</summary>
        public string? Result { get; private set; }

        /// <summary>
        /// 在指定物理像素位置弹出输入窗并阻塞直到提交/取消。
        /// owner 传 overlay 窗口（保证归属关系与 Z 序）；返回 null 表示用户取消或未输入。
        /// </summary>
        public static string? Edit(
            IWin32Window? owner,
            PhysRect boundsPhys,
            double fontSizePhys,
            uint argb,
            string? initial,
            double dpiScale)
        {
            Color color = Color.FromArgb(unchecked((int)argb));
            using (TextInputForm form = new TextInputForm(initial ?? string.Empty, fontSizePhys, color, boundsPhys, dpiScale))
            {
                if (owner != null)
                {
                    form.ShowDialog(owner);
                }
                else
                {
                    form.ShowDialog();
                }

                return string.IsNullOrEmpty(form.Result) ? null : form.Result;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _font.Dispose();
            }
        }

        private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Commit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Cancel();
            }
        }

        private void OnTextBoxLostFocus(object? sender, EventArgs e)
        {
            // 失焦：有内容视为提交，无内容视为取消（避免留下空标注）
            if (_committed)
            {
                return;
            }

            if (string.IsNullOrEmpty(_textBox.Text))
            {
                Cancel();
            }
            else
            {
                Commit();
            }
        }

        private void Commit()
        {
            if (_committed)
            {
                return;
            }

            _committed = true;
            Result = _textBox.Text;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Cancel()
        {
            if (_committed)
            {
                return;
            }

            _committed = true;
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}

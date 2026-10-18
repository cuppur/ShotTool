using System.Drawing.Imaging;
using ShotTool.Core;
using ShotTool.Settings;

namespace ShotTool.App;

internal static class SettingsSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        try
        {
            using var store = new JsonSettingsStore(Path.Combine(directory, "settings-fixture.json"), log, watch: false);
            foreach (uint dpi in new uint[] { 96, 144, 216 })
            {
                using var form = new SettingsForm(store, null, dpi) { DiagnosticMode = true };
                form.Location = new Point(-20000, -20000);
                form.Show();
                var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
                for (int page = 0; page < tabs.TabCount; page++)
                {
                    tabs.SelectedIndex = page;
                    form.PerformLayout();
                    Application.DoEvents();
                    Check(tabs.SelectedTab!, dpi);
                    if (dpi == 216 || dpi == 96 && page == 0)
                        Save(form, Path.Combine(directory, $"settings-{dpi}-{page}.png"));
                    CheckFooter(form);
                }
                form.Size = form.MinimumSize;
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    form.PerformLayout();
                    Application.DoEvents();
                    Check(page, dpi);
                    CheckFooter(form);
                }
                if (dpi == 216) Save(form, Path.Combine(directory, "settings-216-minimum.png"));
                form.Close();
                Console.WriteLine($"[PASS] Settings DPI {dpi}: 4 pages; inputs / checkbox text / buttons fit; fixed footer; minimum-size scrolling");
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Error("设置布局检查失败", ex);
            Console.WriteLine("[FAIL] Settings: " + ex.Message);
            return false;
        }
    }

    private static void Check(Control root, uint dpi)
    {
        foreach (Control control in root.Controls)
        {
            if (!control.Visible) continue;
            if (control is TextBox or ComboBox or Button or CheckBox)
            {
                if (control.Left < 0 || control.Right > root.ClientSize.Width + 1)
                    throw new InvalidOperationException($"控件超出容器：{control.GetType().Name} {control.Text} {control.Bounds} / {root.ClientSize}");
            }
            if (control is CheckBox check)
            {
                var preferred = check.GetPreferredSize(Size.Empty);
                if (check.Width < preferred.Width || check.Height < preferred.Height)
                    throw new InvalidOperationException("复选框文字裁切：" + check.Text);
            }
            if (control is Button button)
            {
                var textSize = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding);
                if (button.ClientSize.Width < textSize.Width || button.ClientSize.Height < textSize.Height)
                    throw new InvalidOperationException("按钮文字裁切：" + button.Text);
            }
            Check(control, dpi);
        }
        if (root is TabPage page && page.HorizontalScroll.Visible)
            throw new InvalidOperationException("设置页产生了横向滚动条");
    }

    private static void CheckFooter(SettingsForm form)
    {
        var save = (Button)form.AcceptButton!;
        foreach (var button in save.Parent!.Controls.OfType<Button>())
        {
            var rect = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            if (!form.ClientRectangle.Contains(rect)) throw new InvalidOperationException("底部保存/取消按钮超出窗口");
        }
    }

    private static void Save(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
}

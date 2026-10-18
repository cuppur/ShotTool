// 叠加绘制挂点注册表：实现 Core.IAnnotationOverlayPainterRegistry。
// overlay 在固定绘制顺序的第 ⑧ 步遍历 ResolveAll()（按 ZOrder 升序）调用 Paint。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;

namespace ShotTool.Editor.Painters
{
    /// <summary>第三方叠加绘制器注册表（OCR 候选框、翻译气泡等挂这里）。</summary>
    public sealed class OverlayPainterRegistry : IAnnotationOverlayPainterRegistry
    {
        private readonly object _gate = new object();
        private readonly List<IAnnotationOverlayPainter> _painters = new List<IAnnotationOverlayPainter>();

        public event EventHandler? Changed;

        public void Register(IAnnotationOverlayPainter painter)
        {
            if (painter == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_painters.Contains(painter))
                {
                    return;
                }

                _painters.Add(painter);
            }

            RaiseChanged();
        }

        public void Unregister(IAnnotationOverlayPainter painter)
        {
            if (painter == null)
            {
                return;
            }

            bool removed;
            lock (_gate)
            {
                removed = _painters.Remove(painter);
            }

            if (removed)
            {
                RaiseChanged();
            }
        }

        /// <summary>按 ZOrder 升序返回快照（绘制期间不受注册变化影响）。</summary>
        public IReadOnlyList<IAnnotationOverlayPainter> ResolveAll()
        {
            List<IAnnotationOverlayPainter> copy;
            lock (_gate)
            {
                copy = new List<IAnnotationOverlayPainter>(_painters);
            }

            copy.Sort(static (a, b) => a.ZOrder.CompareTo(b.ZOrder));
            return copy;
        }

        private void RaiseChanged()
        {
            EventHandler? handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}

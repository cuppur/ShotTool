// 撤销/重做栈：实现 ShotTool.Core.IUndoStack（几何与 UI 均无关），覆盖层与工具条都可直接调用。
// 线程约定：非线程安全，必须由 UI 线程独占访问。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;

namespace ShotTool.Editor.Model
{
    /// <summary>
    /// 标注列表 + 撤销/重做栈。语义：加入即入栈（清空重做栈），撤销把栈顶移入重做栈，
    /// 重做再移回；序号标注始终保持连续（无空洞）。
    /// </summary>
    public sealed class AnnotationHistory : IUndoStack
    {
        private readonly List<Annotation> _items = new List<Annotation>();
        private readonly List<Annotation> _redo = new List<Annotation>();
        private int _nextNumber = 1;

        /// <summary>标注或撤销/重做状态变化时触发（UI 据此重绘并刷新按钮可用态）。</summary>
        public event EventHandler? Changed;

        /// <summary>按加入顺序（= 绘制顺序，后画的在上）暴露只读视图。</summary>
        public IReadOnlyList<Annotation> Items
        {
            get { return _items; }
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public bool IsEmpty
        {
            get { return _items.Count == 0; }
        }

        /// <summary>下一个序号标注的编号。</summary>
        public int NextNumber
        {
            get { return _nextNumber; }
        }

        // ---------------- IUndoStack ----------------

        public bool CanUndo
        {
            get { return _items.Count > 0; }
        }

        public bool CanRedo
        {
            get { return _redo.Count > 0; }
        }

        /// <summary>下一次撤销的显示名，例如「添加 矩形」。</summary>
        public string? NextUndoName
        {
            get
            {
                if (_items.Count == 0)
                {
                    return null;
                }

                return "添加 " + Describe(_items[_items.Count - 1]);
            }
        }

        /// <summary>下一次重做的显示名，例如「添加 矩形」。</summary>
        public string? NextRedoName
        {
            get
            {
                if (_redo.Count == 0)
                {
                    return null;
                }

                return "添加 " + Describe(_redo[_redo.Count - 1]);
            }
        }

        /// <summary>撤销栈顶一个标注；空栈返回 false。</summary>
        public bool Undo()
        {
            if (_items.Count == 0)
            {
                return false;
            }

            Annotation removed = _items[_items.Count - 1];
            _items.RemoveAt(_items.Count - 1);
            _redo.Add(removed);
            RecomputeNextNumber();
            OnChanged();
            return true;
        }

        /// <summary>重做最近一次被撤销的标注。</summary>
        public bool Redo()
        {
            if (_redo.Count == 0)
            {
                return false;
            }

            Annotation restored = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _items.Add(restored);
            RecomputeNextNumber();
            OnChanged();
            return true;
        }

        /// <summary>清空全部标注与栈。</summary>
        public void Clear()
        {
            bool changed = _items.Count > 0 || _redo.Count > 0 || _nextNumber != 1;
            _items.Clear();
            _redo.Clear();
            _nextNumber = 1;
            if (changed)
            {
                OnChanged();
            }
        }

        // ---------------- Editor 内部操作 ----------------

        /// <summary>加入一个标注并返回它（同时清空重做栈 —— 新操作会截断重做分支）。</summary>
        public T Add<T>(T annotation) where T : Annotation
        {
            if (annotation == null)
            {
                throw new ArgumentNullException(nameof(annotation));
            }

            _items.Add(annotation);
            _redo.Clear();
            NumberAnnotation? number = annotation as NumberAnnotation;
            if (number != null && number.Index >= _nextNumber)
            {
                _nextNumber = number.Index + 1;
            }

            OnChanged();
            return annotation;
        }

        /// <summary>加入序号标注（编号自动分配，从 1 开始）。</summary>
        public NumberAnnotation AddNumber(PhysPointF center, double radiusPhys, AnnotationStyle? style)
        {
            AnnotationStyle effective = style ?? AnnotationStyle.Default;
            NumberAnnotation annotation = new NumberAnnotation(center, _nextNumber, radiusPhys, effective);
            _items.Add(annotation);
            _redo.Clear();
            _nextNumber = annotation.Index + 1;
            OnChanged();
            return annotation;
        }

        /// <summary>移除指定标注（如命中测试后删除）；不存在返回 false。</summary>
        public bool Remove(Annotation annotation)
        {
            if (annotation == null)
            {
                return false;
            }

            bool removed = _items.Remove(annotation);
            if (removed)
            {
                _redo.Clear();
                RecomputeNextNumber();
                OnChanged();
            }

            return removed;
        }

        /// <summary>导出用快照（复制一份列表，避免导出期间被 UI 修改）。</summary>
        public List<Annotation> Snapshot()
        {
            return new List<Annotation>(_items);
        }

        private void RecomputeNextNumber()
        {
            int next = 1;
            for (int i = 0; i < _items.Count; i++)
            {
                NumberAnnotation? number = _items[i] as NumberAnnotation;
                if (number != null && number.Index >= next)
                {
                    next = number.Index + 1;
                }
            }

            _nextNumber = next;
        }

        private static string Describe(Annotation annotation)
        {
            switch (annotation.Kind)
            {
                case AnnotationKind.Rectangle:
                    return "矩形";
                case AnnotationKind.Ellipse:
                    return "椭圆";
                case AnnotationKind.Arrow:
                    return "箭头";
                case AnnotationKind.Pen:
                    return "画笔";
                case AnnotationKind.Mosaic:
                    return "马赛克";
                case AnnotationKind.Text:
                    return "文字";
                case AnnotationKind.Number:
                    return "序号";
                default:
                    return "标注";
            }
        }

        private void OnChanged()
        {
            EventHandler? handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}

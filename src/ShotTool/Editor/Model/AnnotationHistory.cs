using ShotTool.Core;

namespace ShotTool.Editor.Model;

/// <summary>Ordered annotations with atomic add, replace and remove transactions.</summary>
public sealed class AnnotationHistory : IUndoStack
{
    private readonly List<Annotation> _items = new();
    private sealed record Edit(List<Annotation> Before, List<Annotation> After, string Name);
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    public event EventHandler? Changed;
    public IReadOnlyList<Annotation> Items => _items;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;
    public int NextNumber => _items.OfType<NumberAnnotation>().Select(n => n.Index + 1).DefaultIfEmpty(1).Max();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoName => CanUndo ? _undo.Peek().Name : null;
    public string? NextRedoName => CanRedo ? _redo.Peek().Name : null;

    public bool Undo()
    {
        if (!_undo.TryPop(out var edit)) return false;
        Restore(edit.Before); _redo.Push(edit); OnChanged(); return true;
    }
    public bool Redo()
    {
        if (!_redo.TryPop(out var edit)) return false;
        Restore(edit.After); _undo.Push(edit); OnChanged(); return true;
    }
    public void Clear()
    {
        bool changed = _items.Count > 0 || CanUndo || CanRedo;
        _items.Clear(); _undo.Clear(); _redo.Clear();
        if (changed) OnChanged();
    }
    public T Add<T>(T annotation) where T : Annotation
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Commit(() => _items.Add(annotation), "添加 " + annotation.KindName);
        return annotation;
    }
    public NumberAnnotation AddNumber(PhysPointF center, double radiusPhys, AnnotationStyle? style)
        => Add(new NumberAnnotation(center, NextNumber, radiusPhys, style ?? AnnotationStyle.Default));
    public bool Remove(Annotation annotation)
    {
        if (!_items.Contains(annotation)) return false;
        Commit(() => _items.Remove(annotation), "删除标注"); return true;
    }
    public bool Replace(Annotation original, Annotation replacement, string name = "修改文字")
    {
        int index = _items.IndexOf(original);
        if (index < 0 || ReferenceEquals(original, replacement)) return false;
        Commit(() => _items[index] = replacement, name); return true;
    }
    public List<Annotation> Snapshot() => new(_items);
    private void Commit(Action change, string name)
    {
        var before = Snapshot(); change();
        _undo.Push(new Edit(before, Snapshot(), name)); _redo.Clear(); OnChanged();
    }
    private void Restore(List<Annotation> items) { _items.Clear(); _items.AddRange(items); }
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

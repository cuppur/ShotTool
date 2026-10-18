// 工具条按钮注册表：实现 Core.IToolbarRegistry。
// dev-ai 在 App 启动时 Register 自己的 IToolbarItemProvider；工具条每次重建时调用 Resolve 取按钮列表。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>扩展按钮注册表（Editor 拥有，dev-ai 只注册 provider）。</summary>
    public sealed class ToolbarRegistry : IToolbarRegistry
    {
        private readonly object _gate = new object();
        private readonly List<IToolbarItemProvider> _providers = new List<IToolbarItemProvider>();

        public event EventHandler? ItemsChanged;

        public void Register(IToolbarItemProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_providers.Contains(provider))
                {
                    return;
                }

                _providers.Add(provider);
            }

            provider.ItemsChanged += OnProviderItemsChanged;
            RaiseChanged();
        }

        public void Unregister(IToolbarItemProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            bool removed;
            lock (_gate)
            {
                removed = _providers.Remove(provider);
            }

            if (removed)
            {
                provider.ItemsChanged -= OnProviderItemsChanged;
                RaiseChanged();
            }
        }

        /// <summary>
        /// 解析当前应显示的扩展按钮：过滤 IsVisible、按 (Slot, Order) 排序。
        /// 单个 provider 抛异常只跳过它，不影响其余 provider（工具条不得因扩展崩溃）。
        /// </summary>
        public IReadOnlyList<IToolbarItem> Resolve(ToolbarContext context)
        {
            List<IToolbarItem> result = new List<IToolbarItem>();
            List<IToolbarItemProvider> providers;
            lock (_gate)
            {
                providers = new List<IToolbarItemProvider>(_providers);
            }

            for (int i = 0; i < providers.Count; i++)
            {
                IEnumerable<IToolbarItem>? items;
                try
                {
                    items = providers[i].GetItems();
                }
                catch (Exception)
                {
                    continue;
                }

                if (items == null)
                {
                    continue;
                }

                foreach (IToolbarItem item in items)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (context != null && !item.IsVisible(context))
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    result.Add(item);
                }
            }

            result.Sort(static (a, b) =>
            {
                int bySlot = ((int)a.Slot).CompareTo((int)b.Slot);
                return bySlot != 0 ? bySlot : a.Order.CompareTo(b.Order);
            });
            return result;
        }

        private void OnProviderItemsChanged(object? sender, EventArgs e)
        {
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            EventHandler? handler = ItemsChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Leap.Unity
{
    internal static class DependencyGuiUtilities
    {
        public static IList<TNode> FlattenNodes<TNode>(this DependencyNodeBase node)
            where TNode : DependencyNodeBase
        {
            List<TNode> list = new List<TNode>();

            void AddToList(DependencyNodeBase node)
            {
                if (node is TNode tNode) list.Add(tNode);
                foreach (var child in node.Children)
                {
                    AddToList(child);
                }
            }
            
            AddToList(node);
            return list;
        }

        private static (bool isDependency, bool isDependant)? GetStatus(DependencyNodeBase node, DependencyNodeBase selected) =>
            selected != null
                ? (selected.DependsOnCached(node), node.DependsOnCached(selected))
                : ((bool, bool)?)null;

        public static Color GetColor(DependencyNodeBase node, DependencyNodeBase selected)
        {
            static Color ModColor(Color color)
            {
                const float tintLow = 0.6f;
                return new Color(
                    Mathf.Lerp(tintLow, 1f, color.r),
                    Mathf.Lerp(tintLow, 1f, color.g),
                    Mathf.Lerp(tintLow, 1f, color.b),
                    1f
                );
            }

            Color color;
            if (selected == null) color = Color.white; // Default color
            if (node == selected) color = Color.blue; // Selected
            color = GetStatus(node, selected) switch
            {
                (true, true) => Color.yellow, // Circular dependency
                (true, false) => Color.red, // Depends on selected
                (false, true) => Color.green,
                _ => Color.white // This also handles when GetStatus returns null (nothing selected)
            };
            return ModColor(color);
        }

        public delegate bool DependencyNodeFilter((DependencyNodeBase node, DependencyNodeBase selected) pair);

        public static DependencyNodeFilter GetFilterPredicate(bool filterToDependencies, bool filterToDependants) =>
            ((DependencyNodeBase node, DependencyNodeBase selected) t) =>
            {
                // Note the filter function should return true for items that will not be displayed
                var nodeStatus = GetStatus(t.node, t.selected);
                var cyclicOnly = filterToDependencies && filterToDependants;
                if (nodeStatus == null) return false;
                if (cyclicOnly) return !(nodeStatus is (true, true));
                if (filterToDependencies) return !(nodeStatus is (true, _));
                if (filterToDependants) return !(nodeStatus is (_, true));
                return false; // display everything
            };
        
        public static string BytesToString(long byteCount)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf[0];
            long bytes = Math.Abs(byteCount);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return (Math.Sign(byteCount) * num) + suf[place];
        }
    }
}
using System.Collections.Generic;
using UnityEditor.PackageManager;

namespace Leap.Unity.Dependency
{
    /// <summary>
    /// Base class for nodes.
    /// Note that all nodes types will do caching based on the assumption that relations and fields are not mutated after
    /// creation.
    /// </summary>
    internal abstract class DependencyNodeBase
    {
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                _path = null; // Uncache path
            }
        }

        public string PackageName;
        public PackageSource PackageSource;

        public DependencyNodeBase Parent
        {
            get => _parent;
            set
            {
                _parent = value;
                _path = null; // Uncache path
            }
        }

        public abstract List<DependencyNodeBase> Children { get; }
        public abstract long GetSize();

        private readonly Dictionary<DependencyNodeBase, bool> _dependsCache =
            new Dictionary<DependencyNodeBase, bool>();

        public bool DependsOnCached(DependencyNodeBase other)
        {
            if (other == null || other == this) return false;
            if (!_dependsCache.TryGetValue(other, out bool ret)) {
                _dependsCache.Add(other, false);
                ret = DependsOn(other);
                _dependsCache[other] = ret;
            }
            return ret;
        }

        public abstract bool DependsOn(DependencyNodeBase other);

        public bool IsMyParent(DependencyNodeBase other) => Parent != null && (Parent == other || Parent.IsMyParent(other));

        public override string ToString() => Name;

        private string _path;
        private DependencyNodeBase _parent;
        private string _name;

        public string GetPath()
        {
            if (_path == null)
            {
                var parentPath = Parent?.GetPath();
                _path = string.IsNullOrEmpty(parentPath) ? Name : $"{parentPath}/{Name}";
            }

            return _path;
        }
    }
}
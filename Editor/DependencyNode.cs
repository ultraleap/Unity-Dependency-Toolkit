using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Leap.Unity.Dependency
{
    internal class DependencyNode : DependencyNodeBase
    {
        public enum NodeKind
        {
            Default,
            Builtin,
            Missing,
            Unknown,
        }
        
        public string Guid;
        public long Size;
        public NodeKind Kind;

        public List<DependencyNode> Dependencies { get; } = new List<DependencyNode>();
        public List<DependencyNode> Dependants { get; } = new List<DependencyNode>();

        public override List<DependencyNodeBase> Children => Array.Empty<DependencyNodeBase>().ToList();
        public override long GetSize() => Size;

        public override bool DependsOn(DependencyNodeBase other)
        {
            foreach (var d in Dependencies) {
                if (d == this) {
                    Debug.Log($"Why does {Name} depend on itself?");
                    return false;
                }
                if (other == d || d.IsMyParent(other)) {
                    return true;
                }
            }

            return Dependencies.Any(d => d.DependsOnCached(other));
        }
    }
}

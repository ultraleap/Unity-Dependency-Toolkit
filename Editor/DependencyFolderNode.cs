using System.Collections.Generic;
using System.Linq;

namespace Leap.Unity
{
    internal class DependencyFolderNode : DependencyNodeBase
    {
        public override List<DependencyNodeBase> Children { get; } = new List<DependencyNodeBase>();
        public override long GetSize() => Children.Sum(c => c.GetSize());
        public override bool DependsOn(DependencyNodeBase other) => Children.Any(c => c.DependsOnCached(other));
    }
}
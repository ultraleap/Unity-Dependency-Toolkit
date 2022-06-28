namespace Leap.Unity
{
    using UnityEngine;
    using UnityEditor;
    using System;
    using System.Linq;
    using System.Collections.Generic;

    [CustomEditor(typeof(DependencyTree))]
    internal class DependencyTreeEditor : Editor
    {
        public enum SortMode
        {
            Directory,
            Name,
            Size
        }

        public SortMode Sort = SortMode.Directory;
        public string Selected;

        private SerializedProperty _knownBuiltinAssetFilePaths;

        private DependencyFolderNode _specialResourcesNode;
        private DependencyFolderNode _missingReferencesNode;
        private DependencyFolderNode _root; // Root is the project root folder, it has other project folders as children

        private const string SpecialResourcesNodeName = "[All Resources]";
        private const string MissingReferencesNodeName = "[Missing references]";

        public delegate bool DrawNodeViewFunc(IList<DependencyNodeBase> nodes, Predicate<DependencyNodeBase> filter);

        private void OnEnable()
        {
            _knownBuiltinAssetFilePaths = serializedObject.FindProperty(nameof(DependencyTree.KnownBuiltinAssetFilePaths));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(_knownBuiltinAssetFilePaths);
            serializedObject.ApplyModifiedProperties();

            var dependencyTree = ((DependencyTree)target);
            var rootNode = dependencyTree.GetRootNode();
            if (_specialResourcesNode == null || _missingReferencesNode == null || _root != rootNode && rootNode != null)
            {
                _root = rootNode;
                _specialResourcesNode = new DependencyFolderNode
                    {
                        Parent = _root,
                        Name = SpecialResourcesNodeName
                    };

                _specialResourcesNode.Children.AddRange(
                    rootNode.FlattenNodes<DependencyNodeBase>()
                    .Where(f => f.Name == "Resources")
                    .Select(n => {
                        var newFolderNode = new DependencyFolderNode{
                            Name = n.GetPath(),
                            Parent = _specialResourcesNode // New parent will be included in the path after assignment
                        };
                        newFolderNode.Children.AddRange(n.Children);
                        foreach (var child in n.Children) {
                            child.Parent = newFolderNode;
                        }
                        return newFolderNode;
                        })
                );
                
                _missingReferencesNode = new DependencyFolderNode { Parent = _root, Name = MissingReferencesNodeName };
                foreach (var missingGuid in dependencyTree.AnalysisResults.missingGuidReferences)
                {
                    var missingRefNode = dependencyTree.NodesByGuid[missingGuid];
                    missingRefNode.Parent = _missingReferencesNode; // New parent will be included in the path after assignment
                    _missingReferencesNode.Children.Add(missingRefNode);
                }
            }
            
            DrawRefreshButton();

            if (GUILayout.Button("Open Dependency Browser", GUILayout.MaxWidth(300f), GUILayout.Height(32f)))
            {
                DependencyBrowser.Open(this);
            }
            EditorUtility.SetDirty(this);
        }

        private DependencyNodeBase GetNodeFromPath(string nodePath)
        {
            if (string.IsNullOrEmpty(nodePath)) return null;

            static DependencyNodeBase GetNamedChild(DependencyNodeBase node, string name) => node.Children.FirstOrDefault(o => o.Name == name);

            static DependencyNodeBase LookupNodeFromPath(DependencyNodeBase root, string path)
            {
                DependencyNodeBase current = root;
                foreach (var pathSegment in path.Split(new []{'/'}, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = GetNamedChild(current, pathSegment);
                    if (current == null) break; // Didn't find a named child, result will be null
                }

                return current;
            }

            if (nodePath.StartsWith(SpecialResourcesNodeName))
            {
                var pathSplitAtResources = nodePath.Substring(SpecialResourcesNodeName.Length+1).Split(new [] { "Resources" }, 2, StringSplitOptions.RemoveEmptyEntries);
                var root = _specialResourcesNode.Children.FirstOrDefault(c => c.Name == $"{pathSplitAtResources[0]}Resources");
                return LookupNodeFromPath(root, pathSplitAtResources[1]);
            } else if (nodePath.StartsWith(MissingReferencesNodeName)) {
                return LookupNodeFromPath(_missingReferencesNode, nodePath.Substring(MissingReferencesNodeName.Length));
            } else {
                return LookupNodeFromPath(((DependencyTree)target).GetRootNode(), nodePath);
            }
        }

        public void DrawRefreshButton()
        {
            if (GUILayout.Button("Refresh (2-3 minutes)", GUILayout.MaxWidth(300f), GUILayout.Height(32f)))
            {
                ((DependencyTree)target).Refresh();
            }
        }

        public void DrawSortPopup() => Sort = (SortMode)EditorGUILayout.EnumPopup("Sort by", Sort);

        public Func<bool> CreateNodeViewDrawer(DrawNodeViewFunc drawNodeViewFunc, bool filterToDependencies, bool filterToDependants, DependencyGuiUtilities.DependencyNodeFilter extraFilter = null, Action guiFuncWhenNoSelection = null) =>
            () =>
            {
                var nodeRelationFilter = DependencyGuiUtilities.GetFilterPredicate(filterToDependencies, filterToDependants);

                // Combine the relation filter with the extra filter
                bool Filter((DependencyNodeBase node, DependencyNodeBase selected) pair) =>
                    nodeRelationFilter(pair)
                    || (extraFilter?.Invoke(pair) ??
                        false); // Use extra filter if non-null else do no filtering with constant false

                var selectedNode = GetNodeFromPath(Selected);
                if (selectedNode == null && guiFuncWhenNoSelection != null)
                {
                    guiFuncWhenNoSelection.Invoke();
                    return false;
                }

                bool ShouldFilter(DependencyNodeBase node) => Filter((node, selectedNode));

                return drawNodeViewFunc(new[] { _specialResourcesNode, _missingReferencesNode }.Concat(_root?.Children ?? Enumerable.Empty<DependencyNodeBase>()).ToArray(),
                    ShouldFilter);
            };

        private static bool DrawToggle(string nodePath, ref string selectedNodePath, DependencyNodeBase node,
            ref DependencyNodeBase selectedNode)
        {
            if (!EditorGUILayout.Toggle(selectedNodePath == nodePath, EditorStyles.radioButton,
                    GUILayout.Width(20f)) || selectedNodePath == nodePath)
            {
                return false;
            }

            selectedNodePath = nodePath;
            selectedNode = node;
            return true;
        }

        public DrawNodeViewFunc CreateListNodeViewDrawer()
        {
            DependencyNodeBase previousSelectedNode = null;
            IEnumerable<DependencyNode> nodesCached = null;
            SortMode previousSortMode = Sort;

            return DrawNodeViewFunc;

            bool DrawNodeViewFunc(IList<DependencyNodeBase> nodes, Predicate<DependencyNodeBase> filter)
            {
                var selectedNode = GetNodeFromPath(Selected);
                var selectionChanged = previousSelectedNode != selectedNode;
                
                void RefreshCache()
                {
                    previousSortMode = Sort;
                    previousSelectedNode = selectedNode;

                    var flattened = nodes.SelectMany(n => n.FlattenNodes<DependencyNodeBase>());
                    nodesCached = (Sort switch
                    {
                        SortMode.Name => flattened.OrderBy(c => c.Name),
                        SortMode.Size => flattened.OrderByDescending(c => c.GetSize()),
                        SortMode.Directory => flattened,
                        _ => Enumerable.Empty<DependencyNodeBase>()
                    }).OfType<DependencyNode>().ToArray(); // only display leaf nodes, force enumeration now with ToArray to avoid repeated enumeration later
                }
                
                if (nodesCached == null || selectionChanged || previousSortMode != Sort)
                {
                    RefreshCache();
                }
                
                foreach (var node in nodesCached)
                {
                    if (filter(node)) continue;
                    EditorGUILayout.BeginHorizontal();
                    
                    var nodePath = node.GetPath();
                    
                    if (DrawToggle(nodePath, ref Selected, node, ref selectedNode))
                    {
                        selectionChanged = true;
                        RefreshCache();
                    }

                    if (GUILayout.Button("Go To", GUILayout.Width(50f)))
                    {
                        var objectToSelect = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(node.Guid));
                        Selection.activeObject = objectToSelect;
                        EditorGUIUtility.PingObject(objectToSelect);
                    }
            
                    var nodeText = $"{node.GetPath()} ({DependencyGuiUtilities.BytesToString(node.GetSize())}) {($"[{node.Dependants.Count} refs, {node.Dependencies.Count} deps]")}";

                    GUI.color = DependencyGuiUtilities.GetColor(node, selectedNode);
                    EditorGUILayout.LabelField(nodeText);
                    GUI.color = Color.white;

                    EditorGUILayout.EndHorizontal();
                }

                return selectionChanged;
            }
        }

        public DrawNodeViewFunc CreateSelectionTreeNodeViewDrawer(List<string> expandedNodes)
        {
            int indent = 0;
            DependencyNodeBase selectedNode;
            
            return DrawNodeViewFunc;

            bool DrawNodeViewFunc(IList<DependencyNodeBase> nodes, Predicate<DependencyNodeBase> filter)
            {
                selectedNode = GetNodeFromPath(Selected);
                bool selectionChanged = false;
                foreach (var node in nodes)
                {
                    selectionChanged |= DrawNode(node, filter);
                }

                return selectionChanged;
            }
            
            bool DrawNode(DependencyNodeBase node, Predicate<DependencyNodeBase> filter)
            {
                var nodePath = node.GetPath();
                bool selectionChanged = false;
                
                EditorGUILayout.BeginHorizontal();
                
                if (DrawToggle(nodePath, ref Selected, node, ref selectedNode))
                {
                    selectionChanged = true;
                }

                GUILayout.Space(4 + (12 * indent));
                var nodeText = $"{node.Name} ({DependencyGuiUtilities.BytesToString(node.GetSize())}){(node is DependencyNode n ? $" [{n.Dependants.Count} refs, {n.Dependencies.Count} deps]" : string.Empty)}";

                IEnumerable<DependencyNodeBase> children = Enumerable.Empty<DependencyNodeBase>();
                if (node is DependencyFolderNode folderNode)
                {
                    var wasFoldedOut = expandedNodes.Contains(nodePath);
                    GUI.color = DependencyGuiUtilities.GetColor(folderNode, selectedNode);
                    var foldedOut = EditorGUILayout.Foldout(wasFoldedOut, nodeText);
                    GUI.color = Color.white;
                    if (foldedOut && !wasFoldedOut)
                    {
                        expandedNodes.Add(nodePath);
                    }
                    else if (!foldedOut && wasFoldedOut)
                    {
                        expandedNodes.Remove(nodePath);
                    }

                    if (foldedOut)
                    {
                        children = folderNode.Children;
                    }
                }
                else
                {
                    GUI.color = DependencyGuiUtilities.GetColor(node, selectedNode);
                    EditorGUILayout.LabelField(nodeText);
                    GUI.color = Color.white;
                }

                EditorGUILayout.EndHorizontal();
                
                foreach (var child in (Sort switch
                         {
                             SortMode.Name => children.OrderBy(c => c.Name),
                             SortMode.Size => children.OrderByDescending(c => c.GetSize()),
                             SortMode.Directory => children,
                             _ => Enumerable.Empty<DependencyNodeBase>()
                         }))
                {
                    if (filter(child)) continue;
                    indent++;
                    selectionChanged |= DrawNode(child, filter);
                    indent--;
                }

                return selectionChanged;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Leap.Unity.Dependency
{
    internal class DependencyBrowser : EditorWindow
    {
        private static DependencyBrowser window;
        private DependencyTreeEditor editor;

        // Resizing layout variables
        private float resizerSpacing;
        private bool resizingHorizontally;
        private float currentHorizontalDistance;
        private Rect horizontalResizeInteractionRect;
        private bool resizingVertically;
        private float currentVerticalDistance;
        private Rect verticalResizeInteractionRect;
        private Texture2D resizerTexture;

        // Browser view state
        private Vector2 windowSize;
        private Vector2 selectionScrollViewPosition = Vector2.zero;
        private List<string> selectionNodesExpanded = new List<string>();
        private Vector2 dependenciesScrollViewPosition = Vector2.zero;
        private Vector2 dependantsScrollViewPosition = Vector2.zero;
        private bool isViewingCyclicDependencies;
        private Dictionary<DependencyNodeBase, bool> nodesUnusedOrHasUnusedChildren = new Dictionary<DependencyNodeBase, bool>();
        private Dictionary<DependencyNodeBase, int> nodeMissingRefCount = new Dictionary<DependencyNodeBase, int>();
        private Dictionary<DependencyNodeBase, bool> nodesMissingOrWithMissingChildren = new Dictionary<DependencyNodeBase, bool>();
        
        private Func<bool> _selectionTreeViewDrawer;
        private Func<bool> _missingRefTreeViewDrawer;
        
        private Func<bool> _dependencyViewDrawer;
        private Func<bool> _dependantViewDrawer;
        private Func<bool> _cyclicDependencyViewDrawer;
        
        // Selection Options
        private string selectionFilterString;
        private bool filterToOnlyUnused;
        private bool filterToAssetsMissingRefs;
        private bool showExternalPackages;
        
        private int repositoryLookupCommitLimit;
        private string repositoryLookupCommitLimitKey = "UltraleapDependencyBrowserGitRepoLookupCommitLimit";
        private string repositoryLookupRoot;
        private readonly string repositoryLookupRootKey = "UltraleapDependencyBrowserGitRepoLookupRoot";

        private event Action resizeOccurred;

        public static void Open(DependencyTreeEditor dependencyTreeEditor)
        {
            if (window == null)
            {
                window = (DependencyBrowser)GetWindow(typeof(DependencyBrowser),
                    true, "Dependency Browser", true);
            }
            window.editor = dependencyTreeEditor;
            window.Show();
            
            
            ((DependencyTree)window.editor.target).OnRefresh += ClearCaches;
            
            void ClearCaches()
            {
                window.nodesUnusedOrHasUnusedChildren.Clear();
                window.nodeMissingRefCount.Clear();
                window.nodesMissingOrWithMissingChildren.Clear();
            }
        }

        private void OnEnable()
        {
            this.position = new Rect(200, 200, 800, 600);
            resizerSpacing = 4f;
            windowSize = new Vector2(800, 600);
            resizerTexture = new Texture2D(1, 1);
            resizerTexture.SetPixel(0, 0, Color.black);
            resizerTexture.Apply();
            currentHorizontalDistance = windowSize.x / 2f;
            currentVerticalDistance = windowSize.y / 2f;
            horizontalResizeInteractionRect = new Rect(currentHorizontalDistance, 0f, 8f, windowSize.y);
            verticalResizeInteractionRect = new Rect(currentHorizontalDistance + resizerSpacing, currentVerticalDistance, windowSize.x - currentHorizontalDistance - resizerSpacing, 8f);
            repositoryLookupRoot = EditorPrefs.GetString(repositoryLookupRootKey, Environment.CurrentDirectory);
            repositoryLookupCommitLimit = EditorPrefs.GetInt(repositoryLookupCommitLimitKey, 500);

            void UpdateResizeInteractionRects()
            {
                horizontalResizeInteractionRect.Set(currentHorizontalDistance, 0f, 8f, windowSize.y);
                verticalResizeInteractionRect.Set(currentHorizontalDistance + resizerSpacing, currentVerticalDistance, windowSize.x - currentHorizontalDistance - resizerSpacing, 8f);
            }

            resizeOccurred += UpdateResizeInteractionRects;
        }

        void OnGUI()
        {
            CheckForWindowResize();
            GUILayout.BeginHorizontal();
            DrawLeftColumn();
            DrawResizer(ref resizingHorizontally, ref currentHorizontalDistance, ref horizontalResizeInteractionRect, resizeOccurred, resizerSpacing,false, resizerTexture);
            DrawRightColumn();
            GUILayout.EndHorizontal();
            Repaint();
        }

        void DrawLeftColumn()
        {
            selectionScrollViewPosition = GUILayout.BeginScrollView(selectionScrollViewPosition, GUILayout.Height(position.height), GUILayout.Width(currentHorizontalDistance));
            
            selectionFilterString = GUILayout.TextField(selectionFilterString);
            filterToOnlyUnused = GUILayout.Toggle(filterToOnlyUnused, "Filter to only unused");
            filterToAssetsMissingRefs = GUILayout.Toggle(filterToAssetsMissingRefs, "Filter to assets with missing references");
            showExternalPackages = GUILayout.Toggle(showExternalPackages, "Show external packages");
            editor.DrawSortPopup();
            
            repositoryLookupRoot = GUILayout.TextField(repositoryLookupRoot);
            EditorPrefs.SetString(repositoryLookupRootKey, repositoryLookupRoot);
            GUILayout.BeginHorizontal();
            repositoryLookupCommitLimit = EditorGUILayout.IntField(repositoryLookupCommitLimit);
            EditorPrefs.SetInt(repositoryLookupCommitLimitKey, repositoryLookupCommitLimit);
            editor.DrawGitLookupMissingRefButton(repositoryLookupRoot, repositoryLookupCommitLimit);
            GUILayout.EndHorizontal();
            
            GUILayout.Label($"Current selection: {editor.Selected}");
            GUILayout.Label("Select an asset or folder");
            _selectionTreeViewDrawer ??= editor.CreateNodeViewDrawer(
                editor.CreateSelectionTreeNodeViewDrawer(selectionNodesExpanded), false, false,
                SelectionTreeFilter);
            _selectionTreeViewDrawer();

            GUILayout.EndScrollView();
        }

        private bool SelectionTreeFilter((DependencyNodeBase node, DependencyNodeBase selected) pair)
        {
            if (filterToOnlyUnused)
            {
                bool HasUnusedChildrenOrIsUnusedCached(DependencyNodeBase node)
                {
                    if (!nodesUnusedOrHasUnusedChildren.TryGetValue(node, out var unused))
                    {
                        unused = node switch
                        {
                            DependencyNode n => n.Dependants.Count < 1,
                            DependencyFolderNode folderNode => folderNode.Children.Any(
                                HasUnusedChildrenOrIsUnusedCached),
                            _ => throw new NotImplementedException($"Missing case for '{node.GetType()}'")
                        };

                        nodesUnusedOrHasUnusedChildren[node] = unused;
                    }

                    return unused;
                }

                if (!HasUnusedChildrenOrIsUnusedCached(pair.node))
                {
                    return true;
                }
            }

            if (filterToAssetsMissingRefs)
            {
                var analysis = ((DependencyTree)editor.target).AnalysisResults;
                
                int MissingRefCountCached(DependencyNodeBase node)
                {
                    if (!nodeMissingRefCount.TryGetValue(node, out var missingRefCount))
                    {
                        missingRefCount = node switch
                        {
                            DependencyNode n => analysis.missingGuidReferences.Sum(guid => n.Dependencies.Count(dep => dep.Guid == guid)),
                            DependencyFolderNode folderNode => folderNode.Children.Sum(MissingRefCountCached),
                            _ => throw new NotImplementedException($"Missing case for '{node.GetType()}'")
                        };

                        nodeMissingRefCount[node] = missingRefCount;
                    }

                    return missingRefCount;
                }

                bool IsMissingOrHasMissingChildrenCached(DependencyNodeBase node)
                {
                    if (!nodesMissingOrWithMissingChildren.TryGetValue(node, out var missing))
                    {
                        missing = node switch
                        {
                            DependencyNode n => analysis.missingGuidReferences.Contains(n.Guid),
                            DependencyFolderNode folderNode => folderNode.Children.Any(
                                IsMissingOrHasMissingChildrenCached),
                            _ => throw new NotImplementedException($"Missing case for '{node.GetType()}'")
                        };
                    }

                    nodesMissingOrWithMissingChildren[node] = missing;
                    return missing;
                }
                
                // Filters everything that is not itself a missing asset or has missing asset references
                if (MissingRefCountCached(pair.node) < 1 && !IsMissingOrHasMissingChildrenCached(pair.node))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(selectionFilterString) && !pair.node.GetPath().Contains(selectionFilterString))
            {
                return true;
            }

            if (!showExternalPackages && pair.node.PackageSource switch
                {
                    // Only mutable package sources are considered non-external for filtering
                    PackageSource.Embedded => false,
                    PackageSource.Local => false,
                    PackageSource.Unknown => false, // Don't filter when we don't know
                    _ => true
                })
            {
                return true;
            }

            return false;
        }

        void DrawDependencyAndDependantViews()
        {
            GUILayout.Label("Dependencies view - assets the selection references");
            dependenciesScrollViewPosition = GUILayout.BeginScrollView(dependenciesScrollViewPosition, GUILayout.Height(currentVerticalDistance - EditorGUIUtility.singleLineHeight), GUILayout.Width(windowSize.x - currentHorizontalDistance - resizerSpacing));
            _dependencyViewDrawer ??= editor.CreateNodeViewDrawer(editor.CreateListNodeViewDrawer(), true, false,
                guiFuncWhenNoSelection: () => GUILayout.Label("Select an asset to view dependencies."));
            _dependencyViewDrawer();
            GUILayout.EndScrollView();

            DrawResizer(ref resizingVertically, ref currentVerticalDistance, ref verticalResizeInteractionRect, resizeOccurred, resizerSpacing, true, resizerTexture);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Dependants view - assets that reference the selection");
            GUILayout.FlexibleSpace();
            isViewingCyclicDependencies = GUILayout.Toggle(isViewingCyclicDependencies, new GUIContent("Filter to Cyclic Dependencies", "Show dependants that are also dependencies, these assets are tightly coupled to the selection."));
            GUILayout.EndHorizontal();

            dependantsScrollViewPosition = GUILayout.BeginScrollView(dependantsScrollViewPosition, GUILayout.Height(this.position.height - currentVerticalDistance - resizerSpacing), GUILayout.Width(windowSize.x - currentHorizontalDistance - resizerSpacing));

            if (isViewingCyclicDependencies)
            {
                _cyclicDependencyViewDrawer ??= editor.CreateNodeViewDrawer(editor.CreateListNodeViewDrawer(), true, true,
                    guiFuncWhenNoSelection: () => GUILayout.Label("Select an asset to view cyclic dependencies."));
                _cyclicDependencyViewDrawer();
            }
            else
            {
                _dependantViewDrawer ??= editor.CreateNodeViewDrawer(editor.CreateListNodeViewDrawer(), false, true,
                    guiFuncWhenNoSelection: () => GUILayout.Label("Select an asset to view dependants."));
                _dependantViewDrawer();
            }

            GUILayout.EndScrollView();
        }
        
        void DrawRightColumn()
        {
            GUILayout.BeginVertical();
            DrawDependencyAndDependantViews();
            GUILayout.EndVertical();
        }

        private void CheckForWindowResize()
        {
            var size = new Vector2(this.position.width, this.position.height);
            if (size == windowSize) return;
            windowSize = size;
            resizeOccurred?.Invoke();
        }

        private static void DrawResizer(ref bool resizing, ref float currentDistance, ref Rect cursorInteractionRect, Action resize, float resizerSpacing, bool isVerticalResizer, Texture2D resizerTexture)
        {
            var textureRect = new Rect(cursorInteractionRect);

            if (isVerticalResizer)
            {
                textureRect.height = cursorInteractionRect.height / 2f;
                textureRect.y += textureRect.height / 2f;
            }
            else
            {
                textureRect.width = cursorInteractionRect.width / 2f;
                textureRect.x += textureRect.width / 2f;
            }

            GUI.DrawTexture(textureRect, resizerTexture);
            EditorGUIUtility.AddCursorRect(cursorInteractionRect, isVerticalResizer ? MouseCursor.ResizeVertical : MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && cursorInteractionRect.Contains(Event.current.mousePosition))
                resizing = true;
            if (resizing)
            {
                if (isVerticalResizer)
                {
                    currentDistance = Event.current.mousePosition.y;
                    cursorInteractionRect.Set(cursorInteractionRect.x, currentDistance, cursorInteractionRect.width, cursorInteractionRect.height);
                }
                else
                {
                    currentDistance = Event.current.mousePosition.x;
                    cursorInteractionRect.Set(currentDistance, cursorInteractionRect.y, cursorInteractionRect.width, cursorInteractionRect.height);
                }
                resize?.Invoke();
            }
            if (Event.current.type == EventType.MouseUp)
                resizing = false;

            GUILayout.Space(resizerSpacing);
        }
    }
}
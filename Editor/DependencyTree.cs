using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace Leap.Unity
{
    using UnityEngine;
    using UnityEditor;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Collections.Generic;

    internal class DependencyTree : ScriptableObject
    {
        [Serializable]
        public class AssetReference
        {
            public string guid;
            public string path;
            public long size;
            public List<string> references = new List<string>();
            public List<string> referencedBy = new List<string>();
            public string packageName;
            public PackageSource packageSource;
            public string asmdefName;
        }

        [Serializable]
        public class StaticAnalysisResults
        {
            public List<string> assetsWithoutGuids = new List<string>();
            public List<string> duplicatedGuids = new List<string>();
            public List<string> missingGuidReferences = new List<string>();
        }

        /// <summary>
        /// This class is used to bundle up information required for tree generation
        /// </summary>
        private class GenerationContext
        {
            public PackageCollection PackageCollection;
            public List<string> KnownBuiltinAssetFilePaths;

            public GenerationContext(PackageCollection packageCollection, List<string> knownBuiltinAssetFilePaths)
            {
                PackageCollection = packageCollection;
                KnownBuiltinAssetFilePaths = knownBuiltinAssetFilePaths;
            }
        }
        
        public event Action OnRefresh;

        private static readonly HashSet<string> YamlFileExtensions
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".asset",
                ".unity",
                ".prefab",
                ".mat",
                ".controller",
                ".anim",
                ".asmdef"
            };

        // GUID format prefix label is slightly different in asmdef files
        private static readonly Regex GuidRegex = new Regex("(?:GUID:|guid: )([0-9a-f]{32})",
            RegexOptions.CultureInvariant);

        private static (bool Success, List<AssetReference> AssetReferences, StaticAnalysisResults AnalysisResults) GenerateTree(GenerationContext generationContext)
        {
            var intermediateGuidsList = new List<string>();
            var filesMissingMetas = new List<string>();
            var assetsByGuid = new Dictionary<string, AssetReference>();
            var analysis = new StaticAnalysisResults();
            var packages = generationContext.PackageCollection.ToArray();

            static void UpdateAssetReferenceFromFilePath(AssetReference assetReference, string filePath, GenerationContext generationContext)
            {
                var isBuiltinAsset = generationContext.KnownBuiltinAssetFilePaths.Any(path => path.Equals(filePath));

                assetReference.path = filePath;

                if (!isBuiltinAsset)
                {
                    try
                    {
                        assetReference.size = new FileInfo(filePath).Length;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Skipping file due to exception. See exception trace in following message.");
                        Debug.LogException(e);
                    }

                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(filePath);
                    if (package != null)
                    {
                        assetReference.packageName = package.name;
                        assetReference.packageSource = package.source;
                    }
                    assetReference.asmdefName = CompilationPipeline.GetAssemblyNameFromScriptPath(filePath);
                }
                else
                {
                    assetReference.packageName = "<builtin>";
                }
            }

            void ParseDirectory(string directoryPath)
            {
                // Adds all GUIDs referenced by the asset to the list.
                // The first added element is always its own GUID.
                // Returns whether or not the GUID of this asset was found and is first in the references list
                static bool ParseAssetFile(string path, List<string> references, ICollection<string> missingMetaFiles)
                {
                    // Add GUIDs in a file to the references list
                    static void ParseFileContent(string content, List<string> references) =>
                        references.AddRange(GuidRegex.Matches(content)
                            .Cast<Match>()
                            .Select(match => match.Groups[1].Value));

                    var metaFile = $"{path}.meta";
                    var guidFound = true;
                    if (File.Exists(metaFile)) {
                        ParseFileContent(File.ReadAllText(metaFile), references);
                    }
                    else
                    {
                        missingMetaFiles.Add(path);
                        guidFound = false;
                        // We don't return here on this error case, if it's a Yaml file there is still useful data within
                    }
                    if (YamlFileExtensions.Contains(Path.GetExtension(path) ?? string.Empty)) {
                        ParseFileContent(File.ReadAllText(path), references);
                    }

                    return guidFound;
                }

                var directoryAssets = AssetDatabase.FindAssets("", new []{directoryPath})
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !Directory.Exists(p)) // Exclude folders for now, Unity treats them as Assets but we don't care about them
                    .ToArray();

                foreach (var filePath in directoryAssets)
                {
                    if (filePath.Equals(DependencyTreePath)) continue; // Don't parse the dependency tree itself as it will be cyclic
                    var ext = Path.GetExtension(filePath);
                    if (ext.Equals(".meta", StringComparison.OrdinalIgnoreCase)) {
                        // Skip .meta files on their own; we'll find them later
                        continue;
                    }

                    intermediateGuidsList.Clear();

                    string thisAssetsGuid = ParseAssetFile(filePath, intermediateGuidsList, filesMissingMetas)
                        ? intermediateGuidsList[0]
                        : null;

                    // Add every new guid to the map
                    foreach (var guid in intermediateGuidsList) {
                        if (!assetsByGuid.ContainsKey(guid)) {
                            assetsByGuid.Add(guid, new AssetReference{guid = guid, packageName = "<unknown>"});
                        }
                    }

                    // Get this asset to fill in the details
                    // It might already be in the map if some other asset references it and was processed first
                    AssetReference thisAsset;
                    if (thisAssetsGuid == null)
                    {
                        // If there's no discovered guid it won't be in the map at all, so add it to the list of assets without guids
                        thisAsset = new AssetReference { guid = string.Empty };
                        analysis.assetsWithoutGuids.Add(filePath);
                    }
                    else
                    {
                        thisAsset = assetsByGuid[thisAssetsGuid];
                    }
                    
                    if (!string.IsNullOrEmpty(thisAsset.path) && !thisAsset.path.Equals(filePath)) analysis.duplicatedGuids.Add(thisAsset.guid);
                    thisAsset.references.AddRange(intermediateGuidsList.Skip(1).Distinct()); // TODO: Count references instead of throwing away count?
                    foreach (var referencedAsset in intermediateGuidsList.Skip(1).Select(guid => assetsByGuid[guid]))
                    {
                        referencedAsset.referencedBy.Add(thisAsset.guid);
                    }
                    
                    UpdateAssetReferenceFromFilePath(thisAsset, filePath, generationContext);
                }
            }

            var directoryCount = packages.Length + 1; // Packages + Assets

            if (EditorUtility.DisplayCancelableProgressBar("Parsing Assets directory", $"(1/{directoryCount}): Assets", (float)0 / directoryCount))
            {
                EditorUtility.ClearProgressBar();
                return default;
            }

            ParseDirectory("Assets");

            for (var i = 0; i < packages.Length; i++)
            {
                var directoryIdx = i + 2; // +1 for indexing from 0, +1 for including assets directory
                var package = packages[i];
                if (EditorUtility.DisplayCancelableProgressBar("Parsing all packages", $"({directoryIdx}/{directoryCount}): {package.name}", (float)i+1.0f / directoryCount))
                {
                    EditorUtility.ClearProgressBar();
                    return default;
                }

                ParseDirectory(package.assetPath);
            }

            EditorUtility.ClearProgressBar();

            // Fill in details for assets that were not in the directories we scanned
            foreach (var tuple in assetsByGuid)
            {
                var (guid, assetRef) = (tuple.Key, tuple.Value);
                if (!string.IsNullOrEmpty(assetRef.path)) continue;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) analysis.missingGuidReferences.Add(guid);
                else UpdateAssetReferenceFromFilePath(assetRef, assetPath, generationContext);
            }

            var allAssetReferences = assetsByGuid.Values.ToList();

            return (true, allAssetReferences, analysis);
        }

        private static (DependencyFolderNode, Dictionary<string, DependencyNode>)? BuildFolderTree(List<AssetReference> leafAssetReferences, StaticAnalysisResults analysis, List<string> knownBuiltinAssets)
        {
            var sep = new char[]{'\\', '/'};
            var root = new DependencyFolderNode();
            var guidMap = new Dictionary<string, DependencyNode>();

            if (leafAssetReferences == null) return null;
            // Build folder structure
            foreach (var asset in leafAssetReferences)
            {
                string nodeName = asset.guid; // Use guid by default, not guaranteed we'll know the real name
                DependencyNode.NodeKind nodeKind = DependencyNode.NodeKind.Default;
                DependencyFolderNode parent = null;

                var isMissingAsset = analysis.missingGuidReferences.Contains(asset.guid);
                var isBuiltinAsset = knownBuiltinAssets.Any(path => path.Equals(asset.path));

                switch (isMissingAsset, isBuiltinAsset)
                {
                    case (true, false):
                        nodeKind = DependencyNode.NodeKind.Missing;
                        break;
                    case (false, true):
                        nodeKind = DependencyNode.NodeKind.Builtin;
                        break;
                    case (true, true):
                        Debug.LogWarning($"Unknown asset kind: {asset.guid}");
                        nodeKind = DependencyNode.NodeKind.Unknown;
                        break;
                }
                
                if (!string.IsNullOrEmpty(asset.path))
                {
                    // Go through and add in folder nodes that are missing to reach this node
                    var path = asset.path.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                    parent = root;
                    for (int i = 0; i < (path.Length - 1); i++)
                    {
                        var childFolderNode = parent.Children.FirstOrDefault(c => c.Name == path[i]) as DependencyFolderNode;
                        if (childFolderNode == null) {
                            childFolderNode = new DependencyFolderNode
                            {
                                Name = path[i],
                                PackageName = asset.packageName,
                                PackageSource = asset.packageSource,
                                Parent = parent
                            };
                            parent.Children.Add(childFolderNode);
                        }
                        parent = childFolderNode;
                    }

                    nodeName = path[path.Length - 1];
                }

                var leafNode = new DependencyNode
                    {
                        Name = nodeName,
                        PackageName = asset.packageName,
                        PackageSource = asset.packageSource,
                        Guid = asset.guid,
                        Size = asset.size,
                        Kind = nodeKind,
                        Parent = parent
                    };

                guidMap.Add(asset.guid, leafNode);
                parent?.Children.Add(leafNode);
            }

            // Add dependencies
            foreach (var asset in leafAssetReferences)
            {
                var assetNode = guidMap[asset.guid];
                foreach (var guid in asset.references)
                {
                    if (guidMap.TryGetValue(guid, out DependencyNode dependencyNode)) {
                        assetNode.Dependencies.Add(dependencyNode);
                    }
                }

                foreach (var guid in asset.referencedBy)
                {
                    if (guidMap.TryGetValue(guid, out DependencyNode dependantNode)) {
                        assetNode.Dependants.Add(dependantNode);
                    }
                }
            }
            return (root, guidMap);
        }

        public List<string> KnownBuiltinAssetFilePaths = new List<string>
        {
            "Resources/unity_builtin_extra",
            "Library/unity default resources",
            "Library/unity editor resources"
        };
        public List<AssetReference> RawTree = new List<AssetReference>();
        public StaticAnalysisResults AnalysisResults;
        [NonSerialized] public DependencyFolderNode RootNode;
        [NonSerialized] public Dictionary<string, DependencyNode> NodesByGuid;

        public void Refresh()
        {
            // TODO: Only rebuild what's changed instead of everything
            var listRequest = Client.List();
            EditorApplication.update += OnComplete;

            void OnComplete()
            {
                if (!listRequest.IsCompleted) return;
                EditorApplication.update -= OnComplete;

                if (listRequest.Status == StatusCode.Failure)
                {
                    Debug.Log(listRequest.Error.message);
                    return;
                }

                var generationContext = new GenerationContext(listRequest.Result, KnownBuiltinAssetFilePaths);
                var (succeeded, rawTree, analysisResults) = GenerateTree(generationContext);
                if (!succeeded) return;

                RawTree = rawTree;
                AnalysisResults = analysisResults;
                EditorUtility.SetDirty(this);
                AssetDatabase.SaveAssets();
                RootNode = null;
                OnRefresh?.Invoke();
            }
        }

        public DependencyFolderNode GetRootNode()
        {
            if (RootNode == null || NodesByGuid == null) {
                (RootNode, NodesByGuid) = BuildFolderTree(RawTree, AnalysisResults, KnownBuiltinAssetFilePaths) ?? (null, null);
            }
            return RootNode;
        }

        private static string DependencyTreePath = "Assets/DependencyTree.asset";

        [MenuItem("Assets/Generate Dependency Tree")]
        public static void Generate()
        {
            // TODO: Put the asset somewhere less annoying to users
            if (File.Exists(DependencyTreePath))
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<DependencyTree>(DependencyTreePath);
            }
            else
            {
                var holder = CreateInstance<DependencyTree>();
                Selection.activeObject = holder;
                AssetDatabase.CreateAsset(holder, DependencyTreePath);
            }
        }
    }
}

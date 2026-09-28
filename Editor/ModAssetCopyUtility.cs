using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ModTools.ItemCopy
{
    public class LegacyConversion
    {
        public MonoBehaviour Legacy;
        public MonoBehaviour ObjectAuthoring;
        public MonoBehaviour InventoryItem;
    }

    public static class ModAssetCopyUtility
    {
        public const string ObjectAuthoringTypeName = "ObjectAuthoring";
        public const string InventoryItemTypeName = "InventoryItemAuthoring";
        public const string PlaceableTypeName = "PlaceableObjectAuthoring";
        public const string LegacyAuthoringTypeName = "EntityMonoBehaviourData";

        private static readonly Dictionary<string, Dictionary<(long, long), string>> PackageIndexCache = new Dictionary<string, Dictionary<(long, long), string>>();
        private static Type objectIdType;

        public static string AssetRoot(string assetPath)
        {
            string[] segments = assetPath.Replace('\\', '/').Split('/');
            return segments.Length >= 2 ? segments[0] + "/" + segments[1] : segments[0];
        }

        public static bool IsInAssets(string assetPath)
        {
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal);
        }

        public static string ParentFolder(string assetPath)
        {
            return Path.GetDirectoryName(assetPath).Replace('\\', '/');
        }

        public static string ToPhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && assetPath.StartsWith(info.assetPath, StringComparison.Ordinal))
                    return Path.GetFullPath(Path.Combine(info.resolvedPath, assetPath.Substring(info.assetPath.Length).TrimStart('/')));
                return Path.GetFullPath(assetPath);
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        public static bool AssetFileExists(string assetPath)
        {
            return File.Exists(ToPhysicalPath(assetPath));
        }

        public static Dictionary<(long, long), string> GetAddressIndex(string root)
        {
            bool cacheable = !IsInAssets(root + "/");
            if (cacheable && PackageIndexCache.TryGetValue(root, out Dictionary<(long, long), string> cached))
                return cached;

            Dictionary<(long, long), string> index = new Dictionary<(long, long), string>();
            string physicalRoot = ToPhysicalPath(root).TrimEnd('\\', '/');
            if (Directory.Exists(physicalRoot))
            {
                string physicalData = Path.Combine(physicalRoot, "Data");
                string scanRoot = cacheable && Directory.Exists(physicalData) ? physicalData : physicalRoot;
                string[] files = Directory.GetFiles(scanRoot, "*.asset", SearchOption.AllDirectories);
                try
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (i % 250 == 0)
                            EditorUtility.DisplayProgressBar("Indexing data blocks", root, (float)i / files.Length);

                        string relative = files[i].Substring(physicalRoot.Length).Replace('\\', '/').TrimStart('/');
                        if (relative.Contains("~/") || relative.StartsWith(".", StringComparison.Ordinal) || relative.Contains("/."))
                            continue;

                        if (TryReadAddressFromYaml(files[i], out long low, out long high))
                            index[(low, high)] = root + "/" + relative;
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            if (cacheable && index.Count > 0)
                PackageIndexCache[root] = index;
            if (index.Count == 0)
                Debug.LogWarning("[Mod Asset Copy] No data blocks indexed in " + root + " (" + physicalRoot + ").");
            return index;
        }

        public static string ResolveDataBlock(long low, long high, string root, ref Dictionary<(long, long), string> index)
        {
            if (low == 0L && high == 0L)
                return null;

            byte[] bytes = new byte[16];
            BitConverter.GetBytes(low).CopyTo(bytes, 0);
            BitConverter.GetBytes(high).CopyTo(bytes, 8);
            string path = AssetDatabase.GUIDToAssetPath(new Guid(bytes).ToString("N"));
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                ScriptableObject asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                SerializedProperty address = asset != null ? new SerializedObject(asset).FindProperty("m_address") : null;
                if (address != null
                    && address.FindPropertyRelative("m_low")?.longValue == low
                    && address.FindPropertyRelative("m_high")?.longValue == high)
                    return path;
            }

            if (index == null)
                index = GetAddressIndex(root);
            return index.TryGetValue((low, high), out string found) ? found : null;
        }

        public static string FindTextBlock(string name, params string[] roots)
        {
            string fallback = null;
            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
            {
                if (!AssetDatabase.IsValidFolder(root))
                    continue;

                foreach (string guid in AssetDatabase.FindAssets(name, new[] { root }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(path) != name)
                        continue;

                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null || new SerializedObject(asset).FindProperty("m_localizedTexts") == null)
                        continue;

                    if (path.Contains("/TextDataBlock/Items/"))
                        return path;
                    if (fallback == null)
                        fallback = path;
                }
            }

            return fallback;
        }

        public static string FindDataDirectory(string folder)
        {
            string current = folder.Replace('\\', '/').TrimEnd('/');
            while (!string.IsNullOrEmpty(current) && current != "Assets" && IsInAssets(current + "/"))
            {
                if (AssetDatabase.IsValidFolder(current))
                {
                    string[] guids = AssetDatabase.FindAssets("t:ScriptableDataDirectory", new[] { current });
                    if (guids.Length > 0)
                        return guids.Select(g => ParentFolder(AssetDatabase.GUIDToAssetPath(g))).OrderBy(p => p.Length).First();
                }

                current = ParentFolder(current);
            }

            return null;
        }

        public static bool NeedsDataDirectory(string sourceBlockPath, string targetFolder)
        {
            return AssetRoot(sourceBlockPath) != AssetRoot(targetFolder);
        }

        public static string TargetBlockFolder(string sourceBlockPath, string typeName, string targetFolder, string dataDirectory)
        {
            if (!NeedsDataDirectory(sourceBlockPath, targetFolder))
                return ParentFolder(sourceBlockPath);

            string folder = ParentFolder(sourceBlockPath) + "/";
            int index = folder.LastIndexOf("/Data/", StringComparison.Ordinal);
            string subFolder = index >= 0 ? folder.Substring(index + "/Data/".Length).TrimEnd('/') : typeName;
            return subFolder.Length > 0 ? dataDirectory + "/" + subFolder : dataDirectory;
        }

        public static MonoBehaviour FindComponentByTypeName(GameObject owner, string typeName)
        {
            return owner.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == typeName);
        }

        public static MonoBehaviour FindObjectAuthoring(GameObject owner)
        {
            return FindComponentByTypeName(owner, ObjectAuthoringTypeName);
        }

        public static MonoBehaviour FindLegacyAuthoring(GameObject owner)
        {
            return FindComponentByTypeName(owner, LegacyAuthoringTypeName);
        }

        public static string GetObjectName(GameObject prefab, out bool isLegacy)
        {
            isLegacy = false;
            MonoBehaviour objectAuthoring = FindObjectAuthoring(prefab);
            if (objectAuthoring != null)
                return new SerializedObject(objectAuthoring).FindProperty("objectName").stringValue;

            MonoBehaviour legacy = FindLegacyAuthoring(prefab);
            if (legacy == null)
                return null;

            isLegacy = true;
            return EnumName(new SerializedObject(legacy).FindProperty("objectInfo.objectID"));
        }

        public static string EnumName(SerializedProperty property)
        {
            if (property == null)
                return null;
            return property.enumValueIndex >= 0 ? property.enumNames[property.enumValueIndex] : property.intValue.ToString();
        }

        public static bool IsVanillaObjectName(string name)
        {
            if (objectIdType == null)
            {
                objectIdType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("ObjectID", false))
                    .FirstOrDefault(t => t != null && t.IsEnum);
            }

            return objectIdType != null && Enum.GetNames(objectIdType).Contains(name);
        }

        public static bool ObjectNameUsedInRoot(string objectName, string root)
        {
            if (!AssetDatabase.IsValidFolder(root))
                return false;

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab != null && GetObjectName(prefab, out _) == objectName)
                    return true;
            }

            return false;
        }

        public static void AddressFromGuid(string guid, out long low, out long high)
        {
            byte[] bytes = new Guid(guid).ToByteArray();
            low = BitConverter.ToInt64(bytes, 0);
            high = BitConverter.ToInt64(bytes, 8);
        }

        public static void EnsureFolder(string folder, List<string> report)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = ParentFolder(folder);
            EnsureFolder(parent, report);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(folder))))
                throw new InvalidOperationException("Could not create folder: " + folder);
            report?.Add("Created folder: " + folder);
        }

        public static HashSet<(long, long)> CollectAddresses(GameObject prefab)
        {
            HashSet<(long, long)> result = new HashSet<(long, long)>();
            foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null)
                    continue;

                SerializedProperty iterator = new SerializedObject(component).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.name != "m_address" || iterator.propertyType != SerializedPropertyType.Generic)
                        continue;

                    SerializedProperty low = iterator.FindPropertyRelative("m_low");
                    SerializedProperty high = iterator.FindPropertyRelative("m_high");
                    if (low != null && high != null)
                        result.Add((low.longValue, high.longValue));
                }
            }

            return result;
        }

        public static void RecacheScriptableData()
        {
            Type utility = Type.GetType("ScriptableDataEditorUtility, ScriptableData.Editor");
            utility?.GetMethod("Recache", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
        }

        public static bool TryMapLegacyPath(string legacyPath, out bool toInventory, out string mappedPath)
        {
            const string prefix = "objectInfo.";
            toInventory = false;
            mappedPath = null;
            if (!legacyPath.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string rest = legacyPath.Substring(prefix.Length);
            if (rest.StartsWith("prefabInfo.authoringRef", StringComparison.Ordinal) || rest.StartsWith("prefabInfo.graphicalRef", StringComparison.Ordinal))
            {
                mappedPath = rest.Substring("prefabInfo.".Length);
                return true;
            }

            if (rest.StartsWith("additionalSprites", StringComparison.Ordinal))
            {
                mappedPath = rest;
                return true;
            }

            if (rest == "icon" || rest == "smallIcon" || rest == "sellValue")
            {
                toInventory = true;
                mappedPath = rest;
                return true;
            }

            return false;
        }

        public static LegacyConversion BeginLegacyConversion(GameObject root, List<string> warnings)
        {
            MonoBehaviour legacy = FindLegacyAuthoring(root);
            if (legacy == null)
                return null;

            Type objectAuthoringType = FindMonoBehaviourType(ObjectAuthoringTypeName);
            Type inventoryType = FindMonoBehaviourType(InventoryItemTypeName);
            if (objectAuthoringType == null)
                throw new InvalidOperationException("Type " + ObjectAuthoringTypeName + " not found.");
            if (FindObjectAuthoring(root) != null)
                throw new InvalidOperationException("Prefab has both " + LegacyAuthoringTypeName + " and " + ObjectAuthoringTypeName + ".");

            SerializedObject source = new SerializedObject(legacy);
            LegacyConversion conversion = new LegacyConversion { Legacy = legacy };

            conversion.ObjectAuthoring = (MonoBehaviour)root.AddComponent(objectAuthoringType);
            SerializedObject objectAuthoring = new SerializedObject(conversion.ObjectAuthoring);
            objectAuthoring.FindProperty("objectName").stringValue = EnumName(source.FindProperty("objectInfo.objectID"));
            foreach (string field in new[] { "initialAmount", "variation", "variationIsDynamic", "variationToToggleTo", "objectType", "tags", "rarity", "salvageMultiplier", "isCustomScenePrefab", "additionalSprites" })
                CopyValue(source.FindProperty("objectInfo." + field), objectAuthoring.FindProperty(field));
            CopyValue(source.FindProperty("objectInfo.prefabInfo.graphicalRef"), objectAuthoring.FindProperty("graphicalRef"));
            CopyValue(source.FindProperty("objectInfo.prefabInfo.authoringRef"), objectAuthoring.FindProperty("authoringRef"));
            Component graphical = source.FindProperty("objectInfo.prefabInfo.prefab").objectReferenceValue as Component;
            objectAuthoring.FindProperty("graphicalPrefab").objectReferenceValue = graphical != null ? graphical.gameObject : null;
            objectAuthoring.ApplyModifiedPropertiesWithoutUndo();

            SerializedProperty recipe = source.FindProperty("objectInfo.requiredObjectsToCraft");
            bool isItem = source.FindProperty("objectInfo.icon").objectReferenceValue != null
                || source.FindProperty("objectInfo.smallIcon").objectReferenceValue != null
                || recipe.arraySize > 0
                || source.FindProperty("objectInfo.sellValue").intValue != -1;

            MonoBehaviour existingInventory = FindComponentByTypeName(root, InventoryItemTypeName);
            if (existingInventory != null)
            {
                conversion.InventoryItem = existingInventory;
            }
            else if (isItem && inventoryType != null)
            {
                conversion.InventoryItem = (MonoBehaviour)root.AddComponent(inventoryType);
                SerializedObject inventory = new SerializedObject(conversion.InventoryItem);
                foreach (string field in new[] { "sellValue", "buyValueMultiplier", "icon", "iconOffset", "smallIcon", "isStackable", "craftingTime" })
                    CopyValue(source.FindProperty("objectInfo." + field), inventory.FindProperty(field));

                SerializedProperty targetRecipe = inventory.FindProperty("requiredObjectsToCraft");
                targetRecipe.arraySize = recipe.arraySize;
                for (int i = 0; i < recipe.arraySize; i++)
                {
                    SerializedProperty from = recipe.GetArrayElementAtIndex(i);
                    SerializedProperty to = targetRecipe.GetArrayElementAtIndex(i);
                    to.FindPropertyRelative("objectName").stringValue = EnumName(from.FindPropertyRelative("objectID"));
                    to.FindPropertyRelative("amount").intValue = from.FindPropertyRelative("amount").intValue;
                }

                inventory.ApplyModifiedPropertiesWithoutUndo();
            }

            MonoBehaviour placeable = FindComponentByTypeName(root, PlaceableTypeName);
            if (placeable != null)
            {
                SerializedObject placeableObject = new SerializedObject(placeable);
                foreach (string field in new[] { "prefabTileSize", "prefabCornerOffset", "centerIsAtEntityPosition", "appearInMapUI", "mapColor" })
                    CopyValue(source.FindProperty("objectInfo." + field), placeableObject.FindProperty(field));
                placeableObject.ApplyModifiedPropertiesWithoutUndo();
            }
            else if (source.FindProperty("objectInfo.prefabTileSize").vector2IntValue != Vector2Int.one
                || source.FindProperty("objectInfo.prefabCornerOffset").vector2IntValue != Vector2Int.zero
                || source.FindProperty("objectInfo.centerIsAtEntityPosition").boolValue
                || source.FindProperty("objectInfo.appearInMapUI").boolValue)
            {
                warnings.Add("Legacy tile size/map settings not transferred: prefab has no " + PlaceableTypeName + ".");
            }

            SerializedProperty iconSkin = source.FindProperty("objectInfo.iconSkinAssetRef.m_address");
            if (iconSkin != null && (iconSkin.FindPropertyRelative("m_low").longValue != 0L || iconSkin.FindPropertyRelative("m_high").longValue != 0L))
                warnings.Add("Legacy iconSkinAssetRef not transferred (ObjectAuthoring has no such field).");
            if (source.FindProperty("objectInfo.languageGenders").arraySize > 0 && FindComponentByTypeName(root, "LocalizationAuthoring") == null)
                warnings.Add("Legacy languageGenders not transferred: prefab has no LocalizationAuthoring.");
            if ((source.FindProperty("objectInfo.tileset").intValue != 0 || source.FindProperty("objectInfo.tileType").intValue != 0) && FindComponentByTypeName(root, "TileAuthoring") == null)
                warnings.Add("Legacy tileset/tileType not transferred: prefab has no TileAuthoring.");
            if (HasNonDefaultValue(source.FindProperty("objectInfo.craftingSettings")))
                warnings.Add("Legacy craftingSettings not transferred (InventoryItemAuthoring has no such field).");
            if (source.FindProperty("optionalPreviewPrefab")?.objectReferenceValue != null)
                warnings.Add("Legacy optionalPreviewPrefab not transferred.");
            if (!isItem)
                warnings.Add("Legacy object has no icon/recipe/sell value, no InventoryItemAuthoring was added.");

            return conversion;
        }

        public static void FinishLegacyConversion(LegacyConversion conversion)
        {
            if (conversion == null)
                return;

            UnityEngine.Object.DestroyImmediate(conversion.Legacy);
            MoveComponentTo(conversion.ObjectAuthoring, 1);
            if (conversion.InventoryItem != null)
                MoveComponentTo(conversion.InventoryItem, 2);
        }

        private static void MoveComponentTo(Component component, int index)
        {
            while (Array.IndexOf(component.GetComponents<Component>(), component) > index && ComponentUtility.MoveComponentUp(component))
            {
            }
        }

        private static Type FindMonoBehaviourType(string name)
        {
            return TypeCache.GetTypesDerivedFrom<MonoBehaviour>().FirstOrDefault(t => t.Name == name && string.IsNullOrEmpty(t.Namespace));
        }

        private static bool HasNonDefaultValue(SerializedProperty property)
        {
            if (property == null)
                return false;

            SerializedProperty iterator = property.Copy();
            SerializedProperty end = property.GetEndProperty();
            if (!iterator.Next(true))
                return false;

            do
            {
                if (SerializedProperty.EqualContents(iterator, end))
                    break;

                switch (iterator.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                        if (iterator.longValue != 0L)
                            return true;
                        break;
                    case SerializedPropertyType.Boolean:
                        if (iterator.boolValue)
                            return true;
                        break;
                }
            }
            while (iterator.Next(true));

            return false;
        }

        private static void CopyValue(SerializedProperty from, SerializedProperty to)
        {
            if (from == null || to == null)
                return;

            if (from.isArray && from.propertyType != SerializedPropertyType.String)
            {
                to.arraySize = from.arraySize;
                for (int i = 0; i < from.arraySize; i++)
                    CopyValue(from.GetArrayElementAtIndex(i), to.GetArrayElementAtIndex(i));
                return;
            }

            switch (from.propertyType)
            {
                case SerializedPropertyType.Integer:
                    to.longValue = from.longValue;
                    break;
                case SerializedPropertyType.Enum:
                    to.intValue = from.intValue;
                    break;
                case SerializedPropertyType.Boolean:
                    to.boolValue = from.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    to.floatValue = from.floatValue;
                    break;
                case SerializedPropertyType.String:
                    to.stringValue = from.stringValue;
                    break;
                case SerializedPropertyType.ObjectReference:
                    to.objectReferenceValue = from.objectReferenceValue;
                    break;
                case SerializedPropertyType.Vector2:
                    to.vector2Value = from.vector2Value;
                    break;
                case SerializedPropertyType.Vector2Int:
                    to.vector2IntValue = from.vector2IntValue;
                    break;
                case SerializedPropertyType.Color:
                    to.colorValue = from.colorValue;
                    break;
                case SerializedPropertyType.Generic:
                    SerializedProperty child = from.Copy();
                    SerializedProperty end = from.GetEndProperty();
                    if (!child.Next(true))
                        break;
                    do
                    {
                        if (SerializedProperty.EqualContents(child, end))
                            break;
                        CopyValue(child, to.FindPropertyRelative(child.name));
                    }
                    while (child.Next(false));
                    break;
            }
        }

        private static bool TryReadAddressFromYaml(string file, out long low, out long high)
        {
            low = 0L;
            high = 0L;
            using (StreamReader reader = new StreamReader(file))
            {
                for (int lineIndex = 0; lineIndex < 80; lineIndex++)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        return false;
                    if (line.TrimEnd() != "  m_address:")
                        continue;

                    string lowLine = reader.ReadLine();
                    string highLine = reader.ReadLine();
                    return lowLine != null && highLine != null
                        && long.TryParse(lowLine.Trim().Replace("m_low:", string.Empty).Trim(), out low)
                        && long.TryParse(highLine.Trim().Replace("m_high:", string.Empty).Trim(), out high)
                        && (low != 0L || high != 0L);
                }
            }

            return false;
        }
    }
}
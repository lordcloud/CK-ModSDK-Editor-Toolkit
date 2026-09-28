using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ModTools.ItemCopy
{
    public class ItemCopyWindow : EditorWindow
    {
        private const string MenuPath = "Assets/Mod Tools/Copy Item...";
        private const string WindowTitle = "Copy Item";
        private const string LastFolderPrefsKey = "ModTools.ItemCopy.LastFolder";
        private const string PrefixPrefsKey = "ModTools.ItemCopy.ObjectNamePrefix";
        private const long EnglishLanguageLow = 681352171529052915L;

        private static readonly Regex ObjectNameRegex = new Regex("^[A-Za-z][A-Za-z0-9_]*$");

        private class AddressRef
        {
            public int ComponentIndex;
            public string PropertyPath;
        }

        private class AssetReferenceField
        {
            public string PropertyPath;
            public UnityEngine.Object Value;
        }

        private class BlockCopy
        {
            public string SourcePath;
            public string TypeName;
            public long SourceLow;
            public long SourceHigh;
            public bool IsEntityBlock;
            public bool Duplicate;
            public string NewName = string.Empty;
            public bool NameEdited;
            public string NewPath;
            public long NewLow;
            public long NewHigh;
            public readonly List<AddressRef> Refs = new List<AddressRef>();
            public readonly List<AssetReferenceField> AssetFields = new List<AssetReferenceField>();
        }

        private class ObjectRefField
        {
            public int ComponentIndex;
            public string PropertyPath;
            public GUIContent Label;
            public Type FieldType;
            public UnityEngine.Object Value;
        }

        private string sourcePrefabPath;
        private string sourceObjectName;
        private string sourceRoot;
        private bool sourceIsLegacy;
        private string textBlockPath;
        private readonly List<BlockCopy> blocks = new List<BlockCopy>();
        private readonly List<ObjectRefField> objectRefs = new List<ObjectRefField>();
        private readonly List<string> unresolvedRefs = new List<string>();
        private string loadError;

        private string objectNamePrefix = string.Empty;
        private string newObjectName = string.Empty;
        private string targetFolder = string.Empty;
        private string prefabName = string.Empty;
        private bool prefabNameEdited;
        private string dataDirectory = string.Empty;
        private bool dataDirectoryEdited;
        private string dataDirectoryResolvedFor;
        private string itemTitle = string.Empty;
        private string itemDescription = string.Empty;

        private List<string> errors = new List<string>();
        private Vector2 scroll;
        private GUIStyle descriptionStyle;

        [MenuItem(MenuPath, true)]
        private static bool CanOpen()
        {
            GameObject prefab = Selection.activeObject as GameObject;
            return prefab != null
                && AssetDatabase.GetAssetPath(prefab).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                && (ModAssetCopyUtility.FindObjectAuthoring(prefab) != null || ModAssetCopyUtility.FindLegacyAuthoring(prefab) != null);
        }

        [MenuItem(MenuPath, false, 1901)]
        private static void Open()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            ItemCopyWindow window = GetWindow<ItemCopyWindow>(true, WindowTitle, true);
            window.minSize = new Vector2(580f, 580f);
            try
            {
                window.Load(path);
            }
            catch (Exception exception)
            {
                window.loadError = "Loading the source failed: " + exception.Message;
                Debug.LogException(exception);
            }
        }

        private void Load(string path)
        {
            blocks.Clear();
            objectRefs.Clear();
            unresolvedRefs.Clear();
            loadError = null;
            errors = new List<string>();
            sourcePrefabPath = path;
            sourceRoot = ModAssetCopyUtility.AssetRoot(path);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            sourceObjectName = ModAssetCopyUtility.GetObjectName(prefab, out sourceIsLegacy);

            Dictionary<(long, long), string> addressIndex = null;
            Dictionary<string, BlockCopy> blockByPath = new Dictionary<string, BlockCopy>();
            MonoBehaviour[] components = prefab.GetComponentsInChildren<MonoBehaviour>(true);

            for (int c = 0; c < components.Length; c++)
            {
                if (components[c] == null)
                    continue;

                SerializedProperty iterator = new SerializedObject(components[c]).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        UnityEngine.Object value = iterator.objectReferenceValue;
                        if (value is Sprite || value is Texture2D)
                        {
                            objectRefs.Add(new ObjectRefField
                            {
                                ComponentIndex = c,
                                PropertyPath = iterator.propertyPath,
                                Label = new GUIContent(ShortPropertyLabel(iterator.propertyPath), components[c].GetType().Name + "." + iterator.propertyPath),
                                FieldType = value.GetType(),
                                Value = value
                            });
                        }
                        continue;
                    }

                    if (iterator.name != "m_address" || iterator.propertyType != SerializedPropertyType.Generic)
                        continue;

                    SerializedProperty lowProperty = iterator.FindPropertyRelative("m_low");
                    SerializedProperty highProperty = iterator.FindPropertyRelative("m_high");
                    if (lowProperty == null || highProperty == null)
                        continue;

                    long low = lowProperty.longValue;
                    long high = highProperty.longValue;
                    string blockPath = ModAssetCopyUtility.ResolveDataBlock(low, high, sourceRoot, ref addressIndex);
                    if (blockPath == null)
                    {
                        if (low != 0L || high != 0L)
                            unresolvedRefs.Add(components[c].GetType().Name + "." + iterator.propertyPath);
                        continue;
                    }

                    if (!blockByPath.TryGetValue(blockPath, out BlockCopy block))
                    {
                        block = CreateBlockCopy(blockPath, low, high, prefab);
                        if (block == null)
                            continue;
                        blockByPath.Add(blockPath, block);
                        blocks.Add(block);
                    }

                    block.Refs.Add(new AddressRef { ComponentIndex = c, PropertyPath = iterator.propertyPath });
                }
            }

            textBlockPath = ModAssetCopyUtility.FindTextBlock(sourceObjectName, sourceRoot);
            itemTitle = string.Empty;
            itemDescription = string.Empty;
            if (textBlockPath != null)
            {
                SerializedProperty english = FindEnglishEntry(new SerializedObject(AssetDatabase.LoadMainAssetAtPath(textBlockPath)));
                if (english != null)
                {
                    itemTitle = english.FindPropertyRelative("title").stringValue;
                    itemDescription = english.FindPropertyRelative("description").stringValue;
                }
            }

            objectNamePrefix = EditorPrefs.GetString(PrefixPrefsKey, string.Empty);
            newObjectName = ModAssetCopyUtility.IsInAssets(path) ? sourceObjectName : objectNamePrefix + sourceObjectName;
            targetFolder = ModAssetCopyUtility.IsInAssets(path) ? ModAssetCopyUtility.ParentFolder(path) : EditorPrefs.GetString(LastFolderPrefsKey, "Assets");
            prefabNameEdited = false;
            dataDirectoryEdited = false;
            dataDirectoryResolvedFor = null;
            UpdateAutoNames();
        }

        private BlockCopy CreateBlockCopy(string blockPath, long low, long high, GameObject prefab)
        {
            UnityEngine.Object blockAsset = AssetDatabase.LoadMainAssetAtPath(blockPath);
            if (blockAsset == null)
                return null;

            SerializedObject serializedBlock = new SerializedObject(blockAsset);
            SerializedProperty prefabProperty = serializedBlock.FindProperty("prefab");

            BlockCopy block = new BlockCopy
            {
                SourcePath = blockPath,
                TypeName = blockAsset.GetType().Name,
                SourceLow = low,
                SourceHigh = high,
                IsEntityBlock = prefabProperty != null
                    && prefabProperty.propertyType == SerializedPropertyType.ObjectReference
                    && prefabProperty.objectReferenceValue == prefab
            };

            SerializedProperty iterator = serializedBlock.GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.name != "m_AssetGUID" || iterator.propertyType != SerializedPropertyType.String)
                    continue;

                string referencedPath = string.IsNullOrEmpty(iterator.stringValue) ? null : AssetDatabase.GUIDToAssetPath(iterator.stringValue);
                block.AssetFields.Add(new AssetReferenceField
                {
                    PropertyPath = iterator.propertyPath,
                    Value = string.IsNullOrEmpty(referencedPath) ? null : AssetDatabase.LoadMainAssetAtPath(referencedPath)
                });
            }

            block.Duplicate = true;
            return block;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(sourcePrefabPath))
            {
                EditorGUILayout.HelpBox("Right-click an item prefab and choose " + MenuPath, MessageType.Info);
                return;
            }

            if (loadError != null)
            {
                EditorGUILayout.HelpBox(loadError + "\nDetails in the console.", MessageType.Error);
                return;
            }

            if (descriptionStyle == null)
                descriptionStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (unresolvedRefs.Count > 0)
                EditorGUILayout.HelpBox("Data block references not found (stay shared with the source):\n" + string.Join("\n", unresolvedRefs), MessageType.Warning);

            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Prefab", sourcePrefabPath);
                EditorGUILayout.TextField("Object name", sourceObjectName + (sourceIsLegacy ? "   (legacy, will be converted)" : string.Empty));
                EditorGUILayout.TextField("Text block", textBlockPath ?? "(none found)");
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("New Item", EditorStyles.boldLabel);
            string prefix = EditorGUILayout.TextField(new GUIContent("Name prefix", "Mod prefix for object names, e.g. MyMod_. Stripped when deriving file names. Saved per editor."), objectNamePrefix).Trim();
            if (prefix != objectNamePrefix)
            {
                objectNamePrefix = prefix;
                EditorPrefs.SetString(PrefixPrefsKey, objectNamePrefix);
            }
            newObjectName = EditorGUILayout.TextField("Object name", newObjectName).Trim();
            UpdateAutoNames();

            targetFolder = EditorGUILayout.TextField(new GUIContent("Folder", "Target folder for the new prefab"), targetFolder);
            prefabName = NameField("Prefab name", prefabName, ref prefabNameEdited);

            if (NeedsDataDirectory())
            {
                ResolveDataDirectory();
                dataDirectory = NameField("Data folder", dataDirectory, ref dataDirectoryEdited);
            }

            if (textBlockPath != null)
            {
                itemTitle = EditorGUILayout.TextField("Title (EN)", itemTitle);
                EditorGUILayout.LabelField("Description (EN)");
                itemDescription = EditorGUILayout.TextArea(itemDescription, descriptionStyle, GUILayout.MinHeight(54f));
            }

            if (blocks.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Data Blocks", EditorStyles.boldLabel);
                foreach (BlockCopy block in blocks)
                {
                    string refs = string.Join(", ", block.Refs.Select(r => r.PropertyPath.Replace(".m_address", string.Empty)).Distinct());
                    using (new EditorGUI.DisabledScope(block.IsEntityBlock))
                        block.Duplicate = EditorGUILayout.ToggleLeft(block.TypeName + "  (" + refs + ")", block.Duplicate || block.IsEntityBlock);

                    if (!block.Duplicate)
                        continue;

                    EditorGUI.indentLevel++;
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.TextField("Source", block.SourcePath);
                        if (block.IsEntityBlock)
                            EditorGUILayout.TextField("New name", block.NewName);
                    }

                    if (!block.IsEntityBlock)
                        block.NewName = NameField("New name", block.NewName, ref block.NameEdited);

                    foreach (AssetReferenceField field in block.AssetFields)
                        field.Value = EditorGUILayout.ObjectField(field.PropertyPath.Replace(".m_AssetGUID", string.Empty), field.Value, typeof(UnityEngine.Object), false);

                    EditorGUI.indentLevel--;
                }
            }

            if (objectRefs.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Sprites and Textures", EditorStyles.boldLabel);
                foreach (ObjectRefField field in objectRefs)
                    field.Value = EditorGUILayout.ObjectField(field.Label, field.Value, field.FieldType, false, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            EditorGUILayout.EndScrollView();

            if (errors.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n", errors), MessageType.Error);

            EditorGUILayout.HelpBox("Everything else (stats, conditions, recipe, weapon/projectile setup) is copied unchanged from the source.", MessageType.None);

            if (GUILayout.Button("Create Item", GUILayout.Height(30f)))
            {
                errors = Validate();
                if (errors.Count == 0)
                {
                    Create();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private static string ShortPropertyLabel(string propertyPath)
        {
            string path = propertyPath.Replace(".Array.data[", "[");
            string last = path.Substring(path.LastIndexOf('.') + 1);
            int bracket = last.IndexOf('[');
            if (bracket < 0)
                return ObjectNames.NicifyVariableName(last);
            return ObjectNames.NicifyVariableName(last.Substring(0, bracket)) + " " + last.Substring(bracket);
        }

        private static string NameField(string label, string value, ref bool edited)
        {
            string result;
            using (new EditorGUILayout.HorizontalScope())
            {
                result = EditorGUILayout.TextField(label, value);
                if (result != value)
                    edited = true;

                using (new EditorGUI.DisabledScope(!edited))
                {
                    if (GUILayout.Button("Auto", GUILayout.Width(48f)))
                    {
                        edited = false;
                        GUI.FocusControl(null);
                    }
                }
            }

            return result;
        }

        private bool NeedsDataDirectory()
        {
            string folder = NormalizedFolder();
            if (textBlockPath != null && ModAssetCopyUtility.NeedsDataDirectory(textBlockPath, folder))
                return true;
            return blocks.Any(b => b.Duplicate && ModAssetCopyUtility.NeedsDataDirectory(b.SourcePath, folder));
        }

        private void ResolveDataDirectory()
        {
            string folder = NormalizedFolder();
            if (dataDirectoryEdited || dataDirectoryResolvedFor == folder)
                return;

            dataDirectoryResolvedFor = folder;
            dataDirectory = ModAssetCopyUtility.FindDataDirectory(folder) ?? string.Empty;
        }

        private void UpdateAutoNames()
        {
            if (!prefabNameEdited)
                prefabName = AutoRename(Path.GetFileNameWithoutExtension(sourcePrefabPath), newObjectName + "Entity");

            foreach (BlockCopy block in blocks)
            {
                if (block.IsEntityBlock)
                {
                    block.NewName = prefabName;
                    continue;
                }

                if (!block.NameEdited)
                {
                    string sourceName = Path.GetFileNameWithoutExtension(block.SourcePath);
                    block.NewName = AutoRename(sourceName, CoreName(newObjectName) + "_" + sourceName);
                }
            }
        }

        private string AutoRename(string sourceName, string fallback)
        {
            string oldCore = CoreName(sourceObjectName);
            string newCore = CoreName(newObjectName);
            if (oldCore.Length > 0 && newCore.Length > 0 && sourceName.Contains(oldCore))
                return sourceName.Replace(oldCore, newCore);
            return fallback;
        }

        private string CoreName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(objectNamePrefix))
                return objectName ?? string.Empty;
            return objectName.StartsWith(objectNamePrefix, StringComparison.Ordinal) ? objectName.Substring(objectNamePrefix.Length) : objectName;
        }

        private string NormalizedFolder()
        {
            return targetFolder.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private string NormalizedDataDirectory()
        {
            return dataDirectory.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private string TargetBlockPath(string sourcePath, string typeName, string newName)
        {
            return ModAssetCopyUtility.TargetBlockFolder(sourcePath, typeName, NormalizedFolder(), NormalizedDataDirectory()) + "/" + newName + ".asset";
        }

        private List<string> Validate()
        {
            List<string> result = new List<string>();
            string folder = NormalizedFolder();

            if (!ModAssetCopyUtility.IsInAssets(folder + "/") || folder.Split('/').Length < 2)
                result.Add("Folder must be inside a mod folder under Assets/.");
            else if (!IsValidFolderPath(folder))
                result.Add("Folder contains an invalid segment: " + folder);

            if (!ObjectNameRegex.IsMatch(newObjectName))
                result.Add("Object name must start with a letter and contain only letters, digits and underscores.");
            else if (newObjectName == sourceObjectName)
                result.Add("Object name must differ from the source.");
            else if (ModAssetCopyUtility.IsVanillaObjectName(newObjectName))
                result.Add("Object name " + newObjectName + " is a vanilla ObjectID.");
            else if (result.Count == 0 && ModAssetCopyUtility.ObjectNameUsedInRoot(newObjectName, ModAssetCopyUtility.AssetRoot(folder)))
                result.Add("Object name " + newObjectName + " is already used by a prefab in " + ModAssetCopyUtility.AssetRoot(folder) + ".");

            if (NeedsDataDirectory())
            {
                string data = NormalizedDataDirectory();
                if (data.Length == 0)
                    result.Add("Data folder is empty. No ScriptableDataDirectory was found above the target folder.");
                else if (!ModAssetCopyUtility.IsInAssets(data + "/") || !IsValidFolderPath(data))
                    result.Add("Data folder must be a valid folder under Assets/.");
            }

            if (result.Count > 0)
                return result;

            HashSet<string> targets = new HashSet<string>();
            AddTargetErrors(result, targets, "Prefab", prefabName, folder + "/" + prefabName + ".prefab");

            foreach (BlockCopy block in blocks.Where(b => b.Duplicate))
                AddTargetErrors(result, targets, block.TypeName, block.NewName, TargetBlockPath(block.SourcePath, block.TypeName, block.NewName));

            if (textBlockPath != null)
            {
                AddTargetErrors(result, targets, "TextDataBlock", newObjectName, TargetBlockPath(textBlockPath, "TextDataBlock", newObjectName));
                if (string.IsNullOrWhiteSpace(itemTitle))
                    result.Add("Title is empty.");
            }

            return result;
        }

        private static bool IsValidFolderPath(string folder)
        {
            return !folder.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
        }

        private static void AddTargetErrors(List<string> result, HashSet<string> targets, string label, string name, string path)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                result.Add(label + ": invalid name '" + name + "'.");
            else if (!targets.Add(path))
                result.Add(label + ": target used twice: " + path);
            else if (ModAssetCopyUtility.AssetFileExists(path))
                result.Add(label + ": asset already exists: " + path);
        }

        private void Create()
        {
            List<string> report = new List<string>();
            List<string> warnings = new List<string>();
            string folder = NormalizedFolder();
            string newPrefabPath = folder + "/" + prefabName + ".prefab";
            string newTextBlockPath = null;
            bool success = false;

            try
            {
                ModAssetCopyUtility.EnsureFolder(folder, report);
                CopyAsset(sourcePrefabPath, newPrefabPath, report);

                foreach (BlockCopy block in blocks.Where(b => b.Duplicate))
                {
                    block.NewPath = TargetBlockPath(block.SourcePath, block.TypeName, block.NewName);
                    ModAssetCopyUtility.EnsureFolder(ModAssetCopyUtility.ParentFolder(block.NewPath), report);
                    CopyAsset(block.SourcePath, block.NewPath, report);
                    SerializedObject serializedBlock = AssignDataBlockIdentity(block.NewPath, out block.NewLow, out block.NewHigh);

                    foreach (AssetReferenceField field in block.AssetFields)
                        serializedBlock.FindProperty(field.PropertyPath).stringValue = AssetGuid(field.Value);

                    serializedBlock.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(serializedBlock.targetObject);
                }

                if (textBlockPath != null)
                {
                    newTextBlockPath = TargetBlockPath(textBlockPath, "TextDataBlock", newObjectName);
                    ModAssetCopyUtility.EnsureFolder(ModAssetCopyUtility.ParentFolder(newTextBlockPath), report);
                    CopyAsset(textBlockPath, newTextBlockPath, report);
                    SerializedObject serializedText = AssignDataBlockIdentity(newTextBlockPath, out _, out _);
                    WriteTexts(serializedText);
                    serializedText.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(serializedText.targetObject);
                }

                WritePrefab(newPrefabPath, warnings);

                foreach (BlockCopy block in blocks.Where(b => b.Duplicate && b.IsEntityBlock))
                {
                    SerializedObject serializedBlock = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(block.NewPath));
                    serializedBlock.FindProperty("prefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
                    serializedBlock.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(serializedBlock.targetObject);
                }

                AssetDatabase.SaveAssets();
                Verify(newPrefabPath, newTextBlockPath, warnings);
                EditorPrefs.SetString(LastFolderPrefsKey, folder);
                success = true;
            }
            catch (Exception exception)
            {
                warnings.Add("Aborted: " + exception.Message);
                warnings.Add("Assets created so far are listed in the report and were not removed.");
                Debug.LogException(exception);
            }

            AssetDatabase.Refresh();
            ModAssetCopyUtility.RecacheScriptableData();

            Debug.Log("[Copy Item]\n" + string.Join("\n", report));
            foreach (string warning in warnings)
                Debug.LogWarning("[Copy Item] " + warning);

            string message = success ? "Item created: " + newPrefabPath : "Copy failed.";
            if (warnings.Count > 0)
                message += "\n\n" + string.Join("\n", warnings);
            message += "\n\nFull report in the console.";
            EditorUtility.DisplayDialog(WindowTitle, message, "OK");

            if (success)
            {
                GameObject created = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
                Selection.activeObject = created;
                EditorGUIUtility.PingObject(created);
            }

            Close();
        }

        private void WriteTexts(SerializedObject serializedText)
        {
            SerializedProperty values = serializedText.FindProperty("m_localizedTexts.values");
            if (values != null && values.isArray)
            {
                for (int i = 0; i < values.arraySize; i++)
                {
                    SerializedProperty entry = values.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("title").stringValue = itemTitle;
                    entry.FindPropertyRelative("description").stringValue = itemDescription;
                }
            }

            SerializedProperty primaryTitle = serializedText.FindProperty("m_prevImportPrimaryEntry.title");
            SerializedProperty primaryDescription = serializedText.FindProperty("m_prevImportPrimaryEntry.description");
            if (primaryTitle != null)
                primaryTitle.stringValue = itemTitle;
            if (primaryDescription != null)
                primaryDescription.stringValue = itemDescription;
        }

        private void WritePrefab(string newPrefabPath, List<string> warnings)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(newPrefabPath);
            try
            {
                root.name = prefabName;
                MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
                LegacyConversion conversion = ModAssetCopyUtility.BeginLegacyConversion(root, warnings);
                Dictionary<MonoBehaviour, SerializedObject> serialized = new Dictionary<MonoBehaviour, SerializedObject>();

                SerializedObject Get(MonoBehaviour component)
                {
                    if (!serialized.TryGetValue(component, out SerializedObject serializedObject))
                    {
                        serializedObject = new SerializedObject(component);
                        serialized.Add(component, serializedObject);
                    }
                    return serializedObject;
                }

                SerializedProperty Resolve(int componentIndex, string propertyPath)
                {
                    MonoBehaviour component = components[componentIndex];
                    if (conversion == null || component != conversion.Legacy)
                        return Get(component).FindProperty(propertyPath);

                    if (!ModAssetCopyUtility.TryMapLegacyPath(propertyPath, out bool toInventory, out string mappedPath))
                    {
                        warnings.Add("Legacy field " + propertyPath + " has no target on the converted prefab.");
                        return null;
                    }

                    MonoBehaviour target = toInventory ? conversion.InventoryItem : conversion.ObjectAuthoring;
                    if (target == null)
                    {
                        warnings.Add("Legacy field " + propertyPath + " skipped, target component missing.");
                        return null;
                    }

                    return Get(target).FindProperty(mappedPath);
                }

                MonoBehaviour objectAuthoring = conversion != null ? conversion.ObjectAuthoring : ModAssetCopyUtility.FindObjectAuthoring(root);
                Get(objectAuthoring).FindProperty("objectName").stringValue = newObjectName;

                foreach (BlockCopy block in blocks.Where(b => b.Duplicate))
                {
                    foreach (AddressRef reference in block.Refs)
                    {
                        SerializedProperty address = Resolve(reference.ComponentIndex, reference.PropertyPath);
                        if (address == null)
                            continue;
                        address.FindPropertyRelative("m_low").longValue = block.NewLow;
                        address.FindPropertyRelative("m_high").longValue = block.NewHigh;
                    }
                }

                foreach (ObjectRefField field in objectRefs)
                {
                    SerializedProperty property = Resolve(field.ComponentIndex, field.PropertyPath);
                    if (property != null)
                        property.objectReferenceValue = field.Value;
                }

                foreach (SerializedObject serializedObject in serialized.Values)
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();

                ModAssetCopyUtility.FinishLegacyConversion(conversion);
                PrefabUtility.SaveAsPrefabAsset(root, newPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private void Verify(string newPrefabPath, string newTextBlockPath, List<string> warnings)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
            HashSet<(long, long)> addresses = ModAssetCopyUtility.CollectAddresses(prefab);

            if (ModAssetCopyUtility.GetObjectName(prefab, out bool stillLegacy) != newObjectName || stillLegacy)
                warnings.Add("New prefab has no ObjectAuthoring with objectName " + newObjectName + ".");

            foreach (BlockCopy block in blocks.Where(b => b.Duplicate))
            {
                ModAssetCopyUtility.AddressFromGuid(AssetDatabase.AssetPathToGUID(block.NewPath), out long low, out long high);
                if (!addresses.Contains((low, high)))
                    warnings.Add("Prefab does not reference " + block.NewPath + ".");
                if (addresses.Contains((block.SourceLow, block.SourceHigh)))
                    warnings.Add("Prefab still references the source block " + block.SourcePath + ".");

                if (block.IsEntityBlock)
                {
                    SerializedObject serializedBlock = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(block.NewPath));
                    if (serializedBlock.FindProperty("prefab").objectReferenceValue != prefab)
                        warnings.Add(block.NewPath + " does not point to the new prefab.");
                }
            }

            if (newTextBlockPath != null)
            {
                UnityEngine.Object textBlock = AssetDatabase.LoadMainAssetAtPath(newTextBlockPath);
                SerializedProperty header = new SerializedObject(textBlock).FindProperty("m_header");
                SerializedProperty sourceHeader = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(textBlockPath)).FindProperty("m_header");
                if (header != null && sourceHeader != null && header.stringValue != sourceHeader.stringValue)
                    warnings.Add("TextDataBlock m_header is '" + header.stringValue + "', source has '" + sourceHeader.stringValue + "'.");
                if (textBlock.name != newObjectName)
                    warnings.Add("TextDataBlock name is '" + textBlock.name + "', expected '" + newObjectName + "'.");
            }

            string sourceFolder = ModAssetCopyUtility.ParentFolder(sourcePrefabPath) + "/";
            if (NormalizedFolder() + "/" != sourceFolder)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(newPrefabPath, false))
                {
                    if (dependency.StartsWith(sourceFolder, StringComparison.Ordinal) && !dependency.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        warnings.Add("Prefab still references source asset " + dependency);
                }
            }
        }

        private static SerializedObject AssignDataBlockIdentity(string path, out long low, out long high)
        {
            ScriptableObject asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
            if (asset == null)
                throw new InvalidOperationException("Not a ScriptableObject: " + path);

            asset.name = Path.GetFileNameWithoutExtension(path);
            ModAssetCopyUtility.AddressFromGuid(AssetDatabase.AssetPathToGUID(path), out low, out high);

            SerializedObject serializedObject = new SerializedObject(asset);
            SerializedProperty address = serializedObject.FindProperty("m_address");
            address.FindPropertyRelative("m_low").longValue = low;
            address.FindPropertyRelative("m_high").longValue = high;
            return serializedObject;
        }

        private static SerializedProperty FindEnglishEntry(SerializedObject textBlock)
        {
            SerializedProperty values = textBlock.FindProperty("m_localizedTexts.values");
            if (values == null || !values.isArray)
                return null;

            for (int i = 0; i < values.arraySize; i++)
            {
                SerializedProperty entry = values.GetArrayElementAtIndex(i);
                SerializedProperty low = entry.FindPropertyRelative("m_language.m_address.m_low");
                if (low != null && low.longValue == EnglishLanguageLow)
                    return entry;
            }

            return null;
        }

        private static void CopyAsset(string from, string to, List<string> report)
        {
            if (!AssetDatabase.CopyAsset(from, to))
                throw new InvalidOperationException("Copy failed: " + from + " -> " + to);
            report.Add("Created: " + to);
        }

        private static string AssetGuid(UnityEngine.Object asset)
        {
            return asset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : string.Empty;
        }
    }
}
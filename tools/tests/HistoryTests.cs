using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jeni.KaihenNavi;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using nadena.dev.modular_avatar.core;
using Object = UnityEngine.Object;

/// <summary>Undo 本体は模倣せず、履歴の分類・保存・復元時の記録対象と、部位選択の副作用を確認する。</summary>
internal static class HistoryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _checks;
    private static readonly Dictionary<string, Object> Assets = new Dictionary<string, Object>();
    private static readonly List<Object> Recorded = new List<Object>();

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _checks++;
    }
    private static object Get(object instance, string name) { return instance.GetType().GetField(name, Private).GetValue(instance); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetField(name, Private).SetValue(instance, value); }
    private static object Call(object instance, string name, params object[] args)
    { return instance.GetType().GetMethod(name, Private).Invoke(instance, args); }

    private sealed class Style : IStyle
    {
        public float flexGrow { get; set; }
        public StyleFontDefinition unityFontDefinition { get; set; }
        public StyleEnum<FontStyle> unityFontStyleAndWeight { get; set; }
        public StyleEnum<DisplayStyle> display { get; set; }
        public StyleColor backgroundColor { get; set; }
        public StyleBackground backgroundImage { get; set; }
        public StyleLength paddingLeft { get; set; }
        public StyleLength minHeight { get; set; }
    }

    private sealed class BadgeFeature : FeatureBase
    {
        public string Badge;
        public bool Done;
        public override string Id { get { return "body"; } }
        public override int Number { get { return 4; } }
        public override Color Accent { get { return new Color(); } }
        public override bool InTrial { get { return true; } }
        public override string NavBadge(VRCAvatarDescriptor avatar) { return Badge; }
        public override bool IsDone(VRCAvatarDescriptor avatar) { return Done; }
        public override VisualElement Build(FeatureContext ctx) { return new VisualElement(); }
    }

    private static void Finish(BackupService backup, string name)
    {
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(name);
        // Unity では操作の記録が Undo 履歴に積まれてから、遅れて CompletePending が呼ばれる。
        Undo.RecordsForTests.Add(name);
        backup.CompletePending();
    }

    private static VRCAvatarDescriptor MakeAvatar(string name)
    {
        var go = new GameObject { name = name };
        var root = new Transform { gameObject = go, name = "Root", localScale = new Vector3(1, 1, 1) };
        go.transform = root;
        go.ComponentsForTests = type => type == typeof(Transform) ? new object[] { root } : new object[0];
        return new VRCAvatarDescriptor { name = go.name, gameObject = go, transform = root };
    }

    /// <summary>起動時の片付けで、どこからも参照されない、起動前からある焼いた画像だけを消す。</summary>
    private static void SweepBakesChecks()
    {
        var oldDirectory = Environment.CurrentDirectory;
        var temp = Path.Combine(Path.GetTempPath(), "kaihennavi-sweep-" + Guid.NewGuid().ToString("N"));
        var oldFind = AssetDatabase.FindForTests;
        var oldDelete = AssetDatabase.DeleteForTests;
        var oldDependencies = AssetDatabase.DependenciesForTests;
        var oldImporter = AssetImporter.LoadForTests;
        var oldResources = Resources.FindForTests;
        try
        {
            var folder = WorkFolders.AvatarsRoot + "/A/Textures";
            Directory.CreateDirectory(Path.Combine(temp, folder));
            Environment.CurrentDirectory = temp;
            var unused = folder + "/Body_2nd.png";
            var used = folder + "/Body_2nd 1.png";
            var fresh = folder + "/Body_2nd 2.png";
            var foreign = folder + "/Photo.png";
            foreach (var path in new[] { unused, used, fresh, foreign }) File.WriteAllBytes(path, new byte[] { 1 });
            AssetDatabase.FindForTests = (filter, folders) => filter == "t:Texture2D" ? new[] { unused, used, fresh, foreign } :
                filter == "t:Material" ? new[] { "Assets/Mat.mat" } : new string[0];
            AssetDatabase.DependenciesForTests = roots => roots.Contains("Assets/Mat.mat") ? new[] { used } : new string[0];
            AssetImporter.LoadForTests = path => new AssetImporter
                { assetPath = path, userData = path == foreign ? "" : "kaihen-navi-color-source:0123" };
            Resources.FindForTests = type => new Object[0];
            var deleted = new List<string>();
            AssetDatabase.DeleteForTests = path => deleted.Add(path);
            var start = new DateTime(2000, 1, 1);
            var removed = ColorLayers.SweepUnusedBakes(start, path => path == fresh ? start : start.AddDays(-1));
            Check(removed == 1 && deleted.SequenceEqual(new[] { unused }),
                "Startup sweep deletes only unreferenced tool-baked images written before this editor session");
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            AssetDatabase.FindForTests = oldFind;
            AssetDatabase.DeleteForTests = oldDelete;
            AssetDatabase.DependenciesForTests = oldDependencies;
            AssetImporter.LoadForTests = oldImporter;
            Resources.FindForTests = oldResources;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    private static GameObject Child(GameObject parent, string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    /// <summary>JSON の入出力だけ差し替え、既存コンポーネントの永続復元データと適用を確認する。</summary>
    private static void ParticipantRestoreChecks()
    {
        var serialize = JsonUtility.SerializeForTests;
        var deserialize = JsonUtility.DeserializeForTests;
        var guidFor = AssetDatabase.GuidForTests;
        var pathFromGuid = AssetDatabase.PathFromGuidForTests;
        var loadAll = AssetDatabase.LoadAllForTests;
        var localId = AssetDatabase.LocalIdForTests;
        try
        {
            var snapshots = new Dictionary<string, object>();
            JsonUtility.SerializeForTests = value =>
            {
                var key = "snapshot-" + snapshots.Count;
                snapshots.Add(key, value);
                return key;
            };
            JsonUtility.DeserializeForTests = (json, type) => snapshots.ContainsKey(json) ? snapshots[json] : null;
            var go = new GameObject("RestoreAvatar");
            var avatar = go.AddComponent<VRCAvatarDescriptor>();
            var outfit = Child(go, "Outfit");
            var data = new MenuData();
            data.items.Add(new MenuItemData { label = "Outfit", parameter = "OutfitParameter",
                targetPath = HierarchyPath.Get(go.transform, outfit.transform), isDefault = true,
                hasOriginalActive = true, originalActive = true });
            var menuRoot = ToggleFeature.BuildMenu(avatar, data, "test");
            var menuItem = menuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true)
                .Single(i => i.transform != menuRoot.transform);
            var menu = new ToggleMenuBackup();
            var snapshot = menu.Capture(avatar);
            menuItem.isDefault = false;
            menuItem.Control.name = "Changed";
            menuItem.Control.parameter.name = "ChangedParameter";
            var marker = menuItem.transform.Find("KaihenNavi_OriginalVisibility");
            marker.gameObject.SetActive(false);
            Recorded.Clear();
            menu.Restore(avatar, snapshot, "restore");
            Check(menuItem.isDefault && menuItem.Control.name == "Outfit" &&
                menuItem.Control.parameter.name == "OutfitParameter", "Existing menu restores defaults and control data by item path");
            Check(Recorded.Contains(menuItem) && marker.gameObject.activeSelf &&
                ReferenceEquals(menuRoot.transform, go.transform.Find(ToggleFeature.MenuObjectName)),
                "Existing menu records Undo and restores original visibility without replacing its root");
            // 古いバックアップは項目のパスが無いのでパラメータで照合する。
            data.items[0].itemPath = null;
            var legacy = JsonUtility.ToJson(data);
            menuItem.isDefault = false;
            menu.Restore(avatar, legacy, "restore");
            Check(menuItem.isDefault, "Legacy menu data matches existing items by parameter");
            data.items[0].parameter = "NotFound";
            menuItem.isDefault = false;
            menu.Restore(avatar, legacy, "restore");
            Check(!menuItem.isDefault, "Unmatched menu items are not modified");
            menu.Restore(avatar, null, "restore");
            menu.Restore(avatar, "invalid", "restore");
            Check(!menuItem.isDefault, "Missing and invalid menu snapshots leave existing items alone");

            var body = Child(go, "Body");
            var hider = Child(outfit, SkinFeature.HiderObjectName).AddComponent<ModularAvatarShapeChanger>();
            var reference = new AvatarObjectReference();
            reference.Set(body);
            hider.Shapes = new List<ChangedShape> { new ChangedShape { Object = reference,
                ShapeName = "Shrink", ChangeType = ShapeChangeType.Set, Value = 73f } };
            var other = Child(outfit, "UserShapeChanger").AddComponent<ModularAvatarShapeChanger>();
            other.Shapes = new List<ChangedShape>();
            var skin = new SkinShapeBackup();
            snapshot = skin.Capture(avatar);
            hider.Shapes.Clear();
            Recorded.Clear();
            skin.Restore(avatar, snapshot, "restore");
            var restored = hider.Shapes.Single();
            Check(restored.ShapeName == "Shrink" && restored.ChangeType == ShapeChangeType.Set && restored.Value == 73f &&
                restored.Object.Get(hider) == body, "Existing skin changer restores a removed shape and its target");
            Check(Recorded.Contains(hider) && !Recorded.Contains(other), "Skin restore records only tool-managed changers");
            typeof(SkinFeature).GetMethod("RemoveShape", Static).Invoke(null, new object[] { outfit, body.AddComponent<SkinnedMeshRenderer>(), "Shrink", "test" });
            Check(hider.Shapes.Count == 0, "SkinFeature.RemoveShape removes an existing managed shape");
            skin.Restore(avatar, snapshot, "restore");
            Check(hider.Shapes.Count == 1 && hider.Shapes[0].Value == 73f, "Snapshot restores the setting removed by RemoveShape");
            hider.Shapes.Clear();
            var empty = skin.Capture(avatar);
            hider.Shapes.Add(restored);
            skin.Restore(avatar, empty, "restore");
            Check(hider.Shapes.Count == 0, "An empty saved Shapes list clears later edits");
            skin.Restore(avatar, null, "restore");
            skin.Restore(avatar, "invalid", "restore");
            skin.Restore(null, snapshot, "restore");
            Check(hider.Shapes.Count == 0, "Missing and invalid skin data do not change a changer");
            body.transform.SetParent(null, false);
            skin.Restore(avatar, snapshot, "restore");
            Check(hider.Shapes.Single().Object.Get(hider) == null, "Missing shape targets restore as unbound references");

            var a = new RuntimeAnimatorController { name = "A" };
            var b = new RuntimeAnimatorController { name = "B" };
            Assets["Assets/A.controller"] = a;
            AssetDatabase.GuidForTests = path => "controller-guid";
            AssetDatabase.PathFromGuidForTests = guid => guid == "controller-guid" ? "Assets/Moved.controller" : "";
            AssetDatabase.LoadAllForTests = path => path == "Assets/Moved.controller" ? new Object[] { b, a } : new Object[0];
            AssetDatabase.LocalIdForTests = obj => ReferenceEquals(obj, a) ? 42 : 43;
            var walk = Child(go, WalkFeature.WalkObjectName).AddComponent<ModularAvatarMergeAnimator>();
            walk.animator = a;
            walk.layerType = VRCAvatarDescriptor.AnimLayerType.Base;
            walk.mergeAnimatorMode = MergeAnimatorMode.Replace;
            walk.pathMode = MergeAnimatorPathMode.Absolute;
            walk.matchAvatarWriteDefaults = true;
            walk.deleteAttachedAnimator = true;
            walk.layerPriority = 7;
            var userWalk = Child(go, "UserWalk").AddComponent<ModularAvatarMergeAnimator>();
            userWalk.animator = b;
            var walkBackup = new WalkAnimatorBackup();
            snapshot = walkBackup.Capture(avatar);
            Check(((IList)snapshots[snapshot].GetType().GetField("entries").GetValue(snapshots[snapshot])).Count == 1,
                "Walk capture includes only the managed direct child");
            walk.animator = b;
            walk.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            walk.mergeAnimatorMode = MergeAnimatorMode.Append;
            walk.pathMode = MergeAnimatorPathMode.Relative;
            walk.matchAvatarWriteDefaults = false;
            walk.deleteAttachedAnimator = false;
            walk.layerPriority = 0;
            Recorded.Clear();
            walkBackup.Restore(avatar, snapshot, "restore");
            Check(walk.animator == a, "Existing walk restores controller A after B, using GUID and local ID even after an asset move");
            Check(walk.layerType == VRCAvatarDescriptor.AnimLayerType.Base && walk.mergeAnimatorMode == MergeAnimatorMode.Replace &&
                walk.pathMode == MergeAnimatorPathMode.Absolute && walk.matchAvatarWriteDefaults && walk.deleteAttachedAnimator &&
                walk.layerPriority == 7, "Walk restores Merge Animator settings");
            Check(Recorded.Contains(walk) && !Recorded.Contains(userWalk) && userWalk.animator == b,
                "Walk restore records Undo without touching user Merge Animators");
            walk.animator = null;
            empty = walkBackup.Capture(avatar);
            walk.animator = b;
            walkBackup.Restore(avatar, empty, "restore");
            Check(walk.animator == null, "A saved null walk controller clears a later assignment");
            walk.animator = b;
            walkBackup.Restore(avatar, null, "restore");
            walkBackup.Restore(avatar, "invalid", "restore");
            Check(walk.animator == b, "Missing and invalid walk data leave existing controllers alone");
            AssetDatabase.LoadAllForTests = path => new Object[0];
            walkBackup.Restore(avatar, snapshot, "restore");
            Check(walk.animator == null, "A missing saved controller does not retain the later controller");
            walk.transform.SetParent(null, false);
            walkBackup.Restore(avatar, snapshot, "restore");
            Check(walk.animator == null, "Missing walk objects are skipped without editing other objects");
            Assets.Remove("Assets/A.controller");
        }
        finally
        {
            JsonUtility.SerializeForTests = serialize;
            JsonUtility.DeserializeForTests = deserialize;
            AssetDatabase.GuidForTests = guidFor;
            AssetDatabase.PathFromGuidForTests = pathFromGuid;
            AssetDatabase.LoadAllForTests = loadAll;
            AssetDatabase.LocalIdForTests = localId;
        }
    }

    private static GameObject CopyHierarchy(GameObject source)
    {
        var copy = new GameObject(source.name);
        copy.SetActive(source.activeSelf);
        copy.transform.localPosition = source.transform.localPosition;
        copy.transform.localRotation = source.transform.localRotation;
        copy.transform.localScale = source.transform.localScale;
        foreach (Transform child in source.transform) CopyHierarchy(child.gameObject).transform.SetParent(copy.transform, false);
        return copy;
    }

    /// <summary>Prefab のファイル入出力だけ差し替え、削除の記録・挿し戻し・整理の順序を確認する。</summary>
    private static void RemovedObjectChecks()
    {
        var save = PrefabUtility.SaveForTests;
        var instantiate = PrefabUtility.InstantiateForTests;
        var unpack = PrefabUtility.UnpackForTests;
        var prefabRoot = PrefabUtility.RootForTests;
        var create = AssetDatabase.CreateForTests;
        var load = AssetDatabase.LoadForTests;
        var find = AssetDatabase.FindForTests;
        var pathFor = AssetDatabase.PathForTests;
        var delete = AssetDatabase.DeleteForTests;
        var created = Undo.CreatedForTests;
        var deserialize = JsonUtility.DeserializeForTests;
        var assets = new Dictionary<string, Object>();
        try
        {
            PrefabUtility.RootForTests = null;
            AssetDatabase.CreateForTests = (asset, path) => assets.Add(path, asset);
            AssetDatabase.LoadForTests = path => path != null && assets.ContainsKey(path) ? assets[path] : null;
            AssetDatabase.FindForTests = (filter, folders) => assets.Where(p => p.Value is BackupSnapshot).Select(p => p.Key).ToArray();
            AssetDatabase.PathForTests = asset => assets.FirstOrDefault(p => ReferenceEquals(p.Value, asset)).Key;
            var deleted = new List<string>();
            AssetDatabase.DeleteForTests = path => { deleted.Add(path); assets.Remove(path); };
            var savedSources = new List<GameObject>();
            PrefabUtility.SaveForTests = (source, path) =>
            {
                savedSources.Add(source);
                var prefab = CopyHierarchy(source);
                assets.Add(path, prefab);
                return prefab;
            };
            PrefabUtility.InstantiateForTests = asset => CopyHierarchy((GameObject)asset);
            var unpacked = new List<GameObject>();
            PrefabUtility.UnpackForTests = (instance, mode, interaction) =>
            {
                Check(mode == PrefabUnpackMode.OutermostRoot && interaction == InteractionMode.UserAction,
                    "Restored objects unpack one level with Undo support");
                unpacked.Add(instance);
            };
            var undoCreated = new List<Object>();
            Undo.CreatedForTests = obj => undoCreated.Add(obj);
            BackupService.InvalidateCache();
            var go = new GameObject("RemovedAvatar");
            var avatar = go.AddComponent<VRCAvatarDescriptor>();
            var parent = Child(go, "Clothes");
            Child(parent, "Before");
            var outfit = Child(parent, "Outfit");
            outfit.transform.localPosition = new Vector3(1, 2, 3);
            Child(outfit, "Mesh");
            Child(parent, "After");
            var backup = new BackupService();
            backup.BeginEdit(avatar, "outfit");
            backup.NoteRemoved(avatar, outfit);
            var pending = (BackupSnapshot)Get(backup, "_pending");
            var record = pending.removedObjects.Single();
            Check(record.parentPath == HierarchyPath.Get(go.transform, parent.transform) && record.siblingIndex == 1 &&
                record.objectName == "Outfit" && record.prefabPath.Contains("/Backups/Removed/"),
                "Removal records the original parent, sibling index, name and tool-owned prefab path");
            Check(undoCreated.Count == 0 && savedSources.Single() == outfit && parent.transform.GetChild(1) == outfit.transform,
                "Saving a removal neither reconnects the original nor creates Undo objects");
            Undo.DestroyObjectImmediate(outfit);
            Finish(backup, "Remove outfit");
            var snapshot = backup.List(avatar).First();
            var snapshotPath = AssetDatabase.GetAssetPath(snapshot);
            new BackupService().Restore(avatar, snapshot);
            var restored = parent.transform.GetChild(1).gameObject;
            Check(restored.name == "Outfit" && restored.transform.parent == parent.transform &&
                parent.transform.GetChild(2).name == "After" && restored.transform.Find("Mesh") != null,
                "Persistent restore recreates the removed hierarchy at its original parent and sibling index");
            Check(restored.transform.localPosition == new Vector3(1, 2, 3) && undoCreated.Contains(restored) && unpacked.Contains(restored),
                "Restored hierarchy exists before transforms apply and is registered for Undo");
            snapshot.hiddenFromHistory = false;
            new BackupService().Restore(avatar, snapshot);
            Check(parent.transform.childCount == 3 && undoCreated.Count == 1, "Restoring twice does not duplicate a present object");
            Undo.DestroyObjectImmediate(restored);
            parent.transform.SetParent(null, false);
            snapshot.hiddenFromHistory = false;
            new BackupService().Restore(avatar, snapshot);
            Check(undoCreated.Count == 1, "A missing parent skips the removed object");
            parent.transform.SetParent(go.transform, false);
            assets.Remove(record.prefabPath);
            snapshot.hiddenFromHistory = false;
            new BackupService().Restore(avatar, snapshot);
            Check(parent.transform.childCount == 2 && undoCreated.Count == 1, "A missing saved prefab is skipped");
            assets[record.prefabPath] = new GameObject("Saved outfit");
            snapshot.hiddenFromHistory = true;
            deleted.Clear();
            BackupService.DeleteStale(BackupService.SessionStart, path => path == record.prefabPath ? DateTime.MaxValue : DateTime.MinValue);
            Check(!deleted.Contains(snapshotPath) && !deleted.Contains(record.prefabPath),
                "A removed prefab written during this session retains its snapshot for Undo");
            BackupService.DeleteStale(BackupService.SessionStart, path => DateTime.MinValue);
            Check(deleted.Contains(snapshotPath) && deleted.Contains(record.prefabPath), "Deleting an old hidden snapshot also deletes its saved removed prefab");

            // Unity の JSON 本体は再現せず、旧データの既知フィールドだけを読み込む。
            JsonUtility.DeserializeForTests = (json, type) =>
            {
                var old = FlatJson.Parse(json);
                return new BackupSnapshot { avatarName = old["avatarName"] };
            };
            var legacy = JsonUtility.FromJson<BackupSnapshot>("{\"avatarName\":\"Legacy\"}");
            Check(legacy.avatarName == "Legacy" && legacy.removedObjects != null && legacy.removedObjects.Count == 0,
                "Old snapshot data without removedObjects retains an empty initialized list");
            legacy.removedObjects = null;
            legacy.avatarId = BackupService.AvatarId(avatar);
            legacy.createdTicks = DateTime.Now.Ticks;
            assets.Add(WorkFolders.Backups(go) + "/legacy.asset", legacy);
            BackupService.InvalidateCache();
            new BackupService().Restore(avatar, legacy);
            Check(undoCreated.Count == 1, "Legacy null removal lists are also safe during restore");
            JsonUtility.DeserializeForTests = deserialize;

            var cancel = new BackupService();
            cancel.BeginEdit(avatar, "outfit");
            cancel.NoteRemoved(avatar, parent);
            var cancelPath = ((BackupSnapshot)Get(cancel, "_pending")).removedObjects.Single().prefabPath;
            cancel.CancelPending();
            Check(!assets.ContainsKey(cancelPath), "Cancelling an unsaved snapshot removes its unused prefab copy");

            var merge = new BackupService();
            merge.BeginEdit(avatar, "outfit");
            merge.NoteRemoved(avatar, parent);
            Finish(merge, "Merge removal");
            var merged = merge.List(avatar).First();
            merge.BeginEdit(avatar, "outfit");
            merge.NoteRemoved(avatar, parent);
            var transferred = ((BackupSnapshot)Get(merge, "_pending")).removedObjects.Single().prefabPath;
            merge.CompletePending(Undo.GetCurrentGroup());
            Check(merged.removedObjects.Count == 2 && assets.ContainsKey(transferred),
                "Merging a step transfers removal records without deleting their saved prefabs");
            merge.EndSession();

            var sameStep = new BackupService();
            sameStep.BeginEdit(avatar, "outfit");
            var added = Child(go, "AddedThenRemoved");
            sameStep.NoteCreated(avatar, added);
            sameStep.NoteRemoved(avatar, added);
            Undo.DestroyObjectImmediate(added);
            Finish(sameStep, "Create and remove");
            new BackupService().Restore(avatar, sameStep.List(avatar).First());
            Check(go.transform.Find("AddedThenRemoved") == null, "An object created and removed within one snapshot is not resurrected");

            var failure = new BackupService();
            failure.BeginEdit(avatar, "outfit");
            PrefabUtility.SaveForTests = (source, path) => null;
            var threw = false;
            try { failure.NoteRemoved(avatar, parent); } catch (InvalidOperationException) { threw = true; }
            Check(threw && ((BackupSnapshot)Get(failure, "_pending")).removedObjects.Count == 0 && parent.transform.parent == go.transform,
                "A failed prefab save aborts before a removal can destroy the original");
            failure.CancelPending();

            var layered = new BackupService();
            layered.BeginEdit(avatar, "outfit");
            var later = Child(go, "LaterOutfit");
            layered.NoteCreated(avatar, later);
            Finish(layered, "Add later outfit");
            var beforeCreation = layered.List(avatar).First();
            layered.EndSession();
            layered.BeginEdit(avatar, "outfit");
            PrefabUtility.SaveForTests = (source, path) =>
            {
                var prefab = CopyHierarchy(source);
                assets.Add(path, prefab);
                return prefab;
            };
            layered.NoteRemoved(avatar, later);
            Undo.DestroyObjectImmediate(later);
            Finish(layered, "Remove later outfit");
            new BackupService().Restore(avatar, beforeCreation);
            Check(go.transform.Find("LaterOutfit") == null,
                "Restoring across creation and later deletion reverses each snapshot in order");
        }
        finally
        {
            PrefabUtility.SaveForTests = save;
            PrefabUtility.InstantiateForTests = instantiate;
            PrefabUtility.UnpackForTests = unpack;
            PrefabUtility.RootForTests = prefabRoot;
            AssetDatabase.CreateForTests = create;
            AssetDatabase.LoadForTests = load;
            AssetDatabase.FindForTests = find;
            AssetDatabase.PathForTests = pathFor;
            AssetDatabase.DeleteForTests = delete;
            Undo.CreatedForTests = created;
            JsonUtility.DeserializeForTests = deserialize;
            BackupService.InvalidateCache();
        }
    }

    public static void Main()
    {
        VisualElement.StyleForTests = () => new Style();
        Vector3.one = new Vector3(1, 1, 1);
        ScriptableObject.CreateForTests = type => (ScriptableObject)Activator.CreateInstance(type, true);
        AssetDatabase.CreateForTests = (asset, path) => Assets.Add(path, asset);
        AssetDatabase.LoadForTests = path => Assets.ContainsKey(path) ? Assets[path] :
            path != null && File.Exists(path) && path.EndsWith(".json") ? new TextAsset { text = File.ReadAllText(path) } : null;
        AssetDatabase.FindForTests = (filter, folders) => Assets.Where(p => p.Value is BackupSnapshot).Select(p => p.Key).ToArray();
        AssetDatabase.PathFromGuidForTests = path => path;
        AssetDatabase.PathForTests = asset => Assets.FirstOrDefault(p => p.Value == asset).Key;
        var deletions = 0;
        AssetDatabase.DeleteForTests = path => deletions++;
        Undo.RecordForTests = target => Recorded.Add(target);
        EditorApplication.timeSinceStartup = 3600;
        GlobalObjectId.IdForTests = o => "GlobalObjectId_V1-2-0123456789abcdef0123456789abcdef-" + o.name + "-0";
        var participants = (IList)typeof(BackupService).GetField("Participants", Static).GetValue(null);
        Check(participants.Cast<IBackupParticipant>().Any(p => p is SkinShapeBackup) &&
            participants.Cast<IBackupParticipant>().Any(p => p is WalkAnimatorBackup), "Skin and walk participants are registered");
        // 履歴自身のテストでは参加機能を外し、末尾で各参加機能の復元を別に確認する。
        ((IList)typeof(BackupService).GetField("Participants", Static).GetValue(null)).Clear();
        var go = new GameObject { name = "テスト用アバター" };
        var root = new Transform { gameObject = go, name = "Root", localScale = new Vector3(1, 1, 1) };
        go.transform = root;
        go.ComponentsForTests = type => type == typeof(Transform) ? new object[] { root } : new object[0];
        var avatar = new VRCAvatarDescriptor { name = go.name, gameObject = go, transform = root };
        var backup = new BackupService();
        var englishName = FlatJson.Parse(File.ReadAllText(PackagePaths.Localization + "/en.json"))["undo.height"];
        Check(L10n.UndoKey(englishName) == "undo.height", "English Undo name must resolve to its translation key");
        backup.BeginEdit(avatar, "body", "body:height");
        Finish(backup, englishName);
        var first = backup.List(avatar).Single();
        Check(first.operationKey == "undo.height" && first.operationName == englishName, "Undo name and key are preserved");
        Check(first.featureId == "body", "The feature used when editing classifies the step");
        Check(Recorded.Contains(first), "Step visibility must participate in Undo");
        root.localScale = new Vector3(1.2f, 1.2f, 1.2f);
        backup.BeginEdit(avatar, "body");
        root.localScale = new Vector3(1.4f, 1.4f, 1.4f);
        Finish(backup, L10n.Tr("undo.body"));
        var second = backup.List(avatar)[0];
        Check(second.SessionTicks == first.SessionTicks && backup.Count(avatar) == 1, "Repeated edits belong to one task");
        Check(second.createdTicks > first.createdTicks, "Fast edits have a stable order");
        Check(first.transforms[0].localScale.x == 1f && second.transforms[0].localScale.x == 1.2f,
            "A fine step stores its own pre-edit state, not the initial session or post-edit state");
        backup.EndSession();
        backup.BeginEdit(avatar, "outfit");
        Finish(backup, L10n.Tr("undo.outfit"));
        Check(backup.Count(avatar) == 2, "Changing feature starts another task");
        backup.EndSession();
        backup.BeginEdit(avatar, "body");
        Finish(backup, L10n.Tr("undo.body"));
        Check(backup.Count(avatar) == 3, "Returning to the same feature starts a new task");
        backup.EndSession();
        Check(new BackupService().List(avatar).Count == 4, "Fine steps survive a service restart");
        var legacy = new BackupSnapshot { avatarId = BackupService.AvatarId(avatar), createdTicks = 1, featureId = "color" };
        var legacyPath = WorkFolders.AvatarsRoot + "/Legacy/Backups/legacy.asset";
        Assets.Add(legacyPath, legacy);
        // Unity ではインポートの通知（BackupAssetWatcher）で読み込みの控えが更新される
        BackupService.OnAssetsChanged(new[] { legacyPath }, new string[0], new string[0], new string[0]);
        Check(legacy.SessionTicks == legacy.createdTicks && backup.List(avatar).Contains(legacy), "Legacy backups are shown as one-step tasks");
        Recorded.Clear();
        backup.Restore(avatar, second);
        Check(!first.hiddenFromHistory && second.hiddenFromHistory && backup.List(avatar).Count == 2, "Restore hides the selected step and all later steps");
        Check(Recorded.OfType<BackupSnapshot>().Count() == 3, "Restoration records every hidden step for Ctrl+Z");
        Check(deletions == 0 && Assets.Count == 5, "Restoration must preserve assets referenced by Undo");
        foreach (var step in Recorded.OfType<BackupSnapshot>()) step.hiddenFromHistory = false;
        backup.OnUndoRedo();
        Check(backup.List(avatar).Count == 5, "Undo of a restoration exposes the same saved steps");

        UndoHelper.Separate();
        backup.BeginEdit(avatar, "body", "drag");
        UndoHelper.Begin("drag", L10n.Tr("undo.body"));
        UndoHelper.End();
        backup.CompletePending();
        var beforeMerge = Assets.Count;
        backup.BeginEdit(avatar, "body", "drag");
        UndoHelper.Begin("drag", L10n.Tr("undo.body"));
        UndoHelper.End();
        backup.CompletePending();
        Check(Assets.Count == beforeMerge, "A merged slider gesture must not add a backup for every frame");
        backup.EndSession();
        for (var i = 0; i < BackupService.MaxBackupsPerAvatar + 2; i++)
        {
            backup.BeginEdit(avatar, "body");
            Finish(backup, L10n.Tr("undo.body"));
            backup.EndSession();
        }
        Check(backup.Count(avatar) == BackupService.MaxBackupsPerAvatar, "The limit counts tasks, not fine steps");
        Check(deletions == 0, "Pruning must not delete Undo targets");

        var window = new KaihenNaviWindow();
        Set(window, "_avatar", avatar);
        Set(window, "_features", FeatureCatalog.Create());
        var nativeUndoCalls = 0;
        Set(window, "_backup", backup);
        Undo.PerformUndoForTests = () => { nativeUndoCalls++; Call(window, "OnUndoRedo"); };
        Call(window, "RestoreLatest");
        Check(nativeUndoCalls == 1, "The arrow performs exactly one native Undo");
        var restartedWindow = new KaihenNaviWindow();
        Set(restartedWindow, "_avatar", avatar);
        Set(restartedWindow, "_features", FeatureCatalog.Create());
        var beforeFallback = backup.List(avatar)[0];
        Call(restartedWindow, "RestoreLatest");
        Check(nativeUndoCalls == 1 && beforeFallback.hiddenFromHistory, "After restart the arrow restores exactly the latest backup");
        Undo.PerformUndoForTests = () => { nativeUndoCalls++; Call(restartedWindow, "OnUndoRedo"); };
        Call(restartedWindow, "RestoreLatest");
        Check(nativeUndoCalls == 2, "The next arrow can undo the restoration itself, including a backup restored after restart");

        // 上に別の操作（カメラの移動など）が積まれたら、Unity の Undo ではなくバックアップで戻す。
        var records = Undo.RecordsForTests;
        var liveBackup = new BackupService();
        var liveWindow = new KaihenNaviWindow();
        Set(liveWindow, "_avatar", avatar);
        Set(liveWindow, "_features", FeatureCatalog.Create());
        Set(liveWindow, "_backup", liveBackup);
        Undo.PerformUndoForTests = () => { nativeUndoCalls++; records.RemoveAt(records.Count - 1); Call(liveWindow, "OnUndoRedo"); };
        liveBackup.BeginEdit(avatar, "color");
        Finish(liveBackup, L10n.Tr("undo.body"));
        Check(liveBackup.HasLiveUndo(avatar), "A step just recorded by this window is the top Undo step");
        // 自動保存（Prefab への Apply）が同じグループにまとまれば、積み方が変わっても Unity の Undo を使う。
        var liveGroup = liveBackup.LiveGroup(avatar);
        Check(liveGroup == Undo.GetCurrentGroup(), "The live step remembers its Undo group");
        var beforeSave = BackupService.UndoState();
        records.Add("Apply Prefab");
        Check(!liveBackup.HasLiveUndo(avatar), "A save changes the Undo fingerprint");
        liveBackup.KeepLive(avatar, liveGroup + 1, beforeSave);
        Check(!liveBackup.HasLiveUndo(avatar), "A save merged into another group does not keep the step live");
        liveBackup.KeepLive(avatar, liveGroup, beforeSave);
        Check(liveBackup.HasLiveUndo(avatar), "A save merged into the same group keeps the step live");
        records.Add("Move Main Camera");
        Check(!liveBackup.HasLiveUndo(avatar), "Another Undo step on top must not be undone by the arrow");
        var colorStep = liveBackup.List(avatar)[0];
        var callsBefore = nativeUndoCalls;
        Call(liveWindow, "RestoreLatest");
        Check(nativeUndoCalls == callsBefore && colorStep.hiddenFromHistory, "With a foreign step on top the arrow restores the backup instead");

        // 別のアバターの復元が上にあるとき、このアバターの「元に戻す」で取り消さない。
        var other = MakeAvatar("別のアバター");
        Check(BackupService.AvatarId(other) != BackupService.AvatarId(avatar), "Test avatars have distinct identifiers");
        liveBackup.EndSession();
        liveBackup.BeginEdit(other, "body");
        Finish(liveBackup, L10n.Tr("undo.body"));
        liveBackup.EndSession();
        liveBackup.BeginEdit(avatar, "body");
        Finish(liveBackup, L10n.Tr("undo.body"));
        liveBackup.EndSession();
        liveBackup.Restore(avatar, liveBackup.List(avatar)[0]);
        records.Add(L10n.Tr("undo.restore"));
        Check(!liveBackup.HasLiveUndo(other), "A restoration of another avatar is not this avatar's Undo step");
        var otherStep = liveBackup.List(other)[0];
        Set(liveWindow, "_avatar", other);
        callsBefore = nativeUndoCalls;
        Call(liveWindow, "RestoreLatest");
        Check(nativeUndoCalls == callsBefore && otherStep.hiddenFromHistory, "The other avatar falls back to its own latest backup");
        Set(liveWindow, "_avatar", avatar);

        // 失敗して取り消した操作は履歴に残さない。
        var assetsBeforeCancel = Assets.Count;
        liveBackup.EndSession();
        liveBackup.BeginEdit(avatar, "outfit");
        liveBackup.CancelPending();
        liveBackup.CompletePending();
        Check(Assets.Count == assetsBeforeCancel, "A cancelled operation leaves no history entry");

        // 一覧は読み込みを控え、バックアップのファイルが増えた・消えたときだけ探し直す。
        var finds = 0;
        var find = AssetDatabase.FindForTests;
        AssetDatabase.FindForTests = (filter, folders) => { finds++; return find(filter, folders); };
        BackupService.InvalidateCache();
        liveBackup.List(avatar);
        liveBackup.List(avatar, true);
        liveBackup.Count(avatar);
        Check(finds == 1, "Listing reuses the loaded backups");
        var none = new string[0];
        var knownPath = AssetDatabase.GetAssetPath(liveBackup.List(avatar)[0]);
        BackupService.OnAssetsChanged(new[] { WorkFolders.AvatarsRoot + "/A/Materials/Hair.mat", knownPath }, none, none, none);
        liveBackup.List(avatar);
        Check(finds == 1, "Saving materials or a loaded backup does not reload the list");
        BackupService.OnAssetsChanged(none, new[] { knownPath }, none, none);
        liveBackup.List(avatar);
        Check(finds == 2, "Deleting a backup reloads the list");
        liveBackup.EndSession();
        liveBackup.BeginEdit(avatar, "body");
        Finish(liveBackup, L10n.Tr("undo.body"));
        Check(finds == 2 && liveBackup.List(avatar)[0].createdTicks > first.createdTicks, "A new backup joins the list without reloading");

        // 自分用アバターへのつなぎ替えで ID を付け替えるとき、隠れたバックアップも含めて Undo に記録する。
        var withHidden = liveBackup.List(avatar, true);
        Check(withHidden.Any(s => s.hiddenFromHistory), "The test has hidden backups");
        Recorded.Clear();
        liveBackup.Rekey(avatar, withHidden);
        Check(withHidden.All(Recorded.Contains), "Re-keying is part of Ctrl+Z, hidden backups included");

        // 起動前から変わっていない、履歴に出ないバックアップだけを消す。
        for (var i = 0; i < BackupService.MaxBackupsPerAvatar + 3; i++)
        {
            Assets.Add(WorkFolders.AvatarsRoot + "/Old/Backups/old" + i + ".asset",
                new BackupSnapshot { avatarId = "old-avatar", avatarName = "old", createdTicks = 100 + i });
        }
        BackupService.InvalidateCache();
        var deletedPaths = new List<string>();
        AssetDatabase.DeleteForTests = path => deletedPaths.Add(path);
        Check(BackupService.DeleteStale(BackupService.SessionStart, path => DateTime.MaxValue) == 0 && deletedPaths.Count == 0,
            "Backups written in this editor session are never deleted");
        var hiddenPaths = Assets.Where(p => p.Value is BackupSnapshot && ((BackupSnapshot)p.Value).hiddenFromHistory).Select(p => p.Key).ToList();
        var removed = BackupService.DeleteStale(BackupService.SessionStart, path => DateTime.MinValue);
        Check(removed == deletedPaths.Count && hiddenPaths.All(deletedPaths.Contains), "Hidden backups from earlier sessions are deleted");
        var oldDeleted = deletedPaths.Where(p => p.Contains("/Old/")).ToList();
        Check(oldDeleted.Count == 3 && oldDeleted.All(p => ((BackupSnapshot)Assets[p]).createdTicks < 103),
            "Only the oldest tasks beyond the limit are deleted");
        Check(deletedPaths.All(p => ((BackupSnapshot)Assets[p]).hiddenFromHistory || p.Contains("/Old/")),
            "Visible backups within the limit are kept");
        // 前の起動で保存前のシーンで取ったものは、もう照合できないので消す。この起動中のものは残す
        var unsavedMarkerId = "GlobalObjectId_V1-2-00000000000000000000000000000000-99-0";
        Assets.Add(WorkFolders.AvatarsRoot + "/Unsaved/Backups/earlier.asset", new BackupSnapshot
            { avatarId = unsavedMarkerId, avatarName = "unsaved", createdTicks = BackupService.SessionStart.Ticks - 1 });
        Assets.Add(WorkFolders.AvatarsRoot + "/Unsaved/Backups/current.asset", new BackupSnapshot
            { avatarId = unsavedMarkerId, avatarName = "unsaved", sceneHandle = 3, createdTicks = DateTime.Now.Ticks });
        BackupService.InvalidateCache();
        deletedPaths.Clear();
        BackupService.DeleteStale(BackupService.SessionStart, path => DateTime.MinValue);
        Check(deletedPaths.Contains(WorkFolders.AvatarsRoot + "/Unsaved/Backups/earlier.asset") &&
              !deletedPaths.Contains(WorkFolders.AvatarsRoot + "/Unsaved/Backups/current.asset"),
            "Unsaved-scene backups from an earlier editor session are deleted, this session's are kept");
        Assets.Remove(WorkFolders.AvatarsRoot + "/Unsaved/Backups/earlier.asset");
        Assets.Remove(WorkFolders.AvatarsRoot + "/Unsaved/Backups/current.asset");
        BackupService.InvalidateCache();
        SweepBakesChecks();

        // 保存前のシーンでは、この起動中に同じシーンで取った同名のバックアップだけを同じアバターとみなす。
        var sameAvatar = typeof(BackupService).GetMethod("SameAvatar", Static);
        var unsavedId = "GlobalObjectId_V1-2-00000000000000000000000000000000-1234-0";
        avatar.gameObject.scene = new UnityEngine.SceneManagement.Scene { handle = 7 };
        var unsavedStep = new BackupSnapshot { avatarId = unsavedId, avatarName = avatar.name, sceneHandle = 7, createdTicks = DateTime.Now.Ticks };
        Func<BackupSnapshot, bool> matches = snap => (bool)sameAvatar.Invoke(null, new object[] { snap, BackupService.AvatarId(avatar), avatar });
        Check(matches(unsavedStep), "An unsaved-scene backup from this session and scene matches by name");
        unsavedStep.sceneHandle = 8;
        Check(!matches(unsavedStep), "Another scene's avatar with the same name does not match");
        unsavedStep.sceneHandle = 7;
        unsavedStep.createdTicks = BackupService.SessionStart.Ticks - 1;
        Check(!matches(unsavedStep), "A backup from an earlier editor session does not match by name");
        Check(!matches(new BackupSnapshot { avatarId = unsavedId, avatarName = avatar.name, createdTicks = DateTime.Now.Ticks }),
            "Old backups without a scene handle do not match by name alone");
        Check(matches(legacy), "Backups with the same identifier still match");
        avatar.gameObject.scene = new UnityEngine.SceneManagement.Scene();
        Check(((string)Call(window, "OperationTitle", new BackupSnapshot { featureId = "body", operationName = "Add Component" })) ==
            L10n.Tr("history.operation", L10n.Tr("feature.body.nav")), "Unknown English steps use the navigation name");

        var feature = new BadgeFeature { Badge = "2", Done = true };
        var badge = new Label();
        var done = new VisualElement();
        var navItem = new VisualElement();
        ((Dictionary<FeatureBase, Label>)Get(window, "_navigationBadges")).Add(feature, badge);
        ((Dictionary<FeatureBase, VisualElement>)Get(window, "_navigationDone")).Add(feature, done);
        ((Dictionary<FeatureBase, VisualElement>)Get(window, "_navigationItems")).Add(feature, navItem);
        Call(window, "RefreshNavigationStatus");
        Check(badge.text == "2" && done.style.display.value == DisplayStyle.Flex, "Navigation badges and done indicators update in place");
        feature.Badge = null;
        feature.Done = false;
        Call(window, "RefreshNavigationStatus");
        Check(badge.style.display.value == DisplayStyle.None && done.style.display.value == DisplayStyle.None &&
            ReferenceEquals(navItem, ((Dictionary<FeatureBase, VisualElement>)Get(window, "_navigationItems"))[feature]),
            "Removing a badge must preserve the navigation item");

        var body = new BodyFeature();
        Set(body, "_avatar", avatar);
        var canvas = new VisualElement();
        Set(body, "_canvas", canvas);
        var repaints = 0;
        canvas.RepaintForTests = () => repaints++;
        typeof(BodyFeature).GetField("_selected", Static).SetValue(null, 1);
        var rebuilds = 0;
        var ctx = new FeatureContext { Avatar = avatar, Backup = new BackupService(), FeatureId = "body", Rebuild = () => rebuilds++ };
        Call(body, "BuildPartList", ctx);
        Call(body, "BuildControls", ctx);
        var length = (Slider)Get(body, "_length");
        var width = (Slider)Get(body, "_width");
        var heading = Get(body, "_partLabel");
        var buttons = (List<Button>)Get(body, "_partButtons");
        Recorded.Clear();
        length.SetValueWithoutNotify(60);
        buttons[0].clicked();
        Check(length.lowValue == 80 && length.highValue == 120, "Whole-body selection updates slider bounds");
        Check(Recorded.Count == 0 && rebuilds == 0, "Selecting a part must not modify the avatar or rebuild the window, even when a range clamps");
        buttons[2].clicked();
        Check(length.lowValue == 50 && length.highValue == 150 && ((VisualElement)Get(body, "_widthRow")).style.display.value == DisplayStyle.Flex,
            "Non-uniform parts expose the width slider");
        Check(ReferenceEquals(length, Get(body, "_length")) && ReferenceEquals(width, Get(body, "_width")) && ReferenceEquals(heading, Get(body, "_partLabel")),
            "Part selection preserves input and heading elements");
        var points = (Dictionary<HumanBodyBones, Vector2>)Get(body, "_points");
        points[HumanBodyBones.Head] = new Vector2();
        Call(body, "OnPointerDown", ctx, new PointerDownEvent());
        Check((int)typeof(BodyFeature).GetField("_selected", Static).GetValue(null) == 1 && repaints == 3 && rebuilds == 0 && Recorded.Count == 0,
            "Selecting a silhouette point repaints locally without editing or rebuilding");
        ParticipantRestoreChecks();
        RemovedObjectChecks();
        Console.WriteLine("History and body checks passed (" + _checks + "). Native Undo, Prefab saving and rendering require Unity.");
    }
}

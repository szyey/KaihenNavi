#if KAIHENNAVI_MA
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using nadena.dev.modular_avatar.core;

namespace Jeni.KaihenNavi
{
    /// <summary>このアバターの歩き方・座り方・エモートの上書きを戻す。</summary>
    internal sealed class WalkAnimatorBackup : IBackupParticipant
    {
        [System.Serializable]
        private sealed class Entry
        {
            public string path;
            public string controllerGuid;
            public long controllerId;
            public VRCAvatarDescriptor.AnimLayerType layerType;
            public MergeAnimatorMode mode;
            public MergeAnimatorPathMode pathMode;
            public bool matchWriteDefaults;
            public bool deleteAttachedAnimator;
            public int layerPriority;
        }

        [System.Serializable]
        private sealed class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        private static bool IsManaged(Transform root, Transform t)
        {
            return t.parent == root && (t.name == WalkFeature.WalkObjectName ||
                t.name == "KaihenNavi_Sit" || t.name == "KaihenNavi_Action");
        }

        public string Key { get { return "walk-animators"; } }

        public string Capture(VRCAvatarDescriptor avatar)
        {
            var data = new Data();
            if (avatar == null) return JsonUtility.ToJson(data);
            foreach (var merge in avatar.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (!IsManaged(avatar.transform, merge.transform)) continue;
                string guid = null;
                long id = 0;
                if (merge.animator != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(merge.animator, out guid, out id);
                data.entries.Add(new Entry
                {
                    path = HierarchyPath.Get(avatar.transform, merge.transform),
                    controllerGuid = guid,
                    controllerId = id,
                    layerType = merge.layerType,
                    mode = merge.mergeAnimatorMode,
                    pathMode = merge.pathMode,
                    matchWriteDefaults = merge.matchAvatarWriteDefaults,
                    deleteAttachedAnimator = merge.deleteAttachedAnimator,
                    layerPriority = merge.layerPriority,
                });
            }
            return JsonUtility.ToJson(data);
        }

        public void Restore(VRCAvatarDescriptor avatar, string json, string undoName)
        {
            if (avatar == null || string.IsNullOrEmpty(json)) return;
            var data = JsonUtility.FromJson<Data>(json);
            if (data == null || data.entries == null) return;
            foreach (var entry in data.entries)
            {
                if (entry == null) continue;
                var t = HierarchyPath.Find(avatar.transform, entry.path);
                if (t == null || !IsManaged(avatar.transform, t)) continue;
                RuntimeAnimatorController controller = null;
                if (!string.IsNullOrEmpty(entry.controllerGuid))
                {
                    var path = AssetDatabase.GUIDToAssetPath(entry.controllerGuid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                        {
                            string guid;
                            long id;
                            if (asset is RuntimeAnimatorController &&
                                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out id) && id == entry.controllerId)
                            {
                                controller = (RuntimeAnimatorController)asset;
                                break;
                            }
                        }
                    }
                }
                var merge = t.GetComponent<ModularAvatarMergeAnimator>();
                if (merge == null) merge = Undo.AddComponent<ModularAvatarMergeAnimator>(t.gameObject);
                else Undo.RecordObject(merge, undoName);
                merge.animator = controller;
                merge.layerType = entry.layerType;
                merge.mergeAnimatorMode = entry.mode;
                merge.pathMode = entry.pathMode;
                merge.matchAvatarWriteDefaults = entry.matchWriteDefaults;
                merge.deleteAttachedAnimator = entry.deleteAttachedAnimator;
                merge.layerPriority = entry.layerPriority;
                PrefabUtility.RecordPrefabInstancePropertyModifications(merge);
                EditorUtility.SetDirty(merge);
            }
        }
    }
}
#endif

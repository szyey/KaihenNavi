#if KAIHENNAVI_MA
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using nadena.dev.modular_avatar.core;

namespace Jeni.KaihenNavi
{
    /// <summary>服ごとの肌隠しを、既存の Shape Changer の編集も含めて戻す。</summary>
    internal sealed class SkinShapeBackup : IBackupParticipant
    {
        [System.Serializable]
        private sealed class Shape
        {
            public string targetPath;
            public string name;
            public ShapeChangeType changeType;
            public float value;
        }

        [System.Serializable]
        private sealed class Entry
        {
            public string path;
            public List<Shape> shapes = new List<Shape>();
        }

        [System.Serializable]
        private sealed class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        public string Key { get { return "skin-shapes"; } }

        public string Capture(VRCAvatarDescriptor avatar)
        {
            var data = new Data();
            if (avatar == null) return JsonUtility.ToJson(data);
            foreach (Transform outfit in avatar.transform)
            {
                var t = outfit.Find(SkinFeature.HiderObjectName);
                var changer = t == null ? null : t.GetComponent<ModularAvatarShapeChanger>();
                if (changer == null) continue;
                var entry = new Entry { path = HierarchyPath.Get(avatar.transform, t) };
                if (changer.Shapes != null)
                {
                    foreach (var shape in changer.Shapes)
                    {
                        var target = shape.Object == null ? null : shape.Object.Get(changer);
                        entry.shapes.Add(new Shape
                        {
                            targetPath = target == null ? null : HierarchyPath.Get(avatar.transform, target.transform),
                            name = shape.ShapeName,
                            changeType = shape.ChangeType,
                            value = shape.Value,
                        });
                    }
                }
                data.entries.Add(entry);
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
                if (t == null || t.name != SkinFeature.HiderObjectName || t.parent == null ||
                    t.parent.parent != avatar.transform) continue;
                var changer = t.GetComponent<ModularAvatarShapeChanger>();
                if (changer == null) changer = Undo.AddComponent<ModularAvatarShapeChanger>(t.gameObject);
                else Undo.RecordObject(changer, undoName);
                var shapes = new List<ChangedShape>();
                foreach (var shape in entry.shapes ?? new List<Shape>())
                {
                    if (shape == null) continue;
                    var target = HierarchyPath.Find(avatar.transform, shape.targetPath);
                    var reference = new AvatarObjectReference();
                    if (target != null) reference.Set(target.gameObject);
                    shapes.Add(new ChangedShape { Object = reference, ShapeName = shape.name,
                        ChangeType = shape.changeType, Value = shape.value });
                }
                changer.Shapes = shapes;
                PrefabUtility.RecordPrefabInstancePropertyModifications(changer);
                EditorUtility.SetDirty(changer);
            }
        }
    }
}
#endif

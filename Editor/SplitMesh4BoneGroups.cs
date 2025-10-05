#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace jp.illusive_isc
{
    public class SplitMesh4BoneGroups : EditorWindow
    {
        [System.Serializable]
        public class BoneGroup
        {
            public string name;
            public List<Transform> bones = new List<Transform>();
        }

        private SkinnedMeshRenderer sourceSMR;
        private List<BoneGroup> groups = new List<BoneGroup>();

        [MenuItem("Tools/splitMesh4BoneGroups")]
        public static void OpenWindow()
        {
            GetWindow<SplitMesh4BoneGroups>("Split Vertices Multi Bone Groups");
        }

        private Vector2 scroll;

        private void OnGUI()
        {
            GUILayout.Label("頂点切り出し（複数ボーングループ対応）", EditorStyles.boldLabel);
            sourceSMR = (SkinnedMeshRenderer)
                EditorGUILayout.ObjectField("元 SMR", sourceSMR, typeof(SkinnedMeshRenderer), true);

            if (GUILayout.Button("グループ追加"))
            {
                groups.Add(new BoneGroup { name = "" });
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                EditorGUILayout.BeginVertical("box");
                g.name = EditorGUILayout.TextField("グループ名", g.name);
                int removeIndex = -1;
                for (int j = 0; j < g.bones.Count; j++)
                {
                    EditorGUILayout.BeginHorizontal();
                    g.bones[j] = (Transform)
                        EditorGUILayout.ObjectField(
                            $"ボーン {j}",
                            g.bones[j],
                            typeof(Transform),
                            true
                        );
                    if (GUILayout.Button("×", GUILayout.Width(20)))
                        removeIndex = j;
                    EditorGUILayout.EndHorizontal();
                }
                if (removeIndex >= 0)
                    g.bones.RemoveAt(removeIndex);

                if (GUILayout.Button("ボーン追加"))
                    g.bones.Add(null);
                if (GUILayout.Button("削除"))
                {
                    groups.RemoveAt(i);
                    i--;
                    continue;
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("切り出す: 全グループ生成"))
            {
                if (sourceSMR == null)
                {
                    EditorUtility.DisplayDialog("Error", "元 SMR を指定してください", "OK");
                    return;
                }
                SplitAllGroups("ぬいぐるみ", sourceSMR, groups);
            }
        }

        public GameObject SplitAllGroups(
            string containerName = "",
            SkinnedMeshRenderer smr = null,
            List<BoneGroup> groupsToProcess = null,
            Transform containerParent = null
        )
        {
            SkinnedMeshRenderer useSMR = smr != null ? smr : sourceSMR;
            List<BoneGroup> useGroups = groupsToProcess != null ? groupsToProcess : groups;

            if (useSMR == null)
            {
                Debug.LogError("SplitAllGroups: SkinnedMeshRenderer is null.");
                return null;
            }

            Mesh originalMesh = useSMR.sharedMesh;
            if (originalMesh == null)
            {
                EditorUtility.DisplayDialog("Error", "元メッシュがありません", "OK");
                return null;
            }

            BoneWeight[] weights = originalMesh.boneWeights;
            Transform[] bones = useSMR.bones;

            // 対象ボーン集合を構築（上位階層は作成しない）
            HashSet<Transform> unionBones = new HashSet<Transform>();
            foreach (var group in useGroups)
            {
                if (group == null || group.bones == null)
                    continue;
                foreach (var b in group.bones)
                {
                    if (b == null)
                        continue;
                    foreach (var c in GetBoneAndChildren(b))
                        unionBones.Add(c);
                }
            }

            if (unionBones.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "対象ボーンが指定されていません", "OK");
                return null;
            }

            // ボーンの階層順にソート
            List<Transform> unionList = unionBones.ToList();
            Dictionary<Transform, int> depthMap = new Dictionary<Transform, int>();
            foreach (var b in unionList)
                depthMap[b] = GetDepthFromRoot(b, useSMR.transform.root);
            unionList = unionList.OrderBy(d => depthMap[d]).ToList();

            // 共有ボーンコピーを作成
            GameObject container = new GameObject(containerName == "" ? (useSMR.name + "_Split") : containerName);
            if (containerParent != null)
                container.transform.parent = containerParent;

            Dictionary<Transform, Transform> sharedBoneMap = new Dictionary<Transform, Transform>();
            foreach (var bone in unionList)
            {
                Transform parentCopy =
                    (bone.parent != null && sharedBoneMap.ContainsKey(bone.parent))
                        ? sharedBoneMap[bone.parent]
                        : container.transform;
                GameObject copy = new GameObject(bone.name);
                copy.transform.parent = parentCopy;
                copy.transform.localPosition = parentCopy == container.transform ? Vector3.zero : bone.localPosition;
                copy.transform.localRotation = bone.localRotation;
                copy.transform.localScale = bone.localScale;
                sharedBoneMap[bone] = copy.transform;
            }

            List<Transform> sharedBonesList = unionList.Select(b => sharedBoneMap[b]).ToList();

            // bindposes
            Matrix4x4[] originalBindposes = originalMesh.bindposes != null && originalMesh.bindposes.Length == bones.Length ? originalMesh.bindposes : null;
            List<Matrix4x4> sharedBindposes = new List<Matrix4x4>();
            foreach (var b in unionList)
            {
                int oldIndex = System.Array.IndexOf(bones, b);
                if (oldIndex >= 0 && originalBindposes != null)
                    sharedBindposes.Add(originalBindposes[oldIndex]);
                else
                    sharedBindposes.Add(sharedBoneMap[b].worldToLocalMatrix * useSMR.transform.localToWorldMatrix);
            }

            // 各グループ分割
            foreach (var group in useGroups)
            {
                if (group == null || group.bones == null || group.bones.Count == 0)
                    continue;

                List<Transform> targetBones = new List<Transform>();
                foreach (var b in group.bones)
                {
                    if (b == null)
                        continue;
                    targetBones.AddRange(GetBoneAndChildren(b));
                }

                int vertCount = originalMesh.vertexCount;
                bool[] pick = new bool[vertCount];
                for (int i = 0; i < vertCount && i < weights.Length; i++)
                {
                    BoneWeight bw = weights[i];
                    if ((bw.weight0 > 0f && bones.Length > bw.boneIndex0 && targetBones.Contains(bones[bw.boneIndex0]))
                        || (bw.weight1 > 0f && bones.Length > bw.boneIndex1 && targetBones.Contains(bones[bw.boneIndex1]))
                        || (bw.weight2 > 0f && bones.Length > bw.boneIndex2 && targetBones.Contains(bones[bw.boneIndex2]))
                        || (bw.weight3 > 0f && bones.Length > bw.boneIndex3 && targetBones.Contains(bones[bw.boneIndex3])))
                    {
                        pick[i] = true;
                    }
                }

                List<int> vertexList = new List<int>();
                int[] oldToNew = Enumerable.Repeat(-1, vertCount).ToArray();
                for (int i = 0; i < vertCount; i++)
                {
                    if (pick[i])
                    {
                        oldToNew[i] = vertexList.Count;
                        vertexList.Add(i);
                    }
                }
                if (vertexList.Count == 0)
                    continue;

                Mesh newMesh = new Mesh();
                newMesh.name = originalMesh.name + "_" + (string.IsNullOrEmpty(group.name) ? "Part" : group.name);
                newMesh.vertices = vertexList.Select(i => originalMesh.vertices[i]).ToArray();
                if (originalMesh.normals != null && originalMesh.normals.Length == vertCount)
                    newMesh.normals = vertexList.Select(i => originalMesh.normals[i]).ToArray();
                if (originalMesh.uv != null && originalMesh.uv.Length == vertCount)
                    newMesh.uv = vertexList.Select(i => originalMesh.uv[i]).ToArray();
                if (originalMesh.colors != null && originalMesh.colors.Length == vertCount)
                    newMesh.colors = vertexList.Select(i => originalMesh.colors[i]).ToArray();

                // --- サブメッシュ + 不要マテリアル削除 ---
                List<Material> newMaterials = new List<Material>();
                int subMeshCount = originalMesh.subMeshCount;
                newMesh.subMeshCount = 0;
                for (int s = 0; s < subMeshCount; s++)
                {
                    int[] origTris = originalMesh.GetTriangles(s);
                    List<int> newTris = new List<int>(origTris.Length);
                    for (int t = 0; t < origTris.Length; t += 3)
                    {
                        int a = origTris[t];
                        int b = origTris[t + 1];
                        int c = origTris[t + 2];
                        if (a >= 0 && b >= 0 && c >= 0 && oldToNew[a] != -1 && oldToNew[b] != -1 && oldToNew[c] != -1)
                        {
                            newTris.Add(oldToNew[a]);
                            newTris.Add(oldToNew[b]);
                            newTris.Add(oldToNew[c]);
                        }
                    }

                    if (newTris.Count > 0)
                    {
                        newMesh.subMeshCount++;
                        newMesh.SetTriangles(newTris.ToArray(), newMesh.subMeshCount - 1);

                        if (useSMR.sharedMaterials != null && s < useSMR.sharedMaterials.Length)
                            newMaterials.Add(useSMR.sharedMaterials[s]);
                        else
                            newMaterials.Add(null);
                    }
                }

                // boneweights
                BoneWeight[] newWeights = new BoneWeight[vertexList.Count];
                for (int vi = 0; vi < vertexList.Count; vi++)
                {
                    int oldVert = vertexList[vi];
                    BoneWeight obw = weights.Length > oldVert ? weights[oldVert] : new BoneWeight();
                    newWeights[vi] = new BoneWeight
                    {
                        boneIndex0 = RemapToShared(obw.boneIndex0, bones, unionList, sharedBonesList),
                        weight0 = obw.weight0,
                        boneIndex1 = RemapToShared(obw.boneIndex1, bones, unionList, sharedBonesList),
                        weight1 = obw.weight1,
                        boneIndex2 = RemapToShared(obw.boneIndex2, bones, unionList, sharedBonesList),
                        weight2 = obw.weight2,
                        boneIndex3 = RemapToShared(obw.boneIndex3, bones, unionList, sharedBonesList),
                        weight3 = obw.weight3,
                    };
                }
                newMesh.boneWeights = newWeights;
                newMesh.bindposes = sharedBindposes.ToArray();
                newMesh.RecalculateBounds();
                if (originalMesh.normals == null || originalMesh.normals.Length != vertCount)
                    newMesh.RecalculateNormals();

                // GameObject + SMR
                GameObject newObj = new GameObject(string.IsNullOrEmpty(group.name) ? "Part" : group.name);
                newObj.transform.position = useSMR.transform.position;
                newObj.transform.rotation = useSMR.transform.rotation;
                newObj.transform.localScale = useSMR.transform.localScale;
                newObj.transform.parent = container.transform;

                SkinnedMeshRenderer newSMR = newObj.AddComponent<SkinnedMeshRenderer>();
                newSMR.sharedMesh = newMesh;
                newSMR.bones = sharedBonesList.ToArray();
                newSMR.materials = newMaterials.ToArray();

                if (useSMR.rootBone != null && sharedBoneMap.ContainsKey(useSMR.rootBone))
                    newSMR.rootBone = sharedBoneMap[useSMR.rootBone];
                else if (newSMR.bones.Length > 0)
                    newSMR.rootBone = newSMR.bones[0];
            }

            return container;
        }

        private int RemapToShared(int oldIndex, Transform[] oldBones, List<Transform> unionList, List<Transform> sharedBonesList)
        {
            if (oldIndex < 0 || oldIndex >= oldBones.Length)
                return 0;
            Transform oldB = oldBones[oldIndex];
            int unionIdx = unionList.IndexOf(oldB);
            if (unionIdx >= 0 && unionIdx < sharedBonesList.Count)
                return unionIdx;
            for (int i = 0; i < sharedBonesList.Count; i++)
                if (sharedBonesList[i].name == oldB.name)
                    return i;
            return 0;
        }

        private List<Transform> GetBoneAndChildren(Transform root)
        {
            List<Transform> bones = new List<Transform> { root };
            foreach (Transform child in root)
                bones.AddRange(GetBoneAndChildren(child));
            return bones;
        }

        private int GetDepthFromRoot(Transform t, Transform root)
        {
            int d = 0;
            Transform cur = t;
            while (cur != null && cur != root)
            {
                d++;
                cur = cur.parent;
            }
            return d;
        }
    }
}
#endif

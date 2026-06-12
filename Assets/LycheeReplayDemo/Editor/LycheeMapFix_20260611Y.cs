// 2026-06-11 Y pass:
// 1) Make the current river surface a little fuller without re-cutting the terrain.
// 2) Replace the side status panels with a bottom floating HUD.
// 3) Import generated captain portraits as circular UI sprites.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UIImage = UnityEngine.UI.Image;
using UIText = UnityEngine.UI.Text;
using UIShadow = UnityEngine.UI.Shadow;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611Y
    {
        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string DonePath => Path.Combine(ProjectRoot, "LycheeMapFix_20260611Y.done");
        static string ReportPath => Path.Combine(ProjectRoot, "LycheeMapFix_20260611Y_report.txt");

        const string WaterDir = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver";
        const string TrimDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611V";
        const string MatDir = "Assets/LycheeMap15/Materials";
        const string UiDir = "Assets/LycheeReplayDemo/UI/Avatars";
        const string OldHud = "BattleHUD_RedBlueStatus_20260611X";
        const string BottomHud = "BattleHUD_BottomStatus_20260611Y";
        const string OldTrim = "River_ShoreTrim_20260611X";
        const string NewTrim = "River_ShoreTrim_20260611Y";
        const string RedSource = UiDir + "/red_captain_20260611X.png";
        const string BlueSource = UiDir + "/blue_captain_20260611X.png";
        const string RedCircle = UiDir + "/red_captain_circle_20260611Y.png";
        const string BlueCircle = UiDir + "/blue_captain_circle_20260611Y.png";
        const string RingSpritePath = UiDir + "/captain_avatar_ring_20260611Y.png";

        static readonly Dictionary<string, Vector2> NodeXZ = new Dictionary<string, Vector2>
        {
            {"E12_from", new Vector2(-18f, -22f)},
            {"E12_to", new Vector2(-2f, -18f)},
            {"E13_from", new Vector2(-2f, -18f)},
            {"E13_to", new Vector2(0f, -6f)}
        };

        static readonly StringBuilder Report = new StringBuilder();
        static Font uiFont;

        [MenuItem("Tools/Lychee/修复地图 20260611Y（水面加宽+底部红蓝HUD）")]
        public static void RunMenu() => Run(true);

        public static void Run(bool force)
        {
            if (!force && File.Exists(DonePath)) return;
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;

            Report.Length = 0;
            Report.AppendLine("LycheeMapFix_20260611Y report " + DateTime.Now.ToString("HH:mm:ss"));

            try
            {
                EnsureFolder(WaterDir);
                EnsureFolder(TrimDir);
                EnsureFolder(MatDir);
                EnsureFolder(UiDir);
                AssetDatabase.Refresh();

                var all = Collect(scene);
                Func<string, Transform> find = n => all.LastOrDefault(t => t != null && t.name == n);

                var waterMat = WaterMat();
                int waterMeshes = WidenWater(find, waterMat);
                int trims = RebuildTrim(find, scene);
                PreparePortraitSprites();
                BuildBottomHud(scene);

                File.WriteAllText(DonePath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();

                Report.AppendLine("waterMeshes=" + waterMeshes + ", trimStrips=" + trims + ", bottom HUD rebuilt.");
                File.WriteAllText(ReportPath, Report.ToString());
                Debug.Log(Report.ToString());
            }
            catch (Exception ex)
            {
                Report.AppendLine("ERROR: " + ex);
                File.WriteAllText(ReportPath, Report.ToString());
                Debug.LogError(ex);
            }
        }

        static List<Transform> Collect(Scene scene)
        {
            var list = new List<Transform>();
            foreach (var root in scene.GetRootGameObjects())
                if (root != null) list.AddRange(root.GetComponentsInChildren<Transform>(true));
            return list;
        }

        static int WidenWater(Func<string, Transform> find, Material waterMat)
        {
            int count = 0;
            foreach (string edge in new[] { "E12", "E13" })
            {
                Transform route = find(edge + "_Route");
                if (route == null) continue;
                MeshFilter mf = route.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Vector3[] src = mf.sharedMesh.vertices;
                int sectionCount = src.Length / 2;
                if (sectionCount < 2) continue;

                var centers = new Vector3[sectionCount];
                var halves = new float[sectionCount];
                var world = new Vector3[src.Length];
                var uv = new Vector2[src.Length];
                float vCoord = 0f;

                for (int i = 0; i < sectionCount; i++)
                {
                    Vector3 a = route.TransformPoint(src[i * 2]);
                    Vector3 b = route.TransformPoint(src[i * 2 + 1]);
                    centers[i] = (a + b) * 0.5f + Vector3.down * 0.015f;
                    halves[i] = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(centers[i].x, centers[i].z));
                }

                Vector2 from = NodeXZ[edge + "_from"];
                Vector2 to = NodeXZ[edge + "_to"];
                for (int i = 0; i < sectionCount; i++)
                {
                    Vector3 prev = centers[Mathf.Max(0, i - 1)];
                    Vector3 next = centers[Mathf.Min(sectionCount - 1, i + 1)];
                    Vector3 dir = new Vector3(next.x - prev.x, 0f, next.z - prev.z);
                    if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
                    dir.Normalize();
                    Vector3 side = new Vector3(-dir.z, 0f, dir.x);

                    float endDist = Mathf.Min(
                        Vector2.Distance(new Vector2(centers[i].x, centers[i].z), from),
                        Vector2.Distance(new Vector2(centers[i].x, centers[i].z), to));
                    float add = Mathf.Lerp(0.10f, edge == "E12" ? 0.32f : 0.26f, Smooth01(endDist / 4.2f));
                    float half = Mathf.Min(halves[i] + add, edge == "E12" ? 2.25f : 2.04f);
                    if (endDist < 1.15f) half = Mathf.Min(half, halves[i] + 0.13f);

                    world[i * 2] = centers[i] + side * half;
                    world[i * 2 + 1] = centers[i] - side * half;

                    if (i > 0)
                        vCoord += Vector2.Distance(new Vector2(centers[i].x, centers[i].z), new Vector2(centers[i - 1].x, centers[i - 1].z)) * 0.22f;
                    uv[i * 2] = new Vector2(0f, vCoord);
                    uv[i * 2 + 1] = new Vector2(1f, vCoord);
                }

                var local = new Vector3[world.Length];
                for (int i = 0; i < world.Length; i++) local[i] = route.InverseTransformPoint(world[i]);
                var tris = new List<int>((sectionCount - 1) * 6);
                for (int i = 0; i + 1 < sectionCount; i++)
                {
                    int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }

                var mesh = new UnityEngine.Mesh();
                mesh.name = edge + "_Route_FullerWater_20260611Y";
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = local;
                mesh.uv = uv;
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                string path = AssetDatabase.GenerateUniqueAssetPath(WaterDir + "/" + mesh.name + ".asset");
                AssetDatabase.CreateAsset(mesh, path);
                mf.sharedMesh = mesh;
                var col = route.GetComponent<MeshCollider>();
                if (col != null)
                {
                    col.sharedMesh = null;
                    col.sharedMesh = mesh;
                }
                var renderer = route.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = waterMat;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                Report.AppendLine(edge + " widened water asset " + path);
                count++;
            }
            return count;
        }

        static int RebuildTrim(Func<string, Transform> find, Scene scene)
        {
            var old = find(OldTrim);
            if (old != null) old.gameObject.SetActive(false);

            var root = new GameObject(NewTrim);
            var mapRoot = scene.GetRootGameObjects().FirstOrDefault(go => go != null && go.name.StartsWith("LYCHEE_CONFIG_MAP"));
            if (mapRoot != null) root.transform.SetParent(mapRoot.transform, true);
            var mat = TrimMat();
            int strips = 0;

            foreach (string edge in new[] { "E12", "E13" })
            {
                Transform route = find(edge + "_Route");
                if (route == null) continue;
                MeshFilter mf = route.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3[] v = mf.sharedMesh.vertices;
                int n = v.Length / 2;
                var left = new List<Vector3>();
                var right = new List<Vector3>();
                var centers = new List<Vector3>();
                for (int i = 0; i < n; i++)
                {
                    Vector3 a = route.TransformPoint(v[i * 2]);
                    Vector3 b = route.TransformPoint(v[i * 2 + 1]);
                    Vector3 c = (a + b) * 0.5f;
                    left.Add(a);
                    right.Add(b);
                    centers.Add(c);
                }
                MakeTrim(edge + "_L", root.transform, mat, left, centers);
                MakeTrim(edge + "_R", root.transform, mat, right, centers);
                strips += 2;
            }
            return strips;
        }

        static void MakeTrim(string name, Transform root, Material mat, List<Vector3> edgePts, List<Vector3> centers)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < edgePts.Count; i++)
            {
                Vector3 outward = edgePts[i] - centers[i];
                outward.y = 0f;
                if (outward.sqrMagnitude < 0.0001f) outward = Vector3.right;
                outward.Normalize();

                Vector3 inner = edgePts[i] - outward * 0.05f;
                Vector3 outer = edgePts[i] + outward * 0.44f;
                verts.Add(inner + Vector3.up * 0.028f);
                verts.Add(outer + Vector3.up * 0.036f);
            }

            for (int i = 0; i + 1 < edgePts.Count; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                tris.Add(a); tris.Add(c); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(b);
            }

            var mesh = new UnityEngine.Mesh();
            mesh.name = name + "_RiverBankTrim_20260611Y";
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            string path = AssetDatabase.GenerateUniqueAssetPath(TrimDir + "/" + mesh.name + ".asset");
            AssetDatabase.CreateAsset(mesh, path);

            var go = new GameObject(name + "_BankTrim");
            go.transform.SetParent(root, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        static void PreparePortraitSprites()
        {
            CreateCircularAvatar(RedSource, RedCircle);
            CreateCircularAvatar(BlueSource, BlueCircle);
            CreateRingSprite(RingSpritePath);
            ImportSprite(RedCircle);
            ImportSprite(BlueCircle);
            ImportSprite(RingSpritePath);
        }

        static void CreateCircularAvatar(string srcAssetPath, string outAssetPath)
        {
            string src = Path.Combine(ProjectRoot, srcAssetPath);
            string dst = Path.Combine(ProjectRoot, outAssetPath);
            if (!File.Exists(src)) return;

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            source.LoadImage(File.ReadAllBytes(src));
            int size = 512;
            var output = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    Color c = source.GetPixelBilinear(u, v);
                    float dx = u - 0.5f, dy = v - 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((0.500f - r) / 0.018f);
                    c.a *= a;
                    output.SetPixel(x, y, c);
                }
            }
            output.Apply();
            File.WriteAllBytes(dst, output.EncodeToPNG());
        }

        static void CreateRingSprite(string outAssetPath)
        {
            string dst = Path.Combine(ProjectRoot, outAssetPath);
            int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size - 0.5f;
                    float v = (y + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float outer = Mathf.Clamp01((0.500f - r) / 0.012f);
                    float inner = Mathf.Clamp01((r - 0.405f) / 0.012f);
                    float a = outer * inner;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            File.WriteAllBytes(dst, tex.EncodeToPNG());
        }

        static void ImportSprite(string assetPath)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }

        static void BuildBottomHud(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (root.name == OldHud || root.name == BottomHud) root.SetActive(false);
            }

            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (uiFont == null) uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var canvasGo = new GameObject(BottomHud, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 95;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var dock = UIObject("BottomFloatingStatus", canvasGo.transform);
            var dr = dock.GetComponent<RectTransform>();
            dr.anchorMin = new Vector2(0.5f, 0f);
            dr.anchorMax = new Vector2(0.5f, 0f);
            dr.pivot = new Vector2(0.5f, 0f);
            dr.anchoredPosition = new Vector2(0f, 26f);
            dr.sizeDelta = new Vector2(960f, 174f);

            var center = UIObject("CenterDataPanel", dock.transform);
            var centerImg = center.AddComponent<UIImage>();
            centerImg.color = new Color(0.025f, 0.031f, 0.036f, 0.76f);
            var cr = center.GetComponent<RectTransform>();
            cr.anchorMin = cr.anchorMax = new Vector2(0.5f, 0.5f);
            cr.pivot = new Vector2(0.5f, 0.5f);
            cr.anchoredPosition = new Vector2(0f, 86f);
            cr.sizeDelta = new Vector2(650f, 138f);

            AddAvatar(dock.transform, true, -415f);
            AddAvatar(dock.transform, false, 415f);
            AddTopScore(center.transform);
            AddCompareRow(center.transform, "据点", "2/5", 0.40f, "3/5", 0.60f, 38f);
            AddCompareRow(center.transform, "士气", "78%", 0.78f, "71%", 0.71f, 10f);
            AddCompareRow(center.transform, "体力", "92%", 0.92f, "86%", 0.86f, -18f);
            AddCompareRow(center.transform, "推进", "64%", 0.64f, "58%", 0.58f, -46f);
        }

        static void AddAvatar(Transform parent, bool red, float x)
        {
            Color team = red ? new Color(0.95f, 0.18f, 0.12f, 1f) : new Color(0.18f, 0.45f, 1f, 1f);
            Sprite avatar = AssetDatabase.LoadAssetAtPath<Sprite>(red ? RedCircle : BlueCircle);
            Sprite ring = AssetDatabase.LoadAssetAtPath<Sprite>(RingSpritePath);

            var shadow = UIObject((red ? "Red" : "Blue") + "AvatarShadow", parent);
            var si = shadow.AddComponent<UIImage>();
            si.sprite = ring;
            si.color = new Color(0f, 0f, 0f, 0.42f);
            SetRect(shadow, new Vector2(x + 3f, 82f), new Vector2(142f, 142f));

            var imgGo = UIObject((red ? "Red" : "Blue") + "CaptainAvatar", parent);
            var img = imgGo.AddComponent<UIImage>();
            img.sprite = avatar;
            img.preserveAspect = true;
            img.color = Color.white;
            SetRect(imgGo, new Vector2(x, 86f), new Vector2(124f, 124f));

            var ringGo = UIObject((red ? "Red" : "Blue") + "AvatarRing", parent);
            var ri = ringGo.AddComponent<UIImage>();
            ri.sprite = ring;
            ri.color = team;
            SetRect(ringGo, new Vector2(x, 86f), new Vector2(140f, 140f));

            var label = Text((red ? "红队  3" : "2  蓝队"), parent, 20, FontStyle.Bold, team, TextAnchor.MiddleCenter);
            SetRect(label.gameObject, new Vector2(x, 12f), new Vector2(150f, 30f));
        }

        static void AddTopScore(Transform parent)
        {
            var red = Text("红队", parent, 16, FontStyle.Bold, new Color(1f, 0.34f, 0.28f, 1f), TextAnchor.MiddleRight);
            SetRect(red.gameObject, new Vector2(-95f, 51f), new Vector2(82f, 24f));
            var mid = Text("3  :  2", parent, 28, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            SetRect(mid.gameObject, new Vector2(0f, 51f), new Vector2(110f, 34f));
            AddShadow(mid.gameObject);
            var blue = Text("蓝队", parent, 16, FontStyle.Bold, new Color(0.34f, 0.58f, 1f, 1f), TextAnchor.MiddleLeft);
            SetRect(blue.gameObject, new Vector2(95f, 51f), new Vector2(82f, 24f));
        }

        static void AddCompareRow(Transform parent, string label, string redValue, float redFill, string blueValue, float blueFill, float y)
        {
            var redValueText = Text(redValue, parent, 13, FontStyle.Bold, Color.white, TextAnchor.MiddleRight);
            SetRect(redValueText.gameObject, new Vector2(-252f, y), new Vector2(56f, 18f));
            var blueValueText = Text(blueValue, parent, 13, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            SetRect(blueValueText.gameObject, new Vector2(252f, y), new Vector2(56f, 18f));
            var labelText = Text(label, parent, 13, FontStyle.Bold, new Color(0.88f, 0.92f, 0.93f, 0.96f), TextAnchor.MiddleCenter);
            SetRect(labelText.gameObject, new Vector2(0f, y), new Vector2(58f, 18f));

            AddBar(parent, new Vector2(-130f, y), new Vector2(190f, 9f), redFill, new Color(0.95f, 0.18f, 0.12f, 1f), true);
            AddBar(parent, new Vector2(130f, y), new Vector2(190f, 9f), blueFill, new Color(0.18f, 0.45f, 1f, 1f), false);
        }

        static void AddBar(Transform parent, Vector2 pos, Vector2 size, float fill, Color color, bool fillFromRight)
        {
            var bg = UIObject("BarBg", parent);
            var bi = bg.AddComponent<UIImage>();
            bi.color = new Color(1f, 1f, 1f, 0.12f);
            SetRect(bg, pos, size);

            var f = UIObject("BarFill", bg.transform);
            var fi = f.AddComponent<UIImage>();
            fi.color = color;
            var rt = f.GetComponent<RectTransform>();
            rt.anchorMin = fillFromRight ? new Vector2(1f - Mathf.Clamp01(fill), 0f) : new Vector2(0f, 0f);
            rt.anchorMax = fillFromRight ? new Vector2(1f, 1f) : new Vector2(Mathf.Clamp01(fill), 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static GameObject UIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static UIText Text(string value, Transform parent, int size, FontStyle style, Color color, TextAnchor align)
        {
            var go = UIObject("Text", parent);
            var text = go.AddComponent<UIText>();
            text.text = value;
            text.font = uiFont;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = align;
            text.raycastTarget = false;
            return text;
        }

        static void SetRect(GameObject go, Vector2 pos, Vector2 size)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        static void AddShadow(GameObject go)
        {
            var sh = go.AddComponent<UIShadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.55f);
            sh.effectDistance = new Vector2(1.4f, -1.4f);
        }

        static Material WaterMat()
        {
            string path = MatDir + "/M_RiverWater_Fuller_20260611Y.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                mat = new Material(shader);
                mat.name = "M_RiverWater_Fuller_20260611Y";
                AssetDatabase.CreateAsset(mat, path);
            }
            SetMat(mat, new Color(0.20f, 0.66f, 0.94f, 0.68f), true);
            return mat;
        }

        static Material TrimMat()
        {
            string path = MatDir + "/M_RiverBankTrim_20260611Y.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                mat = new Material(shader);
                mat.name = "M_RiverBankTrim_20260611Y";
                AssetDatabase.CreateAsset(mat, path);
            }
            SetMat(mat, new Color(0.36f, 0.37f, 0.18f, 0.98f), false);
            return mat;
        }

        static void SetMat(Material mat, Color color, bool transparent)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
            if (transparent)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent + 18;
            }
            else
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
                mat.renderQueue = (int)RenderQueue.Geometry + 7;
            }
            EditorUtility.SetDirty(mat);
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611YAutoRun
    {
        static LycheeMapFix20260611YAutoRun()
        {
            EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611Y.done"))) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611Y.Run(false);
        }
    }
}

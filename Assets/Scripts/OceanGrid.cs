using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a flat, uniformly tessellated square grid in the XZ plane.
/// The ocean shader displaces vertices on the GPU, so the CPU mesh stays flat.
/// Size and tessellation are driven by real-world dimensions in metres.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OceanGrid : MonoBehaviour
{
    const int UINT16_VERTEX_LIMIT = 65535;
    const int MAX_RESOLUTION = 512;

    // Vertices get pushed up/down in the vertex shader; padded bounds keep the mesh from being culled.
    const float VERTICAL_BOUNDS_MARGIN = 50f;

    [Header("Physical Size (metres)")]
    [SerializeField, Min(1f)]
    [Tooltip("海洋网格的边长，单位米。")]
    float sizeMeters = 500f;

    [SerializeField, Min(0.1f)]
    [Tooltip("每个格子的边长，单位米。决定顶点密度，太小会拖慢。")]
    float cellSizeMeters = 2.5f;

    [SerializeField]
    Material material;

    Mesh generatedMesh;
    Material fallbackMaterial;
    float builtSizeMeters;
    int builtResolution;

    public Mesh GeneratedMesh => generatedMesh;

    /// <summary>网格边长（米）。</summary>
    public float SizeMeters
    {
        get => sizeMeters;
        set
        {
            sizeMeters = Mathf.Max(1f, value);
            RebuildIfChanged();
        }
    }

    /// <summary>每格边长（米）。</summary>
    public float CellSizeMeters
    {
        get => cellSizeMeters;
        set
        {
            cellSizeMeters = Mathf.Max(0.1f, value);
            RebuildIfChanged();
        }
    }

    /// <summary>每边的格子数，由尺寸和格子边长推出。</summary>
    public int Resolution => Mathf.Clamp(Mathf.RoundToInt(sizeMeters / cellSizeMeters), 1, MAX_RESOLUTION);

    /// <summary>实际格子边长（米）。尺寸不能被格子边长整除时会略有偏差。</summary>
    public float EffectiveCellSize => sizeMeters / Resolution;

    MeshFilter Filter => GetComponent<MeshFilter>();
    MeshRenderer Renderer => GetComponent<MeshRenderer>();

    void OnEnable()
    {
        Regenerate();
    }

    void OnValidate()
    {
        sizeMeters = Mathf.Max(1f, sizeMeters);
        cellSizeMeters = Mathf.Max(0.1f, cellSizeMeters);
        RebuildIfChanged();
    }

    void OnDisable()
    {
        DestroyGeneratedMesh();
    }

    [ContextMenu("Regenerate Grid")]
    public void Regenerate()
    {
        if (!isActiveAndEnabled)
            return;

        int resolution = Resolution;

        DestroyGeneratedMesh();
        generatedMesh = BuildMesh(sizeMeters, resolution);
        builtSizeMeters = sizeMeters;
        builtResolution = resolution;

        Filter.sharedMesh = generatedMesh;
        Renderer.sharedMaterial = ResolveMaterial();
    }

    void RebuildIfChanged()
    {
        if (sizeMeters == builtSizeMeters && Resolution == builtResolution)
            return;

        Regenerate();
    }

    static Mesh BuildMesh(float size, int resolution)
    {
        int quads = Mathf.Max(1, resolution);
        int verticesPerSide = quads + 1;
        int vertexCount = verticesPerSide * verticesPerSide;

        Mesh mesh = new Mesh { name = "Ocean Grid" };
        if (vertexCount > UINT16_VERTEX_LIMIT)
            mesh.indexFormat = IndexFormat.UInt32;

        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector4[] tangents = new Vector4[vertexCount];
        int[] triangles = new int[quads * quads * 6];

        float step = size / quads;
        float half = size * 0.5f;

        for (int z = 0; z < verticesPerSide; z++)
        {
            for (int x = 0; x < verticesPerSide; x++)
            {
                int index = x + z * verticesPerSide;
                vertices[index] = new Vector3(x * step - half, 0f, z * step - half);
                uvs[index] = new Vector2((float)x / quads, (float)z / quads);
                normals[index] = Vector3.up;
                tangents[index] = new Vector4(1f, 0f, 0f, 1f);
            }
        }

        int triangleIndex = 0;
        for (int z = 0; z < quads; z++)
        {
            for (int x = 0; x < quads; x++)
            {
                int bottomLeft = x + z * verticesPerSide;
                int topLeft = bottomLeft + verticesPerSide;
                int topRight = topLeft + 1;

                triangles[triangleIndex++] = bottomLeft;
                triangles[triangleIndex++] = topLeft;
                triangles[triangleIndex++] = topRight;

                triangles[triangleIndex++] = bottomLeft;
                triangles[triangleIndex++] = topRight;
                triangles[triangleIndex++] = bottomLeft + 1;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.triangles = triangles;

        mesh.bounds = new Bounds(Vector3.zero, new Vector3(size, 2f * VERTICAL_BOUNDS_MARGIN, size));
        return mesh;
    }

    Material ResolveMaterial()
    {
        // 1) Inspector 上显式指定了材质，就用它（海面材质会走这条路）
        if (material != null)
            return material;

        // 2) 渲染器上已经挂着一个有效材质（比如运行时由别的脚本换上的海面材质），不要覆盖它
        Material current = Renderer.sharedMaterial;
        if (current != null && current.shader != null && current != fallbackMaterial)
            return current;

        // 3) 什么都没有时，临时造一个 URP Lit 材质先让网格看得见
        if (fallbackMaterial == null)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                return Renderer.sharedMaterial;

            fallbackMaterial = new Material(lit) { name = "Ocean Grid (Fallback)" };
            fallbackMaterial.SetColor("_BaseColor", new Color(0.08f, 0.25f, 0.4f));
        }

        return fallbackMaterial;
    }

    void DestroyGeneratedMesh()
    {
        if (generatedMesh == null)
            return;

        if (Application.isPlaying)
            Destroy(generatedMesh);
        else
            DestroyImmediate(generatedMesh);

        generatedMesh = null;
    }
}

//#define WITH_SCALE_MATRIX
using System;
using System.IO;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Single;
using MathNet.Numerics.LinearAlgebra.Solvers;
using UnityEditor;
using UnityEngine;

//[ExecuteInEditMode]
[RequireComponent(typeof (SkinnedMeshRenderer))]
public abstract class DDMSkinnedMeshGPUBase : MonoBehaviour
{
    public int iterations = 5;

    public float smoothLambda = 0.9f;

    public bool useCompute = true;

    public float adjacencyMatchingVertexTolerance = 1e-4f; // 0.0f;

    public enum DebugMode
    {
        Off,
        CompareWithLinearBlend /*SmoothOnly, Deltas*/
    }

    public DebugMode debugMode = DebugMode.Off;

    protected bool actuallyUseCompute
    {
        get
        {
            return useCompute && debugMode != DebugMode.CompareWithLinearBlend;
        }
    }

    protected int vCount;

    protected int bCount;

    protected Mesh mesh;

    protected Mesh meshForCPUOutput;

    protected SkinnedMeshRenderer skin;

    protected struct DeformedMesh
    {
        public DeformedMesh(int vertexCount_)
        {
            vertexCount = vertexCount_;
            vertices = new Vector3[vertexCount];
            normals = new Vector3[vertexCount];
            deltaV = new Vector3[vertexCount];
            deltaN = new Vector3[vertexCount];
        }

        public int vertexCount;

        public Vector3[] vertices;

        public Vector3[] normals;

        public Vector3[] deltaV;

        public Vector3[] deltaN;
    }

    protected DeformedMesh deformedMesh;

    protected int[,] adjacencyMatrix;

    // Compute
    [HideInInspector]
    public ComputeShader precomputeShader;

    [HideInInspector]
    public Shader ductTapedShader;

    [HideInInspector]
    public ComputeShader computeShader;

    protected int deformKernel;

    protected int computeThreadGroupSizeX;

    protected ComputeBuffer verticesCB; // float3

    protected ComputeBuffer normalsCB; // float3

    protected ComputeBuffer weightsCB; // float4 + int4

    protected ComputeBuffer bonesCB; // float4x4

    protected ComputeBuffer omegasCB; // float4x4 * 4

    protected ComputeBuffer outputCB; // float3 + float3

    protected ComputeBuffer laplacianCB;

    protected Material ductTapedMaterial;

    public const int maxOmegaCount = 32;

    protected void InitBase()
    {
        Debug
            .Assert(SystemInfo.supportsComputeShaders &&
            precomputeShader != null);

        if (precomputeShader)
        {
            precomputeShader = Instantiate(precomputeShader);
        }
        if (computeShader)
        {
            computeShader = Instantiate(computeShader);
        }
        skin = GetComponent<SkinnedMeshRenderer>();
        mesh = skin.sharedMesh;
        meshForCPUOutput = Instantiate(mesh);

        deformedMesh = new DeformedMesh(mesh.vertexCount);

        adjacencyMatrix =
            GetCachedAdjacencyMatrix(mesh, adjacencyMatchingVertexTolerance);

        vCount = mesh.vertexCount;
        bCount = skin.bones.Length;

        BoneWeight[] bws = mesh.boneWeights;

        // Compute
        verticesCB = new ComputeBuffer(vCount, 3 * sizeof(float));
        normalsCB = new ComputeBuffer(vCount, 3 * sizeof(float));
        weightsCB =
            new ComputeBuffer(vCount, 4 * sizeof(float) + 4 * sizeof(int));
        bonesCB = new ComputeBuffer(bCount, 16 * sizeof(float));
        verticesCB.SetData(mesh.vertices);
        normalsCB.SetData(mesh.normals);
        weightsCB.SetData (bws);

        omegasCB =
            new ComputeBuffer(vCount * maxOmegaCount,
                (10 * sizeof(float) + sizeof(int)));

        outputCB = new ComputeBuffer(vCount, 6 * sizeof(float));

        laplacianCB =
            new ComputeBuffer(vCount * maxOmegaCount,
                (sizeof(int) + sizeof(float)));
        DDMUtilsGPU
            .ComputeLaplacianCBFromAdjacency(ref laplacianCB,
            precomputeShader,
            adjacencyMatrix);
        DDMUtilsGPU
            .ComputeOmegasCBFromLaplacianCB(ref omegasCB,
            precomputeShader,
            verticesCB,
            laplacianCB,
            weightsCB,
            bCount,
            iterations,
            smoothLambda);

        if (computeShader && ductTapedShader)
        {
            deformKernel = computeShader.FindKernel("DeformMesh");
            computeShader.SetBuffer(deformKernel, "Vertices", verticesCB);
            computeShader.SetBuffer(deformKernel, "Normals", normalsCB);
            computeShader.SetBuffer(deformKernel, "Bones", bonesCB);
            computeShader.SetBuffer(deformKernel, "Output", outputCB);
            computeShader.SetInt("VertexCount", vCount);

            uint
                threadGroupSizeX,
                threadGroupSizeY,
                threadGroupSizeZ;
            computeShader
                .GetKernelThreadGroupSizes(deformKernel,
                out threadGroupSizeX,
                out threadGroupSizeY,
                out threadGroupSizeZ);
            computeThreadGroupSizeX = (int) threadGroupSizeX;

            ductTapedMaterial = new Material(ductTapedShader);
            ductTapedMaterial.CopyPropertiesFromMaterial(skin.sharedMaterial);
        }
        else
        {
            useCompute = false;
        }
    }

    protected void ReleaseBase()
    {
        if (verticesCB == null)
        {
            return;
        }
        laplacianCB.Release();

        verticesCB.Release();
        normalsCB.Release();
        weightsCB.Release();
        bonesCB.Release();
        omegasCB.Release();
        outputCB.Release();
    }

    protected void UpdateBase()
    {
        bool compareWithSkinning =
            debugMode == DebugMode.CompareWithLinearBlend;
        if (!compareWithSkinning)
        {
            if (actuallyUseCompute)
                UpdateMeshOnGPU();
            else
                UpdateMeshOnCPU();
        }
        if (compareWithSkinning)
            DrawVerticesVsSkin();
        else
            DrawMesh();

        skin.enabled = compareWithSkinning;
    }


#region Adjacency matrix cache
    [System.Serializable]
    public struct AdjacencyMatrix
    {
        public int

                w,
                h;

        public int[] storage;

        public AdjacencyMatrix(int[,] src)
        {
            w = src.GetLength(0);
            h = src.GetLength(1);
            storage = new int[w * h];
            Buffer.BlockCopy(src, 0, storage, 0, storage.Length * sizeof(int));
        }

        public int[,] data
        {
            get
            {
                var retVal = new int[w, h];
                Buffer
                    .BlockCopy(storage,
                    0,
                    retVal,
                    0,
                    storage.Length * sizeof(int));
                return retVal;
            }
        }
    }

    protected static System.Collections.Generic.Dictionary<Mesh, int[,]>
        adjacencyMatrixMap =
            new System.Collections.Generic.Dictionary<Mesh, int[,]>();

    public static int[,]
    GetCachedAdjacencyMatrix(
        Mesh mesh,
        float adjacencyMatchingVertexTolerance = 1e-4f,
        bool readCachedADjacencyMatrix = false
    )
    {
        int[,] adjacencyMatrix;
        if (adjacencyMatrixMap.TryGetValue(mesh, out adjacencyMatrix))
        {
            return adjacencyMatrix;
        }

        //#if UNITY_EDITOR
        //		if (readCachedADjacencyMatrix)
        //		{
        //			//var path = Path.Combine(Application.persistentDataPath, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)) + ".adj");
        //			var path = Path.Combine("", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)) + "_" + adjacencyMatchingVertexTolerance.ToString() + ".adj");
        //			Debug.Log(path);
        //			if (File.Exists(path))
        //			{
        //				string json = File.ReadAllText(path);
        //				adjacencyMatrix = JsonUtility.FromJson<AdjacencyMatrix>(json).data;
        //			}
        //			else
        //			{
        //#endif
        adjacencyMatrix =
            MeshUtils
                .BuildAdjacencyMatrix(mesh.vertices,
                mesh.triangles,
                maxOmegaCount,
                adjacencyMatchingVertexTolerance *
                adjacencyMatchingVertexTolerance);

        //#if UNITY_EDITOR
        //				var json = JsonUtility.ToJson(new AdjacencyMatrix(adjacencyMatrix));
        //				Debug.Log(json);
        //				using (FileStream fs = new FileStream(path, FileMode.Create))
        //				{
        //					using (StreamWriter writer = new StreamWriter(fs))
        //					{
        //						writer.Write(json);
        //					}
        //				}
        //			}
        //		}
        //		else
        //        {
        //			adjacencyMatrix = MeshUtils.BuildAdjacencyMatrix(mesh.vertices, mesh.triangles, maxOmegaCount, adjacencyMatchingVertexTolerance * adjacencyMatchingVertexTolerance);
        //		}
        //#endif
        adjacencyMatrixMap.Add (mesh, adjacencyMatrix);
        return adjacencyMatrix;
    }
#endregion



#region Direct Delta Mush implementation

    protected virtual void UpdateMeshOnCPU()
    {
        Debug.LogError("UpdateMeshOnCPU Not implemented.");
    }

    protected virtual void UpdateMeshOnGPU()
    {
        Debug.LogError("UpdateMeshOnGPU Not implemented.");
    }

    protected Matrix4x4[] GenerateBoneMatrices()
    {
        Matrix4x4[] boneMatrices = new Matrix4x4[skin.bones.Length];

#if WITH_SCALE_MATRIX
        Matrix4x4[] scaleMatrices = new Matrix4x4[skin.bones.Length];
#endif // WITH_SCALE_MATRIX

        for (int i = 0; i < boneMatrices.Length; i++)
        {
            Matrix4x4 localToWorld = skin.bones[i].localToWorldMatrix;
            Matrix4x4 bindPose = mesh.bindposes[i];

#if WITH_SCALE_MATRIX
            Vector3 localScale = localToWorld.lossyScale;
            Vector3 bpScale = bindPose.lossyScale;

            localToWorld.SetColumn(0, localToWorld.GetColumn(0) / localScale.x);
            localToWorld.SetColumn(1, localToWorld.GetColumn(1) / localScale.y);
            localToWorld.SetColumn(2, localToWorld.GetColumn(2) / localScale.z);
            bindPose.SetColumn(0, bindPose.GetColumn(0) / bpScale.x);
            bindPose.SetColumn(1, bindPose.GetColumn(1) / bpScale.y);
            bindPose.SetColumn(2, bindPose.GetColumn(2) / bpScale.z);

            scaleMatrices[i] =
                Matrix4x4.Scale(localScale) * Matrix4x4.Scale(bpScale);
#endif // WITH_SCALE_MATRIX

            boneMatrices[i] = localToWorld * bindPose;
        }
        return boneMatrices;
    }


#endregion



#region Helpers
    void DrawMesh()
    {
        if (actuallyUseCompute)
        {
            mesh.bounds = skin.bounds; // skin is actually disabled, so it only remembers the last animation frame
            Graphics.DrawMesh(mesh, Matrix4x4.identity, ductTapedMaterial, 0);
        }
        else
            Graphics
                .DrawMesh(meshForCPUOutput,
                Matrix4x4.identity,
                skin.sharedMaterial,
                0);
    }

    void DrawDeltas()
    {
    }

    void DrawVerticesVsSkin()
    {
    }
#endregion
}

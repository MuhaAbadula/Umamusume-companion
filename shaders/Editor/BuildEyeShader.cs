using UnityEditor;
using System.IO;

// Build with the Jalopy editor version (2017.4.23f1), with shaders/ copied under Assets/.
public static class BuildEyeShader
{
    public static void Build()
    {
        Directory.CreateDirectory("Build");
        var bundle = new AssetBundleBuild { assetBundleName = "eyes.unity3d",
            assetNames = new[] { "Assets/shaders/EyeSurface.shader" } };
        if (BuildPipeline.BuildAssetBundles("Build", new[] { bundle },
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64) == null)
            throw new System.Exception("Eye shader bundle build failed");
    }
}

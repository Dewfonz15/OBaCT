using UnityEditor;
using System.IO;

public class BuildAssetBundles
{
    [MenuItem("Assets/Build AssetBundles")]
    static void BuildAllAssetBundles()
    {
        string dir = "Assets/AssetBundles";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        BuildPipeline.BuildAssetBundles(dir,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);

        UnityEngine.Debug.Log("✅ AssetBundles built to: " + Path.GetFullPath(dir));
    }
}

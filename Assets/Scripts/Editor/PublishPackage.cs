using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager.Requests;
using UnityEngine;


public class PublishPackage : EditorWindow
{
  string packageFolder;
  string targetFolder;

  PackRequest req;
  string packagePath;

  [MenuItem("Tools/Publish package")]
  public static void Open()
  {
    EditorWindow.GetWindow<PublishPackage>().Show();
  }

  private void OnGUI()
  {
    GUI.enabled = req == null || req.IsCompleted;

    var newpackageFolder = EditorGUILayout.TextField("Package folder (relative to project packages):", packageFolder);
    targetFolder = EditorGUILayout.TextField("Target folder (absolute):", targetFolder);
    if (newpackageFolder != packageFolder)
    {
      packageFolder = newpackageFolder;
      packagePath = Path.Combine(Application.dataPath, "../Packages", packageFolder);
    }
    if (GUILayout.Button("Make package"))
    {
      req = UnityEditor.PackageManager.Client.Pack(packagePath, targetFolder);
    }
    if (req != null)
    {
      GUILayout.Label("Result: " + req.Status);
      GUILayout.Label("Error: " + req.Error);
    }
  }
}


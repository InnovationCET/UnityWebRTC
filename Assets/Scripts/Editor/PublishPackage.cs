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

  [MenuItem("Tools/Publish package")]
  public static void Open()
  {
    EditorWindow.GetWindow<PublishPackage>().Show();
  }

  private void OnEnable()
  {
    targetFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
  }

  private void OnGUI()
  {
    GUI.enabled = req == null || req.IsCompleted;

    packageFolder = EditorGUILayout.DelayedTextField("Package folder (relative to project packages):", packageFolder);
    targetFolder = EditorGUILayout.TextField("Target folder (absolute):", targetFolder);

    if (GUILayout.Button("Find package folder..."))
      packageFolder = Path.GetFileName(EditorUtility.OpenFolderPanel("Choose package", Path.Combine(Application.dataPath, "../Packages"), ""));
      
    if (GUILayout.Button("Make package"))
    {
      var packagePath = Path.Combine(Application.dataPath, "../Packages", packageFolder);
      req = UnityEditor.PackageManager.Client.Pack(packagePath, targetFolder);
    }
    if (req != null)
    {
      GUILayout.Label("Result: " + req.Status);
      GUILayout.Label("Error: " + req.Error);
    }
  }
}


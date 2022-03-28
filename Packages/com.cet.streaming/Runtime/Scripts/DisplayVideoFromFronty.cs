using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(VideoFromFrontyLocally))]
public class DisplayVideoFromFronty : MonoBehaviour
{
  public RawImage screen;

  VideoFromFrontyLocally video_stream;
  ConcurrentQueue<byte[]> images = new ConcurrentQueue<byte[]>();
  Texture2D texture;

  void Start()
  {
    texture = new Texture2D(1920, 1080);
    video_stream = GetComponent<VideoFromFrontyLocally>();
    video_stream.OnNewFrame += OnFrame;
  }
  private void OnDestroy()
  {
    video_stream.OnNewFrame -= OnFrame;
    Destroy(texture);
  }

  void OnFrame(byte[] jpg, long num, long timestamp)
  {
    // called from another thread - can't call unity functions here
    images.Enqueue(jpg);
  }

  // Update is called once per frame
  void Update()
  {
    byte[] image = null;
    // pump all previous images out of the queue; we want just the last one
    while (images.TryDequeue(out var newimage))
    {
      image = newimage;
    }

    if (image != null && screen != null)
    {
      if (ImageConversion.LoadImage(texture, image))
      {
        screen.texture = texture;
      }
    }
  }
}

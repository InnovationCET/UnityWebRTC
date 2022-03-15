using System.Linq;
using Unity.WebRTC;
using UnityEngine;

public class WebRtcMain : MonoBehaviour
{
  [Header("Stun/Turn servers")]
  public string[] urls = new[]{
    "stun:stun.l.google.com:19302",
    "turn:20.86.157.60:3478"
  };
  public string username = "amit";
  public string password = "Amit2020!";

  [Header("Signalling server details")]
  public string HttpServerAddress = "http://20.86.157.60:3000/";
  [Tooltip("The interval (in ms) that the server is polled at")]
  public int PollTimeMs = 500;

  private bool inited;
  public static WebRtcMain Instance { get; private set; }

  public static RTCConfiguration config
  {
    get
    {
      RTCConfiguration config = default;
      config.iceServers = (from url in WebRtcMain.Instance.urls
                           select new RTCIceServer
                           {
                             urls = new[] { url },
                             username = WebRtcMain.Instance.username,
                             credentialType = RTCIceCredentialType.Password,
                             credential = WebRtcMain.Instance.password
                           }).ToArray();
      return config;
    }
  }

  public void InitIfNeeded()
  {
    if (!inited)
    {
      WebRTC.Initialize();
      StartCoroutine(WebRTC.Update());
      inited = true;
    }
  }

  private void Awake()
  {
    Instance = this;
  }

  private void OnDestroy()
  {
    if(inited)
      WebRTC.Dispose();
  }
}

using System;
using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

public abstract class BaseForRtcConnection : MonoBehaviour
{
  [SerializeField] protected string LogTag;
  [SerializeField] protected bool printLogs;
  [SerializeField] protected bool keepAlive;
  [SerializeField] protected bool connectOnStart;
  [SerializeField] protected string localId;
  [SerializeField] protected string remoteId;

  public bool dataChannelOpen { get; protected set; }
  public event DelegateOnMessage OnMessage; // will be sent the bytes received, or null on close
  protected RTCPeerConnection pc;
  protected RTCDataChannel data_channel;

  protected abstract IEnumerator InitiateConnection();
  protected abstract void BuildPeerConnection(string remote);
  protected abstract void OnAddTrack(MediaStreamTrackEvent e);

  protected void onMessage(byte[] b) => OnMessage?.Invoke(b);

  protected virtual void Start()
  {
    LogTag = (string.IsNullOrWhiteSpace(LogTag) ? this.GetType().Name : LogTag.Trim()) + ": ";
    localId ??= System.Net.Dns.GetHostName();
    if (connectOnStart)
      StartCoroutine(InitiateConnection());
  }

  private void OnDestroy()
  {
    try
    {
      Hangup();
    }
    catch { }
  }

  protected virtual void Hangup()
  {
    if (pc != null)
    {
      Log("Hanging up");
      
      foreach (var recv in pc.GetReceivers())
      {
        foreach (var m in recv.Streams)
          foreach (var track in m.GetTracks())
          {
            track.Stop();
            track.Dispose();
          }
        recv.Dispose();
      }
      receiveStream.Dispose();
      receiveStream = null;
      foreach (var sender in pc.GetSenders())
      {
        if (sender.Track != null)
          sender.Track.Stop();
        sender.Dispose();
      }
      pc.Close();
      pc.Dispose();
      pc = null;
    }
  }

  protected void Log(string msg)
  {
    if (printLogs)
      Debug.Log(LogTag + msg);
  }

  protected MediaStream receiveStream;
  protected RTCPeerConnection BuildBasicPeerConnection(string remote)
  {
    RTCPeerConnection pc = null;
    receiveStream = new MediaStream();
    receiveStream.OnAddTrack = OnAddTrack;

    var configuration = WebRtcMain.config;
    pc = new RTCPeerConnection(ref configuration)
    {
      OnTrack = e => receiveStream.AddTrack(e.Track),
      OnIceCandidate = candidate => StartCoroutine(new put(remote, new Request(candidate))),
      //OnNegotiationNeeded = () => StartCoroutine(PrepareOffer(pc, remoteId)),
      OnIceGatheringStateChange = state => Log("Ice gathering state " + state),
      OnIceConnectionChange = state => Log("Ice connection change " + state),
      OnConnectionStateChange = state =>
      {
        Log("Connection state change " + state);
      },
      OnDataChannel = SetupDataChannel
    };
    return pc;
  }

  protected void SetupDataChannel(RTCDataChannel channel)
  {
    Log("opened data channel");
    this.data_channel = channel;
    this.data_channel.OnOpen = () => dataChannelOpen = true;
    this.data_channel.OnClose = () =>
    {
      dataChannelOpen = false;
      onMessage(null);
    };
    this.data_channel.OnMessage = onMessage;
  }

  public void Send(string msg) => this.data_channel?.Send(msg);
  public void Send(byte[] msg) => this.data_channel?.Send(msg);

  #region network connection
  protected interface IMsg
  {
    UnityWebRequest.Result Result { get; }
    bool IsOk { get; }
    Request answer { get; }
  }

  protected class put : CustomYieldInstruction, IMsg, IDisposable
  {
    UnityWebRequest www;
    public put(string id, Request data)
    {
      var url = WebRtcMain.Instance.HttpServerAddress + id;
      www = UnityWebRequest.Put(url, JsonUtility.ToJson(data));
      www.SendWebRequest();
    }

    public UnityWebRequest.Result Result { get; private set; }
    public Request answer => null;
    public bool IsOk { get; private set; }
    public override bool keepWaiting
    {
      get
      {
        if (www == null) return false;
        Result = www.result;
        if (Result == UnityWebRequest.Result.InProgress)
        {
          return true;
        }
        else if (Result == UnityWebRequest.Result.Success)
        {
          IsOk = true;
          Dispose();
          return false;
        }
        else
        {
          Dispose();
        }
        return false;
      }
    }

    public void Dispose()
    {
      www?.Dispose();
      www = null;
    }
  }

  protected class get : CustomYieldInstruction, IMsg, IDisposable
  {
    private UnityWebRequest www;
    private DateTime deadline;
    private Request msg;

    public UnityWebRequest.Result Result { get; private set; }
    public bool IsOk { get; private set; }
    public Request answer { get; private set; }

    public get(string id, float timeoutSeconds = 3)
    {
      var url = WebRtcMain.Instance.HttpServerAddress + id;
      www = UnityWebRequest.Get(url);
      www.SendWebRequest();
      Result = www.result;
      IsOk = false;
      deadline = DateTime.Now + TimeSpan.FromSeconds(timeoutSeconds);
    }
    public override bool keepWaiting
    {
      get
      {
        if (www == null) return false;
        Result = www.result;
        if (Result == UnityWebRequest.Result.InProgress)
        {
          return true;
        }
        else if (Result == UnityWebRequest.Result.Success)
        {
          IsOk = true;
          answer = JsonUtility.FromJson<Request>(www.downloadHandler.text);
          Dispose();
          return false;
        }
        else
        {
          if (DateTime.Now < deadline)
          {
            var url = www.url;
            Dispose();
            www = UnityWebRequest.Get(url);
            www.SendWebRequest();
            return true;
          }
        }
        return false;
      }
    }

    public void Dispose()
    {
      www?.Dispose();
      www = null;
    }
  }
  #endregion
}
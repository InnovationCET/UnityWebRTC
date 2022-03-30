using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class ReceiveVideo : MonoBehaviour
{
  [Header("Connection")]
  [SerializeField] string LogTag;
  [SerializeField] private AudioSource outputAudioSource;
  [SerializeField] private RawImage outputVideo;
  [SerializeField] private Material alternativeOutput;
  [SerializeField] private bool keepAlive;
  public string localId;
  public string remoteId;

  private RTCPeerConnection pc;
  private MediaStream receiveStream;
  private AudioStreamTrack audioTrack;
  private SignalChannel channel;
  private CancellationTokenSource cts;
  private bool call_alive;
  private ConcurrentQueue<(Action<object> callback, object cookie)> work = new ConcurrentQueue<(Action<object> callback, object cookie)>();

  private void Start()
  {
    LogTag = (string.IsNullOrWhiteSpace(LogTag) ? this.GetType().Name : LogTag.Trim()) + ": ";
    cts = new CancellationTokenSource();
  }

  private void OnDestroy()
  {
    cts.Cancel();
  }

  // stop signal channel and peer-connection on destroy
  // keep alive once started
  // reentrant ??
  [ContextMenu("Call")]
  public void Call()
  {
    SignalChannel.InitIfNeeded(WebRtcMain.Instance.HttpServerAddress);
    WebRtcMain.Instance.InitIfNeeded();
    localId ??= System.Net.Dns.GetHostName();

    cts = new CancellationTokenSource();

    // BUG: if can't reach the signaller, or remote isn't up , gives up the call :-()
    SignalChannel.Call(remoteId, localId, cts.Token)
    .ContinueWith(task =>
    {
      if (task.Status == TaskStatus.RanToCompletion)
      {
        this.channel = task.Result;
        Log("Call was answered; signal channel established");
        work.Enqueue((BuildPeerConnection, this.channel));
      }
      else
      {
        Debug.LogError("Failed to call the remote: " + remoteId);
        if (task.Exception?.InnerException != null)
          Debug.LogError(task.Exception.InnerException.Message);
      }
    });
  }

  IEnumerator call2()
  {
    while (true)
    {
      var local_channel = localId + "_" + Guid.NewGuid().ToString();
      var www = UnityWebRequest.Post(WebRtcMain.Instance.HttpServerAddress + remoteId, JsonUtility.ToJson(new Request { type = "connect", from = local_channel }));
      yield return www.SendWebRequest();
      if (www.result != UnityWebRequest.Result.Success)
      {
        yield return new WaitForSeconds(0.5f);
        continue;
      }
      while (true)
      {
        www = UnityWebRequest.Get(WebRtcMain.Instance.HttpServerAddress + local_channel);
        yield return www.SendWebRequest();
        if (www.result != UnityWebRequest.Result.Success)
        {
          yield return new WaitForSeconds(0.5f);
          continue;
        }
        if (www.responseCode == 200) break;
      }
      var response = JsonUtility.FromJson<Request>(www.downloadHandler.text);
      // if response != ok, delay and retry

      BuildPeerConnection(null);
      while (pc.ConnectionState == RTCPeerConnectionState.New || pc.ConnectionState == RTCPeerConnectionState.Connecting)
      {
        while (true)
        {
          www = UnityWebRequest.Get(WebRtcMain.Instance.HttpServerAddress + local_channel);
          yield return www.SendWebRequest();
          if (www.result != UnityWebRequest.Result.Success)
          {
            yield return new WaitForSeconds(0.5f);
            continue;
          }
          if (www.responseCode == 200) break;
        }
        // handle msg
      }
      if (!keepAlive)
        yield break; // we have no more responsibility

      // else, wait until we've disconnected
      while (pc.ConnectionState == RTCPeerConnectionState.Connected)
      {
        yield return null;
      }
    }
  }

  void BuildPeerConnection(object channel_)
  {
    var channel = channel_ as SignalChannel;
    var configuration = WebRtcMain.config;
    pc = new RTCPeerConnection(ref configuration)
    {
      OnIceCandidate = candidate => channel.send(new Request(candidate)),
      OnNegotiationNeeded = () => StartCoroutine(PrepareOffer(channel)),
      OnIceGatheringStateChange = state => Log("Ice gathering state " + state),
      OnIceConnectionChange = state => Log("Ice connection change " + state),
      OnConnectionStateChange = state =>
      {
        Log("Connection state change " + state);
        if (state == RTCPeerConnectionState.Failed ||
            state == RTCPeerConnectionState.Closed ||
            state == RTCPeerConnectionState.Disconnected)
          OnHangup();
      },
    };
    receiveStream = new MediaStream();
    receiveStream.OnAddTrack = e =>
    {
      Log("received track " + e.Track.Kind);
      if (e.Track is VideoStreamTrack video)
      {
        video.OnVideoReceived += tex =>
        {
          if (outputVideo != null) outputVideo.texture = tex;
          if (alternativeOutput != null) alternativeOutput.mainTexture = tex;
        };
      }
      if (e.Track is AudioStreamTrack audio)
      {
        outputAudioSource.SetTrack(audio);
        outputAudioSource.loop = true;
        outputAudioSource.Play();
      }
    };

    var vid1 = pc.AddTransceiver(TrackKind.Video);
    vid1.Direction = RTCRtpTransceiverDirection.RecvOnly;
    vid1.SetCodecPreferences(RTCRtpReceiver.GetCapabilities(TrackKind.Video).codecs);

    var audio1 = pc.AddTransceiver(TrackKind.Audio);
    audio1.Direction = RTCRtpTransceiverDirection.RecvOnly;
    audio1.SetCodecPreferences(RTCRtpSender.GetCapabilities(TrackKind.Audio).codecs);

    pc.OnTrack = e => receiveStream.AddTrack(e.Track); ;
  }

  private IEnumerator PrepareOffer(SignalChannel channel)
  {
    var op1 = pc.CreateOffer();
    yield return op1;
    if (op1.IsError)
    {
      Log("error: create offer");
      yield break;
    }
    var desc = op1.Desc;
    pc.SetLocalDescription(ref desc);
    Log("created the offer and set it as local. now sending it...");
    channel.send(desc);
    //UnityWebRequest.Post(remote, JsonUtility.ToJson(new Request(desc)));
  }

  private IEnumerator OnAnswer(RTCSessionDescription desc)
  {
    Log("got answer");
    var op1 = pc.SetRemoteDescription(ref desc);
    yield return op1;
    if (op1.IsError)
    {
      Log("error in setting answer " + op1.Error.errorType + " " + op1.Error.message);
      yield break;
    }
  }

  [ContextMenu("Hangup")]
  public void OnHangup()
  {
    if (pc != null)
    {
      var tracks = receiveStream.GetTracks().ToArray();
      foreach (var track in tracks)
      {
        track.Stop();
        receiveStream.RemoveTrack(track);
      }
      receiveStream.Dispose();
      receiveStream = null;
      pc.Close();
      pc = null;
      outputAudioSource.Stop();
      // erase the texture? disable the RawImage?
    }
  }

  private void Update()
  {
    while (work.TryDequeue(out var job))
      job.callback(job.cookie);

    while (pc != null && channel && channel.TryGet(out var msg))
    {
      if (msg == null)
        break; // end of connection - should hang up and make another call
      if (msg.type == "answer")
      {
        print("received answer");
        var desc = new RTCSessionDescription()
        {
          type = RTCSdpType.Answer,
          sdp = msg.sdp
        };
        StartCoroutine(OnAnswer(desc));
      }
      else if (msg.type == "candidate")
      {
        pc.AddIceCandidate((RTCIceCandidate)msg);
      }
      // else, unknown msg
    }
    if (pc != null && keepAlive &&
            (pc.ConnectionState == RTCPeerConnectionState.Failed ||
            pc.ConnectionState == RTCPeerConnectionState.Disconnected))
    {
      Call();
    }
  }

  void Log(string msg)
  {
    Debug.Log(LogTag + msg);
  }
}

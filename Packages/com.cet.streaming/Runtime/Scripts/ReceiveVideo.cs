using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

public class ReceiveVideo : MonoBehaviour
{
  [SerializeField] string LogTag;
  [SerializeField] private AudioSource outputAudioSource;
  [SerializeField] private RawImage outputVideo;
  public string localId;
  public string remoteId;

  private RTCPeerConnection pc;
  private MediaStream receiveStream;
  private AudioStreamTrack audioTrack;
  private SignalChannel channel;
  private CancellationTokenSource cts;
  private bool call_alive;
  private ConcurrentQueue<Action> work = new ConcurrentQueue<Action>();

  private void Start()
  {
    LogTag = (string.IsNullOrWhiteSpace(LogTag) ? this.GetType().Name : LogTag.Trim()) + ": ";
  }

  [ContextMenu("Call")]
  public void Call()
  {
    SignalChannel.InitIfNeeded(WebRtcMain.Instance.HttpServerAddress);
    WebRtcMain.Instance.InitIfNeeded();
    localId ??= System.Net.Dns.GetHostName();

    cts = new CancellationTokenSource();
    SignalChannel.Call(remoteId, localId, cts.Token)
    .ContinueWith(task =>
    {
      if (task.Status == TaskStatus.RanToCompletion)
      {
        this.channel = task.Result;
        Log("Call was answered; signal channel established");
        work.Enqueue(BuildPeerConnection);
      }
      else
      {
        Debug.LogError("Failed to call the remote: " + remoteId);
        if (task.Exception?.InnerException != null)
          Debug.LogError(task.Exception.InnerException.Message);
      }
    });
  }

  void BuildPeerConnection()
  {
    var configuration = WebRtcMain.config;
    pc = new RTCPeerConnection(ref configuration)
    {
      OnIceCandidate = candidate => channel.send(new Request(candidate)),
      OnNegotiationNeeded = () => StartCoroutine(PrepareOffer()),
      OnIceGatheringStateChange = state => Log("Ice gathering state " + state),
      OnIceConnectionChange = state => Log("Ice connection change " + state),
      OnConnectionStateChange = state =>
      {
        Log("Connection state change " + state);
        if (state == RTCPeerConnectionState.Connected)
        {
          cts.Cancel(); // no need for signal channel anymore
          cts = null;
        }
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
        video.OnVideoReceived += tex => outputVideo.texture = tex;
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

  private IEnumerator PrepareOffer()
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
      job();

    while (pc != null && channel && channel.TryGet(out var msg))
    {
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
  }
  void Log(string msg)
  {
    Debug.Log(LogTag + msg);
  }
}

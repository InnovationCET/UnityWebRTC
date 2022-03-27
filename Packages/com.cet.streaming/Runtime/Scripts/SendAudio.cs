using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

public class SendAudio : MonoBehaviour
{
  [SerializeField] string LogTag;
  [SerializeField] private AudioSource inputAudioSource;
  [SerializeField] private string micDeviceName;
  [Tooltip("Can leave empty")]
  [SerializeField] private string localId;

  public bool dataChannelOpen { get; private set; }
  public event Action<byte[]> OnMessage; // will be sent the bytes received, or null on close

  public string remoteId;
  public string SignalServerHttpAddress = "http://20.86.157.60:3000/";

  private RTCPeerConnection pc;
  private MediaStream sendStream;
  private AudioClip clipInput;
  private AudioStreamTrack audioTrack;
  private RTCDataChannel my_data_channel;
  private ConcurrentQueue<Action> work = new ConcurrentQueue<Action>();
  private List<RTCRtpCodecCapability> availableCodecs = new List<RTCRtpCodecCapability>();
  private SignalChannel channel;

  CancellationTokenSource cts;
  int m_samplingFrequency = 48000;
  int m_lengthSeconds = 1;

  void Init()
  {
    SignalChannel.InitIfNeeded(SignalServerHttpAddress);
    WebRtcMain.Instance.InitIfNeeded();
    LogTag = (string.IsNullOrWhiteSpace(LogTag) ? this.GetType().Name : LogTag.Trim()) + ": ";

    var codecs = RTCRtpSender.GetCapabilities(TrackKind.Audio).codecs;
    var excludeCodecTypes = new[] { "audio/CN", "audio/telephone-event" };
    foreach (var codec in codecs)
    {
      if (excludeCodecTypes.Count(type => codec.mimeType.Contains(type)) > 0)
        continue;
      availableCodecs.Add(codec);
    }
  }

  private void Start()
  {
    ListMicrophones();
  }

  void ListMicrophones()
  {
    foreach (var d in Microphone.devices)
      print(d);
  }

  [ContextMenu("Make the call")]
  private void StartCall()
  {
    Init();
    if (cts != null)
      cts.Cancel();
    cts = new CancellationTokenSource();
    SignalChannel.Call(remoteId, localId, cts.Token)
    .ContinueWith(task =>
    {
      this.channel = task.Result;
      print("Call was answered: signal channel established");
      work.Enqueue(StartMicrophone);
      work.Enqueue(BuildPeerConnection);
    }, TaskContinuationOptions.OnlyOnRanToCompletion);
    // if failed with exception or cancelled, what should I do???
  }

  void StartMicrophone()
  {
    clipInput = Microphone.Start(micDeviceName, true, m_lengthSeconds, m_samplingFrequency);
    // set the latency to “0” samples before the audio starts to play.
    while (!(Microphone.GetPosition(micDeviceName) > 0)) { }

    inputAudioSource.loop = true;
    inputAudioSource.clip = clipInput;
    inputAudioSource.Play();
  }

  void BuildPeerConnection()
  {
    sendStream = new MediaStream();
    var configuration = WebRtcMain.config;

    pc = new RTCPeerConnection(ref configuration)
    {
      OnIceCandidate = candidate => channel.send(new Request(candidate)),
      //OnDataChannel = data_channel => Log("The peer has created a data channel"),
      OnNegotiationNeeded = () => StartCoroutine(PeerNegotiationNeeded(pc)),
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

    my_data_channel = pc.CreateDataChannel("channel 1");
    my_data_channel.OnOpen = () => dataChannelOpen = true;
    my_data_channel.OnClose = () =>
    {
      dataChannelOpen = false;
      OnMessage?.Invoke(null);
      OnMessage = null;
    };
    my_data_channel.OnMessage = bytes => OnMessage?.Invoke(bytes);
    // to send message: my_data_channel.Send(string) or my_data_channel.Send(byte[])

    audioTrack = new AudioStreamTrack(inputAudioSource);
    pc.AddTrack(audioTrack, sendStream);
    var transceiver = pc.GetTransceivers().First();

    var error = transceiver.SetCodecPreferences(this.availableCodecs.ToArray());
    if (error != RTCErrorType.None)
      Debug.LogError(error);
  }

  public void Send(byte[] buffer)
  {
    my_data_channel?.Send(buffer);
  }

  public void Send(string text)
  {
    my_data_channel?.Send(text);
  }

  [ContextMenu("Pause")]
  public void OnPause()
  {
    var transceiver1 = pc.GetTransceivers().First();
    var track = transceiver1.Sender.Track;
    track.Enabled = false;
  }

  [ContextMenu("Resume")]
  public void OnResume()
  {
    var transceiver1 = pc.GetTransceivers().First();
    var track = transceiver1.Sender.Track;
    track.Enabled = true;
  }

  [ContextMenu("Hangup")]
  void OnHangup()
  {
    Microphone.End(micDeviceName);
    inputAudioSource.Stop();
    clipInput = null;
    cts?.Cancel();
    audioTrack?.Dispose();
    sendStream?.Dispose();
    my_data_channel?.Dispose();
    dataChannelOpen = false;
    OnMessage = null;
    pc?.Dispose();
    pc = null;
    my_data_channel = null;
    sendStream = null;
    audioTrack = null;
    cts = null;
  }

  IEnumerator PeerNegotiationNeeded(RTCPeerConnection pc)
  {
    Log("Starting negotiation");
    var op = pc.CreateOffer();
    yield return op;
    if (!op.IsError)
    {
      if (pc.SignalingState != RTCSignalingState.Stable)
        yield break;
      var desc = op.Desc;

      var op2 = pc.SetLocalDescription(ref desc);
      yield return op2;

      if (!op.IsError)
      {
        Log("sending the offer to the peer");
        channel.send(desc);
      }
      else
      {
        var error = op.Error;
        OnSetSessionDescriptionError(ref error);
      }
    }
    else
    {
      var error = op.Error;
      OnSetSessionDescriptionError(ref error);
    }
  }

  void OnSetSessionDescriptionError(ref RTCError error)
  {
    Debug.LogError($"{LogTag}: Error Detail Type: {error.message}");
  }

  protected void Update()
  {
    while (pc != null && channel && channel.TryGet(out var msg))
    {
      if (msg.type == "answer")
      {
        var desc = new RTCSessionDescription()
        {
          type = RTCSdpType.Answer,
          sdp = msg.sdp
        };
        pc.SetRemoteDescription(ref desc);
      }
      else if (msg.type == "candidate")
      {
        pc.AddIceCandidate((RTCIceCandidate)msg);
      }
    }
    while (work.TryDequeue(out var action))
      action();
  }

  void Log(string msg)
  {
    Debug.Log(LogTag + msg);
  }
}
